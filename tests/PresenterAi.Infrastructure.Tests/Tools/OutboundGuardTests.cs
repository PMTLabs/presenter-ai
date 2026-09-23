using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using PresenterAi.Infrastructure.Tools;
using Xunit;

namespace PresenterAi.Infrastructure.Tests.Tools;

public class OutboundGuardTests
{
    [Theory]
    [InlineData("0.0.0.0", false)] [InlineData("0.1.2.3", false)]
    [InlineData("10.0.0.1", false)] [InlineData("10.255.255.255", false)]
    [InlineData("100.64.0.1", false)] [InlineData("100.100.100.200", false)]
    [InlineData("127.0.0.1", false)] [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)] [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)] [InlineData("168.63.129.16", false)]
    [InlineData("192.0.0.1", false)] [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)] [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.2", false)] [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)] [InlineData("255.255.255.255", false)]
    [InlineData("8.8.8.8", true)] [InlineData("1.1.1.1", true)]
    [InlineData("172.15.255.255", true)] [InlineData("172.32.0.1", true)]
    [InlineData("100.63.0.1", true)] [InlineData("100.128.0.1", true)]
    [InlineData("::", false)] [InlineData("::1", false)]
    [InlineData("fc00::1", false)] [InlineData("fd00:ec2::254", false)]
    [InlineData("fe80::1", false)] [InlineData("ff02::1", false)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)] // Teredo
    [InlineData("2001:db8::1", false)] [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a9fe:a9fe", false)] [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:a9fe:a9fe::1", false)] [InlineData("2002:0808:0808::1", true)]
    [InlineData("2606:4700:4700::1111", true)] [InlineData("2001:4860:4860::8888", true)]
    [InlineData("192.88.99.1", false)] [InlineData("198.19.1.1", false)]
    public void Address_table(string address, bool expected) =>
        Assert.Equal(expected, new StrictOutboundAddressPolicy().IsAllowed(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://example.com/")] [InlineData("https://user:pass@example.com/")]
    [InlineData("https://example.com/#fragment")] [InlineData("https://127.0.0.1/")]
    public void Invalid_or_blocked_url(string url) =>
        Assert.Throws<OutboundGuardException>(() => OutboundUrlValidator.Validate(url, new StrictOutboundAddressPolicy()));

    [Theory]
    [InlineData("169.254.169.254")] [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    public async Task Dns64_a_record_refused(string blocked)
    {
        var resolver = new FakeResolver([IPAddress.Parse("2001:db8:64::a9fe:a9fe")], [IPAddress.Parse(blocked)]);
        var connector = new FakeConnector();
        var callback = new GuardedConnectCallback(new AllowV6Policy(), resolver, connector);
        await Assert.ThrowsAsync<OutboundGuardException>(async () => await callback.ConnectAsync("test.example", 443, default));
        Assert.Equal(0, connector.Calls);
    }

    [Fact]
    public async Task Save_resolves_both_record_sets()
    {
        var resolver = new FakeResolver([IPAddress.Parse("8.8.8.8")], [IPAddress.Loopback]);
        var error = await Assert.ThrowsAsync<OutboundGuardException>(() =>
            OutboundUrlValidator.ValidateForSaveAsync("https://test.example/", new StrictOutboundAddressPolicy(), resolver));
        Assert.Equal("tools_url_blocked", error.Code);
    }

    [Fact]
    public async Task Mixed_public_private_host_refused()
    {
        var connector = new FakeConnector();
        var callback = new GuardedConnectCallback(new StrictOutboundAddressPolicy(),
            new FakeResolver([IPAddress.Parse("8.8.8.8"), IPAddress.Loopback], [IPAddress.Parse("8.8.8.8")]), connector);
        await Assert.ThrowsAsync<OutboundGuardException>(async () => await callback.ConnectAsync("test.example", 443, default));
        Assert.Equal(0, connector.Calls);
    }

    [Fact]
    public async Task Approved_address_port_and_token_reach_connector()
    {
        var connector = new FakeConnector();
        using var cts = new CancellationTokenSource();
        var address = IPAddress.Parse("8.8.8.8");
        var callback = new GuardedConnectCallback(new StrictOutboundAddressPolicy(),
            new FakeResolver([address], [address]), connector);
        await callback.ConnectAsync("test.example", 443, cts.Token);
        Assert.Equal(address, connector.Address);
        Assert.Equal(443, connector.Port);
        Assert.Equal(cts.Token, connector.Token);
    }

    [Fact]
    public async Task Public_dns64_control_allowed()
    {
        var connector = new FakeConnector();
        var callback = new GuardedConnectCallback(new AllowV6Policy(),
            new FakeResolver([IPAddress.Parse("2001:db8:64::808:808")], [IPAddress.Parse("8.8.8.8")]), connector);
        await callback.ConnectAsync("test.example", 443, default);
        Assert.Equal(1, connector.Calls);
    }

    [Fact]
    public async Task Metadata_redirect_to_blocked_host_refused()
    {
        using var client = Client(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        { Headers = { Location = new Uri("https://127.0.0.1/metadata") } }), 65536);
        var error = await Assert.ThrowsAsync<OutboundGuardException>(() => OutboundMetadata.GetAsync(client, "https://public.example/metadata", new StrictOutboundAddressPolicy()));
        Assert.Equal("tools_url_blocked", error.Code);
    }

    [Fact]
    public async Task Metadata_does_not_inherit_credentials()
    {
        using var client = Client(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), 65536);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "placeholder");
        await Assert.ThrowsAsync<OutboundGuardException>(() => OutboundMetadata.GetAsync(client, "https://public.example/", new StrictOutboundAddressPolicy()));
    }

    [Fact]
    public async Task Fourth_metadata_redirect_refused()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.Found)
        { Headers = { Location = new Uri($"https://public.example/{request.RequestUri!.AbsolutePath.Length}") } });
        using var client = Client(handler, 65536);
        var error = await Assert.ThrowsAsync<OutboundGuardException>(() => OutboundMetadata.GetAsync(client, "https://public.example/start", new StrictOutboundAddressPolicy()));
        Assert.Equal("tools_redirect_refused", error.Code);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task Oversize_body_cut_even_without_content_length()
    {
        using var client = Client(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(new byte[65537])) }), 65536);
        var error = await Assert.ThrowsAsync<OutboundGuardException>(() => client.GetAsync("https://public.example/"));
        Assert.Equal("tools_response_too_large", error.Code);
    }

    [Fact]
    public void Named_clients_and_shared_handler_are_guarded()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalTools();
        using var provider = services.BuildServiceProvider();
        Assert.IsType<StrictOutboundAddressPolicy>(provider.GetRequiredService<IOutboundAddressPolicy>());
        var handler = provider.GetRequiredService<SocketsHttpHandler>();
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        Assert.NotNull(factory.CreateClient("mcp"));
        Assert.NotNull(factory.CreateClient("mcp-oauth"));
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("mcp-oauth")]
    public async Task Named_client_actual_pipeline_blocks_dns_refuses_redirect_caps_response_and_disables_proxy(string name)
    {
        await using var fake = await StrictFakeAuthServer.StartAsync();
        var port = new Uri(fake.Url).Port;
        var connector = new LoopbackSocketConnector();
        var resolver = new MutableResolver();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOutboundDnsResolver>(resolver);
        services.AddSingleton<ISocketConnector>(connector);
        services.AddExternalTools();
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<SocketsHttpHandler>();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        var url = $"https://public.example:{port}";
        resolver.Address = IPAddress.Loopback;
        var blocked = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/guard-ok"));
        Assert.Equal("tools_url_blocked", Assert.IsType<OutboundGuardException>(blocked.InnerException).Code);
        Assert.Equal(0, connector.Calls);
        var approvedAddress = IPAddress.Parse("8.8.8.8");
        resolver.Address = approvedAddress;
        using var ok = await client.GetAsync(url + "/guard-ok");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, connector.Calls);
        Assert.Equal(approvedAddress, connector.DialedAddress);
        Assert.Equal(port, connector.DialedPort);

        resolver.Addresses = [approvedAddress, IPAddress.Loopback];
        using var mixedRequest = new HttpRequestMessage(HttpMethod.Get, $"https://mixed.example:{port}/guard-ok");
        mixedRequest.Headers.ConnectionClose = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(mixedRequest));
        Assert.Equal(1, connector.Calls);
        resolver.Addresses = [approvedAddress];
        var refused = await Assert.ThrowsAsync<OutboundGuardException>(() => client.GetAsync(url + "/guard-redirect"));
        Assert.Equal("tools_redirect_refused", refused.Code);
        Assert.Equal(1, connector.Calls);
        var error = await Assert.ThrowsAsync<OutboundGuardException>(() => client.GetAsync(url + "/guard-oversize"));
        Assert.Equal("tools_response_too_large", error.Code);
        Assert.Equal(1, connector.Calls);
    }

    private sealed class MutableResolver : IOutboundDnsResolver
    {
        public IPAddress[] Addresses { get; set; } = [IPAddress.Loopback];
        public IPAddress Address { get => Addresses[0]; set => Addresses = [value]; }
        public Task<IPAddress[]> ResolveAllAsync(string host, CancellationToken ct) => Task.FromResult(Addresses);
        public Task<IPAddress[]> ResolveAAsync(string host, CancellationToken ct) => Task.FromResult(Addresses);
    }

    private sealed class LoopbackSocketConnector : ISocketConnector
    {
        public int Calls;
        public IPAddress? DialedAddress;
        public int DialedPort;
        public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            DialedAddress = address;
            DialedPort = port;
            var socket = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, port, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    private static HttpClient Client(HttpMessageHandler handler, long cap) => new(new OutboundRequestHandler(new StrictOutboundAddressPolicy(), cap) { InnerHandler = handler });

    private sealed class FakeResolver(IPAddress[] all, IPAddress[] a) : IOutboundDnsResolver
    {
        public Task<IPAddress[]> ResolveAllAsync(string host, CancellationToken cancellationToken) => Task.FromResult(all);
        public Task<IPAddress[]> ResolveAAsync(string host, CancellationToken cancellationToken) => Task.FromResult(a);
    }
    private sealed class AllowV6Policy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ||
            new StrictOutboundAddressPolicy().IsAllowed(address);
    }
    private sealed class FakeConnector : ISocketConnector
    {
        public int Calls { get; private set; }
        public IPAddress? Address { get; private set; }
        public int Port { get; private set; }
        public CancellationToken Token { get; private set; }
        public ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            Calls++;
            Address = address;
            Port = port;
            Token = cancellationToken;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
