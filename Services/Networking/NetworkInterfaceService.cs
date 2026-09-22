using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ebs50_backend.DTOs;

namespace ebs50_backend.Services.Networking;

/// <summary>Reads local interfaces without modifying operating system settings.</summary>
public interface INetworkInterfaceService
{
    /// <summary>Returns interface inventory, including disconnected interfaces.</summary>
    IReadOnlyList<NetworkInterfaceDto> GetInterfaces();
}

public sealed class NetworkInterfaceService(IConfiguration configuration) : INetworkInterfaceService
{
    public IReadOnlyList<NetworkInterfaceDto> GetInterfaces()
    {
        var port = configuration.GetValue<int>("ServiceSettings:Port", 6789);
        var officeId = configuration["NetworkSettings:OfficeInterfaceId"];
        var ebsId = configuration["NetworkSettings:EbsInterfaceId"];

        var results = new List<NetworkInterfaceDto>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var properties = n.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(a.Address)).ToArray();
                var role = n.Id == officeId ? "Office"
                    : n.Id == ebsId ? "EBS" : "Unassigned";

                var gateways = properties.GatewayAddresses
                    .Where(g => g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString()).ToArray();

                var webUrls = n.OperationalStatus == OperationalStatus.Up
                    ? addresses.Where(a => !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        .Select(a => $"http://{a.Address}:{port}").ToArray()
                    : [];

                results.Add(new NetworkInterfaceDto(n.Id, n.Name, n.OperationalStatus.ToString(), role,
                    addresses.Select(a => $"{a.Address}/{a.PrefixLength}").ToArray(),
                    gateways,
                    webUrls));
            }
            catch (NetworkInformationException)
            {
                // Skip virtual or transient adapters that fail to query properties
            }
        }

        return results;
    }
}
