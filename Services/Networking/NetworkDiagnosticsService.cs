using System.Diagnostics;
using System.Net.Sockets;
using ebs50_backend.DTOs;
using ebs50_backend.Services.Dispatching;

namespace ebs50_backend.Services.Networking;

/// <summary>Probes only local web endpoints and the configured EBS station.</summary>
public interface INetworkDiagnosticsService
{
    /// <summary>Runs bounded probes. Caller cancellation is propagated.</summary>
    Task<NetworkDiagnosticResult> DiagnoseAsync(CancellationToken cancellationToken);
}

public sealed class NetworkDiagnosticsService(
    INetworkInterfaceService interfaces,
    IEbs50ConnectionSettingsProvider settingsProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<NetworkDiagnosticsService> logger) : INetworkDiagnosticsService
{
    public async Task<NetworkDiagnosticResult> DiagnoseAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        // Finish database access before starting concurrent network probes.
        ConnectionSettings settings;
        try { settings = await settingsProvider.GetAsync(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(DateTimeOffset.UtcNow, [new("Configuration", "EBS", "TimedOut", "Timeout",
                "Quá thời gian đọc cấu hình trạm.", 10000)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load EBS connection settings for diagnostics");
            return new(DateTimeOffset.UtcNow, [new("Configuration", "EBS", "Failed", "ConfigReadError",
                "Lỗi khi đọc cấu hình trạm từ cơ sở dữ liệu.", 0)]);
        }

        var snapshot = interfaces.GetInterfaces();
        using var concurrency = new SemaphoreSlim(4);
        var jobs = snapshot.SelectMany(n => n.WebUrls).Distinct()
            .Select(url => ProbeAsync("Local HTTP", url + "/health/live", TimeSpan.FromSeconds(5),
                async token =>
                {
                    using var response = await httpClientFactory.CreateClient("NetworkDiagnostics")
                        .GetAsync(url + "/health/live", HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    return (string?)null;
                }, concurrency, deadline.Token, cancellationToken)).ToList();
        jobs.Add(ProbeAsync("EBS TCP", $"{settings.Host}:{settings.Port}", TimeSpan.FromSeconds(3),
            async token =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(settings.Host, settings.Port, token);
                return client.Client.LocalEndPoint?.ToString();
            }, concurrency, deadline.Token, cancellationToken));
        var results = await Task.WhenAll(jobs);
        cancellationToken.ThrowIfCancellationRequested();
        return new NetworkDiagnosticResult(DateTimeOffset.UtcNow, results);
    }

    private async Task<NetworkProbeResult> ProbeAsync(string name, string target, TimeSpan timeout,
        Func<CancellationToken, Task<string?>> action, SemaphoreSlim concurrency,
        CancellationToken deadline, CancellationToken caller)
    {
        var elapsed = Stopwatch.StartNew();
        var entered = false;
        try
        {
            await concurrency.WaitAsync(deadline);
            entered = true;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            limit.CancelAfter(timeout);
            var source = await action(limit.Token);
            return new(name, target, "Passed", "Connected", "Kết nối thành công.", elapsed.ElapsedMilliseconds, source);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new(name, target, "TimedOut", "Timeout", "Quá thời gian chờ.", elapsed.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Network probe {Name} failed for {Target}", name, target);
            var socket = ex as SocketException ?? ex.InnerException as SocketException;
            var code = socket != null ? socket.SocketErrorCode.ToString()
                : ex is ArgumentException ? "InvalidConfiguration"
                : ex is HttpRequestException ? "HttpFailed"
                : "ProbeFailed";
            return new(name, target, "Failed", code, "Không kết nối được. Kiểm tra địa chỉ, dịch vụ và firewall.", elapsed.ElapsedMilliseconds);
        }
        finally { if (entered) concurrency.Release(); }
    }
}
