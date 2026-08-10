using System.Net;
using System.Net.Sockets;

namespace AirPlayReceiver.Services;

/// <summary>
/// Creates listeners that accept both IPv6 and IPv4 traffic on a single
/// dual-mode socket.
///
/// The mDNS advertisement publishes both A (IPv4) and AAAA (IPv6) records, and
/// iOS frequently prefers the receiver's IPv6 link-local address when opening
/// the RTSP/media connections. A socket bound to 0.0.0.0 only accepts IPv4, so
/// the receiver would show up in the Screen Mirroring picker but fail to
/// connect for those devices. Binding to [::] with <see cref="Socket.DualMode"/>
/// enabled accepts both families on every interface — Wi-Fi, Ethernet, and the
/// Windows Mobile Hotspot adapter alike.
///
/// Falls back to IPv4-only sockets on machines where IPv6 is disabled.
/// </summary>
public static class DualStackSockets
{
    /// <summary>TCP listener on all interfaces, IPv6 + IPv4. Pass port 0 for an OS-assigned port.</summary>
    public static TcpListener CreateTcpListener(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                return listener;
            }
            catch (SocketException) { /* IPv6 stack unavailable — fall through */ }
        }
        return new TcpListener(IPAddress.Any, port);
    }

    /// <summary>Bound UDP socket on all interfaces, IPv6 + IPv4. Pass port 0 for an OS-assigned port.</summary>
    public static UdpClient CreateUdpClient(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var udp = new UdpClient(AddressFamily.InterNetworkV6);
                udp.Client.DualMode = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                return udp;
            }
            catch (SocketException) { /* IPv6 stack unavailable — fall through */ }
        }
        return new UdpClient(port);
    }
}
