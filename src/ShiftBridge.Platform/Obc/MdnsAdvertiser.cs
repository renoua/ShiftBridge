using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

namespace ShiftBridge.Platform.Obc;

/// <summary>Annonce un service TCP sur le réseau local via mDNS (_openbikecontrol._tcp, _wahoo-fitness-tnp._tcp).</summary>
public sealed class MdnsAdvertiser : IMdnsAdvertiser
{
    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;

    /// <summary>
    /// Carte réseau principale : active, Ethernet ou Wi-Fi, MAC sur 6 octets, de préférence celle qui a une passerelle IPv4
    /// (donc celle réellement utilisée, pas une carte virtuelle Hyper-V / VPN).
    /// </summary>
    internal static NetworkInterface? PrimaryNic()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        && n.GetPhysicalAddress().GetAddressBytes().Length == 6)
            .ToList();
        return candidates.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses
                   .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
               ?? candidates.FirstOrDefault();
    }

    /// <param name="log">Diagnostic : sur quelle carte et quelles adresses le service est annoncé.</param>
    public void Advertise(string instanceName, string serviceType, ushort port, IDictionary<string, string> txt, Action<string>? log = null)
    {
        Stop();

        // Une seule carte et uniquement de l'IPv4 : annoncer toutes les cartes et toutes les adresses fait apparaître
        // plusieurs fois le même appareil dans les applis qui listent une entrée par adresse résolue.
        var nic = PrimaryNic();
        var addresses = nic?.GetIPProperties().UnicastAddresses
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        _mdns = nic is null
            ? new MulticastService()
            : new MulticastService(all => all.Where(n => n.Id == nic.Id));
        if (nic is not null) _mdns.UseIpv6 = false;

        _profile = addresses is { Count: > 0 }
            ? new ServiceProfile(instanceName, serviceType, port, addresses)
            : new ServiceProfile(instanceName, serviceType, port);
        foreach (var (k, v) in txt) _profile.AddProperty(k, v);
        _discovery = new ServiceDiscovery(_mdns);
        _discovery.Advertise(_profile);
        _mdns.Start();
        _discovery.Announce(_profile);

        log?.Invoke($"mDNS : « {instanceName} » annoncé sur {nic?.Name ?? "toutes les cartes"}"
                    + (addresses is { Count: > 0 } ? $" ({string.Join(", ", addresses)})" : ""));
    }

    public void Stop()
    {
        if (_discovery is not null && _profile is not null)
        {
            try { _discovery.Unadvertise(_profile); } catch { /* arrêt best-effort */ }
        }
        _discovery?.Dispose();
        _mdns?.Stop();
        _mdns?.Dispose();
        _discovery = null;
        _mdns = null;
        _profile = null;
    }

    public void Dispose() => Stop();
}
