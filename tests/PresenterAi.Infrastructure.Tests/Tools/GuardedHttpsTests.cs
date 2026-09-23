using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PresenterAi.Infrastructure.Tools;
using Xunit;

namespace PresenterAi.Infrastructure.Tests.Tools;

public class GuardedHttpsTests
{
    [Theory]
    [InlineData("mcp", "X-Test-Key", 307)]
    [InlineData("mcp", "Authorization", 307)]
    [InlineData("mcp-oauth", "registration", 307)]
    [InlineData("mcp-oauth", "token", 308)]
    public async Task Credentials_are_never_replayed_to_another_real_https_origin(string name, string kind, int redirect)
    {
        using var cert = NewCertificate();
        await using var other = await HttpsOrigin.StartAsync(cert, _ => Results.Ok());
        await using var first = await HttpsOrigin.StartAsync(cert, _ => Results.Redirect(other.Url + "/stolen", permanent: redirect == 308, preserveMethod: true));
        using var provider = BuildServices(cert);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var request = new HttpRequestMessage(HttpMethod.Post, first.Url + "/entry");
        if (kind is "registration" or "token") request.Content = new StringContent("body-sentinel");
        else request.Headers.TryAddWithoutValidation(kind, kind == "Authorization" ? "Bearer token-sentinel" : "header-sentinel");
        var error = await Assert.ThrowsAsync<OutboundGuardException>(() => client.SendAsync(request));
        Assert.Equal("tools_redirect_refused", error.Code);
        Assert.Equal(1, first.Count);
        Assert.Equal(0, other.Count);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task Metadata_get_follows_at_most_three_real_https_redirects(int redirects, bool succeeds)
    {
        using var cert = NewCertificate();
        HttpsOrigin? first = null;
        HttpsOrigin? second = null;
        await using (second = await HttpsOrigin.StartAsync(cert, context =>
        {
            var n = int.Parse(context.Request.Query["n"].ToString());
            return n == redirects ? Results.Text("metadata") : Results.Redirect(first!.Url + $"/?n={n + 1}");
        }))
        await using (first = await HttpsOrigin.StartAsync(cert, context =>
        {
            var n = int.Parse(context.Request.Query["n"].ToString());
            return n == redirects ? Results.Text("metadata") : Results.Redirect(second.Url + $"/?n={n + 1}");
        }))
        {
            using var provider = BuildServices(cert);
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("mcp-oauth");
            if (succeeds)
            {
                using var response = await OutboundMetadata.GetAsync(client, first.Url + "/?n=0", new LoopbackPolicy());
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("metadata", await response.Content.ReadAsStringAsync());
            }
            else
            {
                var error = await Assert.ThrowsAsync<OutboundGuardException>(() =>
                    OutboundMetadata.GetAsync(client, first.Url + "/?n=0", new LoopbackPolicy()));
                Assert.Equal("tools_redirect_refused", error.Code);
            }
            Assert.Equal(4, first.Count + second.Count);
            Assert.True(first.Count > 0 && second.Count > 0);
        }
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("mcp-oauth")]
    public async Task Named_clients_emit_no_httpclient_logs_or_sensitive_values(string name)
    {
        using var cert = NewCertificate();
        await using var origin = await HttpsOrigin.StartAsync(cert, _ => Results.Ok());
        var capture = new CapturingLoggerProvider();
        using var provider = BuildServices(cert, capture);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var request = new HttpRequestMessage(HttpMethod.Post, origin.Url + "/uri-sentinel");
        request.Headers.TryAddWithoutValidation("X-Test-Key", "header-sentinel");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token-sentinel");
        request.Content = new StringContent("body-sentinel");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, origin.Count);
        var records = capture.Records.ToArray();
        Assert.DoesNotContain(records, entry => entry.Contains("System.Net.Http.HttpClient.", StringComparison.Ordinal));
        Assert.DoesNotContain(records, entry => new[] { "header-sentinel", "token-sentinel", "body-sentinel", "uri-sentinel" }
            .Any(sentinel => entry.Contains(sentinel, StringComparison.Ordinal)));
    }

    private static ServiceProvider BuildServices(X509Certificate2 cert, CapturingLoggerProvider? capture = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            if (capture is not null) logging.AddProvider(capture);
        });
        services.AddSingleton<IOutboundAddressPolicy, LoopbackPolicy>();
        services.AddExternalTools();
        // Only TLS verification is adjusted; the shared handler and both named clients come from AddExternalTools.
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SocketsHttpHandler>().SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                errors == SslPolicyErrors.None || presented is not null &&
                presented.GetCertHashString() == cert.GetCertHashString()
        };
        return provider;
    }

    private static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(20));
        // Schannel cannot serve TLS with an ephemeral private key on Windows.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private sealed class LoopbackPolicy : IOutboundAddressPolicy
    {
        public bool IsAllowed(IPAddress address) => IPAddress.IsLoopback(address);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Records);
        public void Dispose() { }
        private sealed class CaptureLogger(string category, ConcurrentQueue<string> records) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => records.Enqueue(category + " " + formatter(state, exception));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class HttpsOrigin : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _count;
        private HttpsOrigin(WebApplication app) => _app = app;
        public int Count => Volatile.Read(ref _count);
        public string Url => _app.Urls.Single().TrimEnd('/');
        public static async Task<HttpsOrigin> StartAsync(X509Certificate2 cert, Func<HttpContext, IResult> respond)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();

            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
            var app = builder.Build();
            var origin = new HttpsOrigin(app);
            app.MapFallback((HttpContext context) =>
            {
                Interlocked.Increment(ref origin._count);
                return respond(context);
            });
            await app.StartAsync();
            Assert.StartsWith("https://", origin.Url, StringComparison.Ordinal);
            return origin;
        }
        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
