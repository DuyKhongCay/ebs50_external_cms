using System.Net;
using System.Net.Sockets;
using ebs50_backend.DTOs;
using ebs50_backend.Services.Dispatching;
using ebs50_backend.Services.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ebs50_backend.Tests;

public sealed class NetworkDiagnosticsServiceTests
{
    private readonly NullLogger<NetworkDiagnosticsService> _logger = NullLogger<NetworkDiagnosticsService>.Instance;

    [Fact]
    public async Task DiagnoseAsync_WhenAllProbesSucceed_ReturnsPassedResults()
    {
        // Arrange
        using var tcpListener = StartLoopbackTcpListener(out var ebsPort);

        var interfaces = new StubNetworkInterfaceService(
        [
            new NetworkInterfaceDto(
                "nic-1",
                "Ethernet",
                "Up",
                "Office",
                ["127.0.0.1/24"],
                ["127.0.0.1"],
                ["http://127.0.0.1:6789"])
        ]);

        var settingsProvider = new StubConnectionSettingsProvider(new ConnectionSettings(
            "127.0.0.1", ebsPort, "root", "", "/home/root/ebs_50_run/Input"));

        var httpClientFactory = new StubHttpClientFactory((request, token) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(response);
        });

        var service = new NetworkDiagnosticsService(interfaces, settingsProvider, httpClientFactory, _logger);

        // Act
        var result = await service.DiagnoseAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Checks.Count);

        var httpProbe = result.Checks.FirstOrDefault(c => c.Name == "Local HTTP");
        Assert.NotNull(httpProbe);
        Assert.Equal("Passed", httpProbe.Status);
        Assert.Equal("Connected", httpProbe.Code);

        var tcpProbe = result.Checks.FirstOrDefault(c => c.Name == "EBS TCP");
        Assert.NotNull(tcpProbe);
        Assert.Equal("Passed", tcpProbe.Status);
        Assert.Equal("Connected", tcpProbe.Code);
        Assert.NotNull(tcpProbe.SourceAddress);
        Assert.Contains("127.0.0.1", tcpProbe.SourceAddress);
    }

    [Fact]
    public async Task DiagnoseAsync_WhenProbeExceedsTimeout_ReturnsTimedOut()
    {
        // Arrange
        var interfaces = new StubNetworkInterfaceService(
        [
            new NetworkInterfaceDto(
                "nic-1",
                "Ethernet",
                "Up",
                "Office",
                ["127.0.0.1/24"],
                [],
                ["http://127.0.0.1:6789"])
        ]);

        var settingsProvider = new StubConnectionSettingsProvider(new ConnectionSettings(
            "127.0.0.1", 1, "root", "", "/home/root/ebs_50_run/Input"));

        // Simulate a slow HTTP server that exceeds the 5-second probe timeout
        var httpClientFactory = new StubHttpClientFactory(async (request, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var service = new NetworkDiagnosticsService(interfaces, settingsProvider, httpClientFactory, _logger);

        // Act
        var result = await service.DiagnoseAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var httpProbe = result.Checks.FirstOrDefault(c => c.Name == "Local HTTP");
        Assert.NotNull(httpProbe);
        Assert.Equal("TimedOut", httpProbe.Status);
        Assert.Equal("Timeout", httpProbe.Code);
        Assert.Equal("Quá thời gian chờ.", httpProbe.Message);
    }

    [Fact]
    public async Task DiagnoseAsync_WhenCallerCancels_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled caller token

        var interfaces = new StubNetworkInterfaceService([]);
        var settingsProvider = new StubConnectionSettingsProvider(new ConnectionSettings(
            "127.0.0.1", 22, "root", "", "/home/root/ebs_50_run/Input"));
        var httpClientFactory = new StubHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var service = new NetworkDiagnosticsService(interfaces, settingsProvider, httpClientFactory, _logger);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.DiagnoseAsync(cts.Token);
        });
    }

    [Fact]
    public async Task DiagnoseAsync_WhenOneProbeFails_DoesNotLoseOtherProbeResults()
    {
        // Arrange
        // Use a closed port on loopback where no TCP listener is active (port 1 or an ephemeral closed port)
        var closedPort = GetUnusedLoopbackPort();

        var interfaces = new StubNetworkInterfaceService(
        [
            new NetworkInterfaceDto(
                "nic-1",
                "Ethernet",
                "Up",
                "Office",
                ["127.0.0.1/24"],
                [],
                ["http://127.0.0.1:6789"])
        ]);

        var settingsProvider = new StubConnectionSettingsProvider(new ConnectionSettings(
            "127.0.0.1", closedPort, "root", "", "/home/root/ebs_50_run/Input"));

        // HTTP probe succeeds
        var httpClientFactory = new StubHttpClientFactory((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var service = new NetworkDiagnosticsService(interfaces, settingsProvider, httpClientFactory, _logger);

        // Act
        var result = await service.DiagnoseAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Checks.Count);

        var httpProbe = result.Checks.FirstOrDefault(c => c.Name == "Local HTTP");
        Assert.NotNull(httpProbe);
        Assert.Equal("Passed", httpProbe.Status);

        var tcpProbe = result.Checks.FirstOrDefault(c => c.Name == "EBS TCP");
        Assert.NotNull(tcpProbe);
        Assert.Equal("Failed", tcpProbe.Status);
        Assert.Equal("ConnectionRefused", tcpProbe.Code);
    }

    #region Helper Types & Stubs

    private static TcpListener StartLoopbackTcpListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Background accept loop to immediately accept connections and discard them
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    client.Dispose();
                }
            }
            catch (SocketException)
            {
                // Listener was stopped
            }
            catch (ObjectDisposedException)
            {
                // Listener was disposed
            }
        });

        return listener;
    }

    private static int GetUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubNetworkInterfaceService(IReadOnlyList<NetworkInterfaceDto> items) : INetworkInterfaceService
    {
        public IReadOnlyList<NetworkInterfaceDto> GetInterfaces() => items;
    }

    private sealed class StubConnectionSettingsProvider(ConnectionSettings settings) : IEbs50ConnectionSettingsProvider
    {
        public Task<ConnectionSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);
    }

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new DelegateHandler(handlerFunc));
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handlerFunc(request, cancellationToken);
    }

    #endregion
}
