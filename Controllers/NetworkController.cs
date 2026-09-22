using ebs50_backend.DTOs;
using ebs50_backend.Services.Networking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ebs50_backend.Controllers;

/// <summary>Read-only inventory and diagnostics for the server's connected networks.</summary>
[ApiController]
[Route("api/network")]
public sealed class NetworkController(INetworkInterfaceService interfaces,
    INetworkDiagnosticsService diagnostics) : ControllerBase
{
    /// <summary>Lists local interfaces and candidate web URLs; does not prove remote reachability.</summary>
    [HttpGet("interfaces")]
    [ProducesResponseType(typeof(IReadOnlyList<NetworkInterfaceDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NetworkInterfaceDto>> GetInterfaces() => Ok(interfaces.GetInterfaces());

    /// <summary>Checks local HTTP and the configured EBS TCP port, without changing settings.</summary>
    /// <param name="cancellationToken">Cancellation when the caller disconnects.</param>
    /// <returns>Individual probe results, including failures and timeouts.</returns>
    [HttpPost("diagnostics")]
    [EnableRateLimiting("network-diagnostics")]
    [ProducesResponseType(typeof(NetworkDiagnosticResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<NetworkDiagnosticResult>> DiagnoseAsync(CancellationToken cancellationToken)
        => Ok(await diagnostics.DiagnoseAsync(cancellationToken));
}
