using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using AirGlass.Support;

namespace AirGlass.Server;

/// <summary>
/// Tracks the PC's IPv4 address on the active Wi-Fi or Ethernet adapter,
/// i.e. the address a phone on the same network can reach.
/// </summary>
public sealed class LocalAddressMonitor : IDisposable
{
    /// <summary>Adapters that are not the PC's real network connection.</summary>
    private static readonly string[] VirtualAdapterHints =
    {
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker",
        "tap-", "tap adapter", "wireguard", "tunnel", "vpn", "loopback", "bluetooth",
    };

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly object _gate = new();
    private string? _address;
    private bool _started;

    /// <summary>Raised on the UI thread; <c>null</c> when no suitable adapter is up.</summary>
    public event Action<string?>? Changed;

    public void Start()
    {
        _started = true;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        Update();
    }

    public void Dispose()
    {
        if (!_started) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var address = LanAddress();
        lock (_gate)
        {
            if (address == _address && _address != null) return;
            _address = address;
        }
        Log.Server($"LAN address: {address ?? "none"}");
        if (_ui != null) _ui.Post(_ => Changed?.Invoke(address), null);
        else Changed?.Invoke(address);
    }

    private static string? LanAddress()
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        var candidates = adapters
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.Ethernet3Megabit)
            .Where(adapter => !IsVirtual(adapter))
            .Select(adapter =>
            {
                var properties = adapter.GetIPProperties();
                var ipv4 = properties.UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork
                        // Link-local addresses mean DHCP failed; nothing can reach them.
                        && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
                var hasGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && !gateway.Address.Equals(System.Net.IPAddress.Any));
                return (Address: ipv4?.ToString(), HasGateway: hasGateway);
            })
            .Where(candidate => candidate.Address != null)
            .ToList();

        // The adapter with a default gateway is the one the phone shares a
        // network with; host-only and similar adapters have none.
        return candidates.FirstOrDefault(c => c.HasGateway).Address ?? candidates.FirstOrDefault().Address;
    }

    private static bool IsVirtual(NetworkInterface adapter)
    {
        var text = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        return VirtualAdapterHints.Any(text.Contains);
    }
}
