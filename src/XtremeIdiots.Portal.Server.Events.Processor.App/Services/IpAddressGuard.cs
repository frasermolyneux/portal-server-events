using System.Net;
using System.Net.Sockets;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

/// <summary>
/// Guards against persisting or enriching non-routable placeholder IP addresses
/// (e.g. "0.0.0.0" / "::") that some event producers emit when a player's real IP is
/// unknown. Such values must be treated as "IP unknown" so they never overwrite a good
/// address, create bogus PlayerIpAddress rows, or trigger false alt-account linking.
/// </summary>
internal static class IpAddressGuard
{
    /// <summary>
    /// Returns true when the supplied value is a real, persistable IP address.
    /// Returns false for null/empty/whitespace, unparseable values, and non-routable
    /// placeholders (IPv4 0.0.0.0 / IPv6 :: "any" addresses).
    /// </summary>
    public static bool IsPersistable(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        if (!IPAddress.TryParse(ipAddress.Trim(), out var parsed))
        {
            return false;
        }

        // Reject the "any" placeholders emitted when a real IP was not captured.
        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        // Reject IPv4-mapped forms of the IPv4 "any" address (e.g. ::ffff:0.0.0.0).
        if (parsed.IsIPv4MappedToIPv6 && parsed.MapToIPv4().Equals(IPAddress.Any))
        {
            return false;
        }

        return parsed.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
    }
}
