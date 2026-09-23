using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace PresenterAi.Infrastructure.Tools;

public sealed class OutboundGuardException(string code) : HttpRequestException(code)
{
    public string Code { get; } = code;
}

public interface IOutboundAddressPolicy
{
    bool IsAllowed(IPAddress address);
}

public sealed class StrictOutboundAddressPolicy : IOutboundAddressPolicy
{
    public bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsAllowed(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            if (a == 0 || a == 10 || a == 127 || a >= 224 ||
                a == 100 && b is >= 64 and <= 127 ||
                a == 169 && b == 254 || a == 172 && b is >= 16 and <= 31 ||
                a == 192 && (b == 168 || b == 0 || b == 88 && c == 99) ||
                a == 198 && (b is 18 or 19 || b == 51 && c == 100) ||
                a == 203 && b == 0 && c == 113 ||
                a == 192 && b == 0 && c == 2 ||
                a == 168 && b == 63 && c == 129 && bytes[3] == 16)
                return false;
            // IANA special-use blocks, including protocol assignments and limited broadcast.
            if (a == 192 && b == 0 || a == 192 && b == 0 && c == 0 ||
                a == 192 && b == 175 && c == 48 || a == 192 && b == 31 && c == 196 ||
                a == 192 && b == 52 && c == 193 || a == 192 && b == 168 ||
                a == 198 && b == 19)
                return false;
            return true;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (IPAddress.IPv6None.Equals(address) || IPAddress.IPv6Loopback.Equals(address) ||
            (bytes[0] & 0xfe) == 0xfc || bytes[0] == 0xff ||
            bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80 ||
            bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00 ||
            bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8 ||
            bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x02 ||
            bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x10)
            return false;
        // Well-known NAT64 and 6to4 encode an IPv4 target.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b &&
            bytes.AsSpan(4, 8).SequenceEqual(new byte[8]))
            return IsAllowed(new IPAddress(bytes.AsSpan(12, 4)));
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsAllowed(new IPAddress(bytes.AsSpan(2, 4)));
        // IPv4-compatible and IPv4-translated forms are not routable public destinations.
        if (bytes.AsSpan(0, 12).SequenceEqual(new byte[12])) return false;
        return bytes[0] is >= 0x20 and <= 0x3f;
    }
}

public interface IOutboundDnsResolver
{
    Task<IPAddress[]> ResolveAllAsync(string host, CancellationToken cancellationToken);
    Task<IPAddress[]> ResolveAAsync(string host, CancellationToken cancellationToken);
}

public sealed class OutboundDnsResolver : IOutboundDnsResolver
{
    public Task<IPAddress[]> ResolveAllAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
    public Task<IPAddress[]> ResolveAAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken);
}

public interface ISocketConnector
{
    ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken);
}

public sealed class SocketConnector : ISocketConnector
{
    public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public static class OutboundUrlValidator
{
    public static async Task<Uri> ValidateForSaveAsync(string url, IOutboundAddressPolicy policy,
        IOutboundDnsResolver resolver, CancellationToken cancellationToken = default)
    {
        var uri = Validate(url, policy);
        if (!IPAddress.TryParse(uri.Host, out _))
        {
            var all = await resolver.ResolveAllAsync(uri.Host, cancellationToken);
            var a = await resolver.ResolveAAsync(uri.Host, cancellationToken);
            if (all.Length == 0 || all.Concat(a).Any(address => !policy.IsAllowed(address)))
                throw new OutboundGuardException("tools_url_blocked");
        }
        return uri;
    }

    public static Uri Validate(string url, IOutboundAddressPolicy policy)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Length == 0)
            throw new OutboundGuardException("tools_url_invalid");
        if (IPAddress.TryParse(uri.Host, out var address) && !policy.IsAllowed(address))
            throw new OutboundGuardException("tools_url_blocked");
        return uri;
    }
}

public sealed class GuardedConnectCallback(IOutboundAddressPolicy policy, IOutboundDnsResolver resolver, ISocketConnector connector)
{
    public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken) =>
        ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] all;
        IPAddress[] a;
        if (IPAddress.TryParse(host, out var literal))
        {
            all = [literal];
            a = literal.AddressFamily == AddressFamily.InterNetwork ? [literal] : [];
        }
        else
        {
            all = await resolver.ResolveAllAsync(host, cancellationToken);
            a = await resolver.ResolveAAsync(host, cancellationToken);
        }
        if (all.Length == 0 || all.Concat(a).Any(address => !policy.IsAllowed(address)))
            throw new OutboundGuardException("tools_url_blocked");
        return await connector.ConnectAsync(all[0], port, cancellationToken);
    }
}
