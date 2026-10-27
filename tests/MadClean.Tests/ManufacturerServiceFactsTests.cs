using System.Net;
using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ManufacturerServiceFactsTests
{
    // Synthetic authored-article fixtures exercise scope/parser rules; they are not live manufacturer verification.
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Manufacturer = "Microsoft Corporation", Model = "Surface Laptop 6 for Business", Sku = "Surface_Laptop_6_for_Business_2033";
    private static string Article(string id, string title, string body) => $"<html><body><main><div class=\"content\"><h1 id=\"{id}\">{title}</h1></div><div class=\"content\">{body}</div></main></body></html>";
    private static string IdentityHtml() => Article("surface-system-sku-reference", "Surface System SKU reference", """
        <table><thead><tr><th>Device</th><th>System Model</th><th>System SKU</th></tr></thead><tbody>
        <tr><td>Surface Laptop 6 for Business 13.5-inch</td><td>Surface Laptop 6 for Business</td><td>Surface_Laptop_6_for_Business_2033</td></tr>
        <tr><td>Surface Laptop 6 for Business 15-inch</td><td>Surface Laptop 6 for Business</td><td>Surface_Laptop_6_for_Business_2035</td></tr>
        </tbody></table>
        """);
    private static string PartsHtml() => Article("surface-laptop-6th-for-business-device-information-and-service-parts", "Surface Laptop 6th for Business Device Information and Service Parts", """
        <h2 id="device-identity-information">Device Identity Information</h2>
        <h2 id="illustrated-service-parts-list">Illustrated Service Parts List</h2>
        <table><thead><tr><th>Item</th><th>Component</th><th>SKU Part No.</th><th>Substitute SKU</th></tr></thead><tbody>
        <tr><td>4</td><td><strong>Removable Solid-State Drive</strong></td><td></td><td></td></tr>
        <tr><td></td><td>13-inch 256GB rSSD</td><td>fixture-small</td><td></td></tr>
        <tr><td></td><td>15-inch 256GB rSSD</td><td>fixture-large</td><td></td></tr>
        <tr><td>5</td><td>Other part</td><td></td><td></td></tr>
        <tr><td>7</td><td>Motherboard Module ( includes main processor and main memory)</td><td></td><td></td></tr>
        <tr><td></td><td>13-inch Intel i5/8GB</td><td>fixture-small-board</td><td></td></tr>
        <tr><td></td><td>15-inch Intel i5/8GB</td><td>fixture-large-board</td><td></td></tr>
        <tr><td>8</td><td>Other part</td><td></td><td></td></tr>
        </tbody></table>
        """);
    private static string ProcedureHtml() => Article("surface-laptop-6-for-business-component-disassembly-and-reassembly", "Surface Laptop 6 for Business Component Disassembly and Reassembly", """
        <h2 id="prerequisite-steps">Prerequisite Steps</h2><p>Read the full official safety guidance.</p>
        <h2 id="storage-rssd-replacement-process">Storage (rSSD) Replacement Process</h2>
        <h3>Required Tools</h3><ul><li>Surface Battery Cover (M1214771-001)</li><li>USB Drive with SDT and BMR Image</li></ul>
        <h3>Procedure - Removal (rSSD)</h3><p>Full vendor procedure required.</p>
        <h3>Procedure - Installation (rSSD)</h3><p>Reinstall the operating system and all drivers/firmware using the latest Surface BMR for your model.</p>
        <h2 id="another-process">Another process</h2>
        """);
    private static ManufacturerServiceDocument[] Documents() =>
    [
        new(ManufacturerServiceFactsSources.Identity, IdentityHtml(), Now.AddSeconds(-3)),
        new(ManufacturerServiceFactsSources.Parts, PartsHtml(), Now.AddSeconds(-2)),
        new(ManufacturerServiceFactsSources.Procedure, ProcedureHtml(), Now.AddSeconds(-1))
    ];
    private static CheckResult<ManufacturerServiceFactsReport> Parse(ManufacturerServiceDocument[] documents, string sku = Sku) =>
        ManufacturerServiceFactsParser.Parse(Manufacturer, Model, sku, documents, Now);
    private static string SourceBody(Uri uri) => uri == ManufacturerServiceFactsSources.Identity ? IdentityHtml() : uri == ManufacturerServiceFactsSources.Parts ? PartsHtml() : ProcedureHtml();

    [Theory]
    [InlineData("Surface_Laptop_6_for_Business_2033", "13.5-inch")]
    [InlineData("Surface_Laptop_6_for_Business_2035", "15-inch")]
    public void ExactAuthoredEvidenceYieldsOnlyScopedServiceObservations(string sku, string label)
    {
        var result = Parse(Documents(), sku);
        Assert.Equal(CheckState.Available, result.State);
        var report = Assert.Single(result.Items);
        Assert.Contains(label, report.ProductLabel); Assert.Equal(3, report.Facts.Count);
        Assert.All(report.Facts, fact => Assert.True(ManufacturerServiceFactsSources.IsPinnedUri(fact.SourceUri)));
        Assert.Contains(report.Coverage, text => text.Contains("RAM type/upgrade maximum/soldered design", StringComparison.Ordinal));
        Assert.Contains(report.Facts, fact => fact.Uncertainty.Contains("does not establish RAM type/maximum, soldering", StringComparison.Ordinal));
        if (sku == Sku) Assert.Contains(report.Coverage, text => text.Contains("No numerical rounding", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentIdentityMustComeFromOneExactTableRowNotANameElsewhere()
    {
        var documents = Documents();
        documents[0] = documents[0] with { Html = IdentityHtml().Replace(Sku, Sku + "_OTHER", StringComparison.Ordinal) + Sku };
        Assert.Equal(CheckState.Unknown, Parse(documents).State);
        documents[0] = documents[0] with { Html = IdentityHtml().Replace("</tbody>", "<tr><td>Conflicting device</td><td>Surface Laptop 6 for Business</td><td>" + Sku + "</td></tr></tbody>", StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
    }

    [Fact]
    public void AllThreeSourcesAreRequiredWithUniquePinnedOriginsAndRecentActualRetrievalTimes()
    {
        var source = Documents();
        Assert.Equal(CheckState.Unknown, Parse(source[..2]).State);
        Assert.Empty(Parse([source[0], source[1], source[1]]).Items);
        source[2] = source[2] with { RetrievedUtc = Now.AddSeconds(1) };
        Assert.Empty(Parse(source).Items);
        source[2] = source[2] with { RetrievedUtc = Now.AddMinutes(-3) };
        Assert.Empty(Parse(source).Items);
    }

    [Theory]
    [InlineData("<main><nav><h1 id='surface-system-sku-reference'>Surface System SKU reference</h1></nav></main>")]
    [InlineData("<main><script><h1 id='surface-system-sku-reference'>Surface System SKU reference</h1></script></main>")]
    [InlineData("<main><template><h1 id='surface-system-sku-reference'>Surface System SKU reference</h1></template></main>")]
    [InlineData("<main><div><h1>Surface Laptop 7</h1></div></main>")]
    [InlineData("<main><h1>Malformed</main>")]
    public void NavigationScriptTemplatesAndMalformedMarkupAreNotArticleEvidence(string html)
    {
        var documents = Documents(); documents[0] = documents[0] with { Html = html };
        var result = Parse(documents);
        Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("Surface Laptop 7th for Business Device Information and Service Parts")]
    [InlineData("Generic support and replacement guide")]
    public void AnotherProductOrGenericTitleCannotReuseTheSameKeywords(string title)
    {
        var documents = Documents(); documents[1] = documents[1] with { Html = PartsHtml().Replace("Surface Laptop 6th for Business Device Information and Service Parts", title, StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
    }

    [Fact]
    public void ServiceCategoryNeedsBothVariantRowsAndTheAuthoredTableContext()
    {
        var documents = Documents();
        documents[1] = documents[1] with { Html = PartsHtml().Replace("13-inch 256GB rSSD", "unmatched service part", StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
        documents[1] = documents[1] with { Html = PartsHtml().Replace("Removable Solid-State Drive", "Fixed storage", StringComparison.Ordinal) + "Removable Solid-State Drive" };
        Assert.Empty(Parse(documents).Items);
        documents[1] = documents[1] with { Html = PartsHtml().Replace("<td>fixture-small</td>", "<td><table><tr><td>nested</td></tr></table></td>", StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchingPartsTableOutsideTheIllustratedServiceSectionIsNotEvidence(bool beforeHeading)
    {
        var documents = Documents(); string html = PartsHtml();
        if (beforeHeading)
        {
            int start = html.IndexOf("<table>", StringComparison.Ordinal), end = html.IndexOf("</table>", StringComparison.Ordinal) + "</table>".Length;
            string table = html[start..end]; html = html.Remove(start, end - start);
            html = html.Replace("<h2 id=\"illustrated-service-parts-list\">", table + "<h2 id=\"illustrated-service-parts-list\">", StringComparison.Ordinal);
        }
        else html = html.Replace("<table>", "<h2 id=\"unrelated-section\">Unrelated section</h2><table>", StringComparison.Ordinal);
        documents[1] = documents[1] with { Html = html };
        var result = Parse(documents); Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public void ProcedurePrerequisitesCannotBeFoundOutsideTheStorageSection()
    {
        var documents = Documents();
        documents[2] = documents[2] with { Html = ProcedureHtml().Replace("USB Drive with SDT and BMR Image", "Other tool", StringComparison.Ordinal) + "USB Drive with SDT and BMR Image" };
        Assert.Empty(Parse(documents).Items);
        documents[2] = documents[2] with { Html = ProcedureHtml().Replace("Procedure - Installation (rSSD)", "Procedure - Installation (another device)", StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
    }

    [Fact]
    public void HiddenAmbiguousOversizedOrDeepArticleEvidenceIsRejected()
    {
        var documents = Documents();
        documents[1] = documents[1] with { Html = PartsHtml().Replace("<table>", "<table hidden>", StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
        documents[1] = documents[1] with { Html = PartsHtml().Replace("class=\"content\"", "class=\"content\" class=\"other\"", StringComparison.Ordinal) };
        Assert.Empty(Parse(documents).Items);
        documents[1] = documents[1] with { Html = new string('中', ManufacturerServiceFactsSources.MaximumDocumentBytes / 2) };
        Assert.Empty(Parse(documents).Items);
        documents[1] = documents[1] with { Html = "<main>" + string.Concat(Enumerable.Repeat("<div>", 70)) + "fact" + string.Concat(Enumerable.Repeat("</div>", 70)) + "</main>" };
        Assert.Empty(Parse(documents).Items);
    }

    [Fact]
    public void ParserCancellationIsNotACompletedManufacturerCheck()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ManufacturerServiceFactsParser.Parse(Manufacturer, Model, Sku, Documents(), Now, cancelled.Token));
    }

    [Theory]
    [InlineData("https://learn.microsoft.com.evil.test/en-us/surface/surface-system-sku-reference")]
    [InlineData("http://learn.microsoft.com/en-us/surface/surface-system-sku-reference")]
    [InlineData("https://secret@learn.microsoft.com/en-us/surface/surface-system-sku-reference")]
    [InlineData("https://learn.microsoft.com/en-us/surface/surface-system-sku-reference?model=2033")]
    [InlineData("https://learn.microsoft.com/en-us/surface/%73urface-system-sku-reference")]
    [InlineData("https://learn.microsoft.com:8443/en-us/surface/surface-system-sku-reference")]
    public void SourceUrisAreNotUserConfigurableOrAmbiguouslyNormalized(string uri)
    {
        Assert.False(ManufacturerServiceFactsSources.IsPinnedUri(new(uri)));
        var documents = Documents(); documents[0] = documents[0] with { SourceUri = new(uri) };
        Assert.Empty(Parse(documents).Items);
    }

    [Fact]
    public async Task UnsupportedIdentityMakesNoRequestAndNeverFallsBackToARetailBoardOrNearbyModel()
    {
        using var handler = new FixtureHandler((_, _) => throw new InvalidOperationException("Unexpected request"));
        using var checker = new ManufacturerServiceFactsClient(handler);
        var result = await checker.ReadAsync(Manufacturer, "Surface Laptop 7 for Business", Sku, default);
        Assert.Equal(CheckState.Unsupported, result.State); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TransportSendsOnlyThreePinnedDocumentationRequestsWithoutIdentityOrCredentials()
    {
        using var handler = new FixtureHandler((request, _) => Task.FromResult(Reply(request, SourceBody(request.RequestUri!))));
        using var checker = new ManufacturerServiceFactsClient(handler);
        var result = await checker.ReadAsync(Manufacturer, Model, Sku, default);
        Assert.Equal(CheckState.Available, result.State);
        Assert.Equal(ManufacturerServiceFactsSources.All, handler.Requests.Select(request => request.Uri));
        Assert.All(handler.Requests, request =>
        {
            Assert.DoesNotContain(Sku, request.Uri.AbsoluteUri, StringComparison.Ordinal);
            Assert.DoesNotContain(request.Headers, name => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task RedirectIsNotFollowedAndPreviouslyRetrievedIdentityDoesNotProducePartialServiceFacts()
    {
        using var handler = new FixtureHandler((request, _) =>
        {
            if (request.RequestUri == ManufacturerServiceFactsSources.Identity) return Task.FromResult(Reply(request, IdentityHtml()));
            var response = Reply(request, "redirect"); response.StatusCode = HttpStatusCode.Redirect;
            response.Headers.Location = new("https://example.test/firmware.exe"); return Task.FromResult(response);
        });
        using var checker = new ManufacturerServiceFactsClient(handler);
        var result = await checker.ReadAsync(Manufacturer, Model, Sku, default);
        Assert.Equal(CheckState.Unavailable, result.State); Assert.Empty(result.Items); Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AccessAndServerFailuresNeverBecomeMatchedServiceFacts(HttpStatusCode status)
    {
        using var handler = new FixtureHandler((request, _) => { var response = Reply(request, "unavailable"); response.StatusCode = status; return Task.FromResult(response); });
        using var checker = new ManufacturerServiceFactsClient(handler);
        var result = await checker.ReadAsync(Manufacturer, Model, Sku, default);
        Assert.True(result.State is CheckState.Unavailable or CheckState.Failed); Assert.Empty(result.Items);
    }

    [Fact]
    public async Task OfflineDetailsAreNotLeakedOrConvertedToAnUpToDateVerdict()
    {
        using var handler = new FixtureHandler((_, _) => throw new HttpRequestException("private-token-value"));
        using var checker = new ManufacturerServiceFactsClient(handler);
        var result = await checker.ReadAsync(Manufacturer, Model, Sku, default);
        Assert.Equal(CheckState.Failed, result.State); Assert.DoesNotContain("private-token-value", result.Message, StringComparison.Ordinal); Assert.Empty(result.Items);
    }

    [Fact]
    public async Task UnsupportedMediaInvalidUtf8AndDecodedOverrunRemainUnknown()
    {
        foreach (var bytes in new[] { new byte[] { 0xC3, 0x28 }, new byte[ManufacturerServiceFactsSources.MaximumDocumentBytes + 1] })
        {
            using var handler = new FixtureHandler((request, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new UnboundedContent(bytes) };
                response.Content.Headers.ContentType = new("text/html") { CharSet = "utf-8" }; return Task.FromResult(response);
            });
            using var checker = new ManufacturerServiceFactsClient(handler);
            Assert.Equal(CheckState.Unknown, (await checker.ReadAsync(Manufacturer, Model, Sku, default)).State);
        }
        using var wrongTypeHandler = new FixtureHandler((request, _) =>
        {
            var response = Reply(request, IdentityHtml()); response.Content.Headers.ContentType = new("application/octet-stream"); return Task.FromResult(response);
        });
        using var wrongType = new ManufacturerServiceFactsClient(wrongTypeHandler);
        Assert.Equal(CheckState.Unknown, (await wrongType.ReadAsync(Manufacturer, Model, Sku, default)).State);
    }

    [Fact]
    public async Task ChangedResponseOriginAndMalformedSourceStopTheWholeCheck()
    {
        using var handler = new FixtureHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/other"), Content = new StringContent(IdentityHtml(), Encoding.UTF8, "text/html") }));
        using var checker = new ManufacturerServiceFactsClient(handler);
        Assert.Equal(CheckState.Unknown, (await checker.ReadAsync(Manufacturer, Model, Sku, default)).State);
    }

    [Fact]
    public async Task CancellationStopsTheCallerWithoutReturningConfirmedFacts()
    {
        using var handler = new FixtureHandler(async (request, token) => { await Task.Delay(Timeout.Infinite, token); return Reply(request, "unreachable"); });
        using var checker = new ManufacturerServiceFactsClient(handler);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.ReadAsync(Manufacturer, Model, Sku, cancelled.Token));
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, string html) => new(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private sealed record RequestSnapshot(Uri Uri, string[] Headers);
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal List<RequestSnapshot> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!, request.Headers.Select(header => header.Key).ToArray()));
            return reply(request, cancellationToken);
        }
    }
    private sealed class UnboundedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
