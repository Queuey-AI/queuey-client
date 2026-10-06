using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Queuey.Client.Waas;

// Queuey F2.3 (2026-10-06): en deploy-fil som peker på http://localhost:3000, ble lagret, og hver levering ble stoppet av
// egress-vakten etterpå. Serveren avviser nå en slik URL når den skrives (DeliveryDestinationRules), og CLI-en sier det
// før noe er sendt, med samme regler: navnet localhost og alt under det, og adressene vakten aldri slipper gjennom
// (SsrfEgressPolicy i Queuey). Unntakene en selv-hostet Queuey har (Delivery:Egress), ser CLI-en ikke, så sjekken står over
// når API-et selv kjører på maskinen eller et privat nett: der kan leveringen nå det.

/// <summary>
/// The delivery destinations Queuey's delivery never reaches: a URL on this machine (<c>localhost</c>, <c>*.localhost</c>,
/// a loopback address) or at a private or reserved address. A service on the developer's machine is reached through a
/// local listener instead: <c>"kind": "localForward"</c> and <c>queuey listen</c>.
/// </summary>
public static class DeploymentDestinations
{
    /// <summary>
    /// Why <paramref name="url"/> cannot be a delivery destination, or null when it can. A relative path is appended to the
    /// workspace's base and checked there; a <c>${VAR}</c> is checked once expanded.
    /// </summary>
    public static string? LocalTargetRefusal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url!.IndexOf("${", StringComparison.Ordinal) >= 0
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        string? where = Where(uri);
        return where is null
            ? null
            : $"it points at {uri.Host}, which is {where}, and Queuey's delivery never reaches it. To deliver to a service on " +
              "your machine, set \"kind\": \"localForward\" on the queue's delivery and run queuey listen --queue <name> " +
              "--forward-to <address>: the address belongs to the session, not to this file.";
    }

    /// <summary>True when <paramref name="apiBase"/> runs on this machine or a private network, where delivery can reach it too.</summary>
    public static bool IsLocal(Uri? apiBase) => apiBase is not null && apiBase.IsAbsoluteUri && Where(apiBase) is not null;

    /// <summary>
    /// Throws <see cref="QueueyConfigurationException"/> naming every delivery URL in the <paramref name="expanded"/> file
    /// that Queuey's delivery never reaches, before anything is sent; Queuey refuses the write too. Not when the API at
    /// <paramref name="apiBase"/> runs on this machine or a private network: a self-hosted Queuey there may be allowed to
    /// reach it, which only the server knows.
    /// </summary>
    public static void EnsureReachable(DeploymentFile expanded, Uri apiBase)
    {
        if (expanded is null) throw new ArgumentNullException(nameof(expanded));
        if (IsLocal(apiBase))
            return;

        IReadOnlyList<string> problems = expanded.LocalDestinationProblems();
        if (problems.Count > 0)
            throw new QueueyConfigurationException(
                (problems.Count == 1 ? "A delivery URL Queuey can't reach, so nothing was sent: " : "Delivery URLs Queuey can't reach, so nothing was sent: ")
                + string.Join("; ", problems))
            {
                SuggestedAction = "Deliver to a service on your machine through a local listener: \"kind\": \"localForward\" on the queue's delivery, and queuey listen.",
            };
    }

    private static string? Where(Uri uri)
    {
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            string literal = uri.Host.StartsWith("[", StringComparison.Ordinal) && uri.Host.EndsWith("]", StringComparison.Ordinal)
                ? uri.Host.Substring(1, uri.Host.Length - 2)
                : uri.Host;
            if (!IPAddress.TryParse(literal, out IPAddress? ip))
                return null;
            IPAddress address = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            if (IPAddress.IsLoopback(address))
                return "on this machine";
            return IsBlocked(address) ? "a private or reserved address" : null;
        }

        string name = uri.Host.TrimEnd('.');
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            ? "on this machine"
            : null;
    }

    // Samme områder som SsrfEgressPolicy.IsBlockedIp i Queuey etter #432 og #433 (2026-10-06), med de samme testvektorene
    // (DeploymentDestinationsTests her, SsrfEgressPolicyTests i Queuey). Serveren avviser uansett når URL-en lagres, men
    // CLI-en sier det før noe er sendt. En IPv4-mappet adresse er pakket ut før denne kalles (Where).
    private static bool IsBlocked(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            uint a = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return InRange(a, 0x00000000, 8)      // 0.0.0.0/8
                || InRange(a, 0x0A000000, 8)      // 10.0.0.0/8
                || InRange(a, 0x64400000, 10)     // 100.64.0.0/10 CGNAT, also 100.100.100.200 metadata
                || InRange(a, 0x7F000000, 8)      // 127.0.0.0/8
                || InRange(a, 0xA83F8110, 32)     // 168.63.129.16 Azure WireServer
                || InRange(a, 0xA9FE0000, 16)     // 169.254.0.0/16 link-local and metadata
                || InRange(a, 0xAC100000, 12)     // 172.16.0.0/12
                || InRange(a, 0xC0000000, 24)     // 192.0.0.0/24
                || InRange(a, 0xC0000200, 24)     // 192.0.2.0/24 TEST-NET-1
                || InRange(a, 0xC0586300, 24)     // 192.88.99.0/24 6to4 relay
                || InRange(a, 0xC0A80000, 16)     // 192.168.0.0/16
                || InRange(a, 0xC6120000, 15)     // 198.18.0.0/15
                || InRange(a, 0xC6336400, 24)     // 198.51.100.0/24 TEST-NET-2
                || InRange(a, 0xCB007100, 24)     // 203.0.113.0/24 TEST-NET-3
                || InRange(a, 0xE0000000, 4)      // 224.0.0.0/4 multicast
                || InRange(a, 0xF0000000, 4);     // 240.0.0.0/4 reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Bare 2000::/3 er global unicast. Utenfor ligger ::, ::1, IPv4-kompatibel ::/96, NAT64 64:ff9b::/96 og
            // 64:ff9b:1::/48, unik-lokal fc00::/7, link-lokal, site-lokal og multicast. Et prefiks som bærer en IPv4-adresse,
            // sperres helt, som i Queuey.
            if ((b[0] & 0xE0) != 0x20)
                return true;
            return (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0x00)        // 2001::/23, Teredo among them
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // 2001:db8::/32 documentation
                || (b[0] == 0x20 && b[1] == 0x02)                                // 2002::/16 6to4
                || (b[0] == 0x3F && b[1] == 0xFE)                                // 3ffe::/16 6bone
                || (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00);      // 3fff::/20 documentation
        }

        return true;
    }

    private static bool InRange(uint address, uint network, int prefix)
        => prefix == 0 || (address >> (32 - prefix)) == (network >> (32 - prefix));
}
