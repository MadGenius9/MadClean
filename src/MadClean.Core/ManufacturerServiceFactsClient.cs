using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MadClean.Core;

/// <summary>Explicit fixed-document check. No hardware identifier, credential or arbitrary source URI is sent.</summary>
public sealed class ManufacturerServiceFactsClient : IManufacturerServiceFactsInventory, IDisposable
{
    private readonly HttpClient client;
    public ManufacturerServiceFactsClient() : this(CreateDefaultHandler()) { }
    // A transport seam for contract fixtures; the production Windows entry point uses only the locked default constructor.
    public ManufacturerServiceFactsClient(HttpMessageHandler transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        client = new(transport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        MaxResponseHeadersLength = 32
    };
    public async Task<CheckResult<ManufacturerServiceFactsReport>> ReadAsync(string manufacturer, string model, string sku, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (HardwareCompatibilityIdentityCatalog.Find(manufacturer, model, sku) == null)
            return CheckResult<ManufacturerServiceFactsReport>.Empty(CheckState.Unsupported,
                "This exact reported manufacturer/model/SKU is unsupported. No official source request was sent and no nearby model or retail board was substituted.");
        using var sequence = CancellationTokenSource.CreateLinkedTokenSource(token);
        sequence.CancelAfter(TimeSpan.FromSeconds(45));
        List<ManufacturerServiceDocument> documents = [];
        try
        {
            foreach (var uri in ManufacturerServiceFactsSources.All)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sequence.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MadClean", "0.1"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (response.RequestMessage?.RequestUri == null || !response.RequestMessage.RequestUri.AbsoluteUri.Equals(uri.AbsoluteUri, StringComparison.Ordinal))
                    return Empty(CheckState.Unknown, "The response source URI changed or could not be established; no service facts were accepted.");
                if ((int)response.StatusCode is >= 300 and < 400)
                    return Empty(CheckState.Unavailable, "An official source returned a redirect. It was not followed; no linked asset or replacement source was fetched.");
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests)
                    return Empty(CheckState.Unavailable, "An official source was inaccessible, missing, denied or rate limited. All model/upgrade conclusions remain Unknown.");
                if (!response.IsSuccessStatusCode)
                    return Empty(CheckState.Failed, $"An official source request failed (HTTP {(int)response.StatusCode}). No service or compatibility finding was inferred.");
                var contentType = response.Content.Headers.ContentType;
                if (!string.Equals(contentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                    contentType?.CharSet is { Length: > 0 } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                    return Empty(CheckState.Unknown, "The official response was not supported UTF-8 HTML; it was not rendered, executed or interpreted as a specification.");
                if (response.Content.Headers.ContentLength > ManufacturerServiceFactsSources.MaximumDocumentBytes)
                    return Empty(CheckState.Unknown, "An official source response exceeded the document bound; no truncated evidence was accepted.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[16_384];
                while (true)
                {
                    int count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (buffer.Length + count > ManufacturerServiceFactsSources.MaximumDocumentBytes)
                        return Empty(CheckState.Unknown, "Decoded source content exceeded the document bound; no partial or compressed-bomb evidence was accepted.");
                    buffer.Write(chunk, 0, count);
                }
                string html = new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                documents.Add(new(uri, html, DateTimeOffset.UtcNow));
            }
            token.ThrowIfCancellationRequested();
            var result = ManufacturerServiceFactsParser.Parse(manufacturer, model, sku, documents, DateTimeOffset.UtcNow, sequence.Token);
            sequence.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Empty(CheckState.Failed, "The bounded source check timed out. Offline or unfinished evidence remains Unknown, not current or compatible."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        { return Empty(CheckState.Failed, "The official source could not be retrieved. Check network/access availability and retry; no credentials, source substitution or validation bypass was used."); }
        catch (DecoderFallbackException)
        { return Empty(CheckState.Unknown, "The official source response was not valid UTF-8; no service facts were accepted."); }
    }
    private static CheckResult<ManufacturerServiceFactsReport> Empty(CheckState state, string message) =>
        CheckResult<ManufacturerServiceFactsReport>.Empty(state, message + " No complete hardware-compatibility or firmware check is claimed.");
    public void Dispose() => client.Dispose();
}
