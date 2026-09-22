using System.Net;

namespace ebs50_backend.Services.Database;

/// <summary>Local-only maintenance access and scoped leases for all other dynamic requests.</summary>
public sealed class DatabaseMaintenanceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, DatabaseMaintenanceCoordinator coordinator, IServiceScopeFactory scopes)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/database") || path.Equals("/Database", StringComparison.OrdinalIgnoreCase))
        {
            var remote = context.Connection.RemoteIpAddress;
            var host = context.Request.Host.Host.Trim('[', ']');
            bool localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
            // Do not trust forwarded client IPs or allow DNS rebinding to a LAN hostname.
            if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
                || !localHost || context.Request.Headers.ContainsKey("Forwarded")
                || context.Request.Headers.Keys.Any(key => key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Mở chức năng này trên máy server qua http://localhost:<port>/Database." }, context.RequestAborted);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
            return;
        }
        if (path.Equals("/health/live", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        using var lease = coordinator.TryEnter();
        if (lease == null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new { error = "Database đang bảo trì. Vui lòng thử lại sau." }, context.RequestAborted);
            return;
        }
        // ASP.NET normally disposes request services after middleware returns. Own the downstream scope
        // explicitly so every DbContext/connection is disposed BEFORE the lease is released.
        var originalServices = context.RequestServices;
        await using var scope = scopes.CreateAsyncScope();
        context.RequestServices = scope.ServiceProvider;
        try { await next(context); }
        finally { context.RequestServices = originalServices; }
    }
}
