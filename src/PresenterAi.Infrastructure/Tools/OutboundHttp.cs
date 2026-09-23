using System.Net;

namespace PresenterAi.Infrastructure.Tools;

public sealed class OutboundRequestHandler(IOutboundAddressPolicy policy, long maxBytes) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<bool> MetadataHop = new("ExternalTools.MetadataHop");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null) throw new OutboundGuardException("tools_url_invalid");
        OutboundUrlValidator.Validate(request.RequestUri.OriginalString, policy);
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 300 and <= 399 &&
            !(request.Method == HttpMethod.Get && request.Options.TryGetValue(MetadataHop, out var metadata) && metadata))
        {
            response.Dispose();
            throw new OutboundGuardException("tools_redirect_refused");
        }
        if (response.Content is not null)
        {
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                response.Dispose();
                throw new OutboundGuardException("tools_response_too_large");
            }
            var original = response.Content;
            var stream = await original.ReadAsStreamAsync(cancellationToken);
            var capped = new StreamContent(new LimitedReadStream(stream, maxBytes));
            foreach (var header in original.Headers)
                capped.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = capped;
        }
        return response;
    }
}

public static class OutboundMetadata
{
    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url,
        IOutboundAddressPolicy policy, CancellationToken cancellationToken = default)
    {
        // Metadata must never inherit a caller's bearer token or fixed server header.
        if (client.DefaultRequestHeaders.Any())
            throw new OutboundGuardException("tools_url_invalid");
        for (var hop = 0; hop <= 3; hop++)
        {
            var uri = OutboundUrlValidator.Validate(url, policy);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Options.Set(OutboundRequestHandler.MetadataHop, true);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (>= 300 and <= 399)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (hop == 3 || location is null) throw new OutboundGuardException("tools_redirect_refused");
            url = new Uri(uri, location).OriginalString;
        }
        throw new OutboundGuardException("tools_redirect_refused");
    }
}

internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _read;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer, offset, count, cancellationToken));
    private int Count(int n)
    {
        _read += n;
        if (_read > limit) throw new OutboundGuardException("tools_response_too_large");
        return n;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
