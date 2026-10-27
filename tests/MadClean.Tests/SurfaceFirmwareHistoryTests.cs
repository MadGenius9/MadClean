using System.Net;
using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SurfaceFirmwareHistoryTests
{
    // Synthetic authored-document scope fixtures, not a detected device or live Microsoft result.
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static FirmwareIdentity Identity(string sku = "Surface_Laptop_6_for_Business_2035") =>
        new("Microsoft Corporation", "Surface Laptop 6 for Business", sku, "Unknown", "Unknown", "Unknown", "Unknown", "unrelated-installed-string", "Unknown");
    private static string IdentityHtml() => """
        <main><h1 id="surface-system-sku-reference">Surface System SKU reference</h1><div class="content">
        <table><tr><th>Device</th><th>System Model</th><th>System SKU</th></tr>
        <tr><td>Surface Laptop 6 for Business 15-inch</td><td>Surface Laptop 6 for Business</td><td>Surface_Laptop_6_for_Business_2035</td></tr></table>
        </div></main>
        """;
    private static string Release(string date = "October 22", string component = "Surface - System - 1.2.3.4", string device = "Surface Integration - System devices") => $$"""
        <p><strong>{{date}} release</strong></p>
        <p>The following update is available for Surface Laptop 6 for Business devices running Windows 11, Version 23H2 or greater.</p>
        <ul><li>Published reliability observation.</li></ul>
        <p>This release contains the following components. The specific components installed are based on your device’s configuration:</p>
        <table><tr><th>Windows Update Name</th><th>Device manager</th></tr><tr><td>{{component}}</td><td>{{device}}</td></tr></table>
        """;
    private static string HistoryHtml() => """
        <main id="supMainContent"><h1 id="surface-laptop-6-for-business-update-history">Surface Laptop 6 for Business update history</h1>
        <section class="supAppliesToSection"><span class="appliesToItem">Surface Laptop 6 - 13 inch</span><span class="appliesToItem">Surface Laptop 6 - 15 inch</span></section>
        <div class="learnArticleContent"><h2 id="improvements-and-fixes-in-the-updates">Improvements and fixes in the updates</h2>
        <h3 id="october-2026-updates">October 2026 updates</h3>
        """ + Release() + "<h3 id=\"august-2026-updates\">August 2026 updates</h3>" +
        Release("August 6", "Surface Driver Update (23.105.143.0)", "Surface UEFI - Firmware") + "<h2 id=\"other\">Unrelated section</h2></div></main>";
    private static SurfaceFirmwareHistoryDocument[] Documents(string? history = null, string? identity = null) =>
        [new(SurfaceFirmwareHistorySources.Identity, identity ?? IdentityHtml(), Now), new(SurfaceFirmwareHistorySources.History, history ?? HistoryHtml(), Now)];
    private static CheckResult<SurfaceFirmwareHistoryReport> Parse(string? html = null) => SurfaceFirmwareHistoryParser.Parse(Identity(), Documents(html), Now);

    [Fact]
    public void DriversOnlyLatestReleaseDoesNotBecomeABiosVersionOrCurrentVerdict()
    {
        var result = Parse(); Assert.Equal(CheckState.Available, result.State);
        var report = Assert.Single(result.Items); Assert.Equal(2, report.Releases.Count);
        Assert.Null(report.Releases[0].PublishedUefiVersion);
        Assert.Equal("23.105.143.0", report.Releases[1].PublishedUefiVersion);
        Assert.Equal(new DateOnly(2026, 10, 22), report.Releases[0].PublishedDate);
        Assert.Contains("applicability remain Unknown", result.Message);
        Assert.All(report.Releases, release => Assert.Equal(SurfaceFirmwareHistorySources.History, release.SourceUri));
        var differentBios = SurfaceFirmwareHistoryParser.Parse(Identity() with { BiosVersion = "999999999" }, Documents(), Now);
        Assert.Equal(report.Releases[1].PublishedUefiVersion, Assert.Single(differentBios.Items).Releases[1].PublishedUefiVersion);
    }
    [Fact]
    public void MultipleReleasesInOneMonthRemainSeparatelyDated()
    {
        string html = HistoryHtml().Replace(Release(), Release() + Release("October 1"));
        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(Parse(html).Items).Releases[1].PublishedDate);
    }
    [Fact]
    public void LegacyUefiPackageNotationIsPublishedTextNotInstalledVersionOrdering()
    {
        var report = Assert.Single(Parse(HistoryHtml().Replace("Surface Driver Update (23.105.143.0)", "Surface - Firmware - 20.111.143.0")).Items);
        Assert.Equal("20.111.143.0", report.Releases[1].PublishedUefiVersion);
    }
    [Theory]
    [InlineData("Surface_Laptop_6_for_Business_2033", CheckState.Unknown)]
    [InlineData("Surface_Laptop_6_for_Business_20350", CheckState.Unsupported)]
    public async Task UnknownOrAmbiguousIdentityDoesNotSendNetworkRequests(string sku, CheckState state)
    {
        var transport = new FixtureTransport(_ => throw new InvalidOperationException("Unexpected request"));
        using var client = new SurfaceFirmwareHistoryClient(transport);
        var result = await client.CheckAsync(Identity(sku), CancellationToken.None);
        Assert.Equal(state, result.State); Assert.Empty(result.Items); Assert.Equal(0, transport.Count);
    }
    [Theory]
    [InlineData("manufacturer")][InlineData("model")]
    public void SimilarModelOrRetailBoardResemblanceCannotQualifyHistory(string field)
    {
        var identity = field == "manufacturer" ? Identity() with { PcManufacturer = "Other OEM", BoardProduct = "Surface Laptop 6 for Business" }
            : Identity() with { PcModel = "Surface Laptop 6", BoardProduct = "Surface Laptop 6 for Business" };
        Assert.Equal(CheckState.Unsupported, SurfaceFirmwareHistoryParser.Parse(identity, Documents(), Now).State);
    }
    [Theory]
    [InlineData("title")][InlineData("variant")][InlineData("scope")][InlineData("outside")]
    [InlineData("future")][InlineData("duplicate")][InlineData("wrongmonth")][InlineData("badversion")]
    [InlineData("emptytable")][InlineData("duplicateuefi")][InlineData("hidden")][InlineData("script")]
    [InlineData("malformed")][InlineData("oversize")][InlineData("notesbound")][InlineData("tableheaders")]
    public void ChangedAmbiguousOrOutOfScopeEvidenceNeverProducesPartialHistory(string change)
    {
        string html = HistoryHtml();
        html = change switch
        {
            "title" => html.Replace("<h1 id=\"surface-laptop-6-for-business-update-history\">Surface Laptop 6 for Business update history", "<h1 id=\"surface-laptop-6-for-business-update-history\">Surface Laptop 5 update history"),
            "variant" => html.Replace("Surface Laptop 6 - 15 inch", "Surface Laptop 6 - 15.1 inch"),
            "scope" => html.Replace("devices running Windows 11", "Enterprise Edition devices running Windows 11"),
            "outside" => html.Replace("<h3 id=\"october", "<h2>Other heading</h2><h3 id=\"october"),
            "future" => html.Replace("October 22 release", "October 28 release"),
            "duplicate" => html.Replace(Release(), Release() + Release()),
            "wrongmonth" => html.Replace("October 22 release", "September 22 release"),
            "badversion" => html.Replace("23.105.143.0", "latest"),
            "emptytable" => html.Replace("<th>Windows Update Name</th><th>Device manager</th>", ""),
            "duplicateuefi" => html.Replace("</table><h2", "<tr><td>Surface Driver Update (23.105.143.0)</td><td>Surface UEFI - Firmware</td></tr></table><h2"),
            "hidden" => html.Replace("class=\"learnArticleContent\"", "class=\"learnArticleContent\" hidden"),
            "script" => html.Replace("<div class=\"learnArticleContent\">", "<script><div class=\"learnArticleContent\">").Replace("</div></main>", "</div></script></main>"),
            "malformed" => html.Replace("</table>", "</div>"),
            "oversize" => html + new string('x', SurfaceFirmwareHistorySources.MaximumDocumentBytes),
            "notesbound" => html.Replace("Published reliability observation.", new string('x', 4097)),
            "tableheaders" => html.Replace("Windows Update Name", "Download package"),
            _ => throw new ArgumentException(change)
        };
        var result = Parse(html); Assert.Equal(CheckState.Unknown, result.State); Assert.Empty(result.Items);
    }
    [Fact]
    public void LiveIdentityRowMustAgreeAndBothSourcesMustBeFreshUniqueAndPinned()
    {
        Assert.Equal(CheckState.Unknown, SurfaceFirmwareHistoryParser.Parse(Identity(), Documents(identity: IdentityHtml().Replace("15-inch", "13.5-inch")), Now).State);
        var documents = Documents();
        Assert.Equal(CheckState.Unknown, SurfaceFirmwareHistoryParser.Parse(Identity(), [documents[0], documents[0]], Now).State);
        Assert.Equal(CheckState.Unknown, SurfaceFirmwareHistoryParser.Parse(Identity(), [documents[0] with { RetrievedUtc = Now.AddMinutes(-3) }, documents[1]], Now).State);
        Assert.Equal(CheckState.Unknown, SurfaceFirmwareHistoryParser.Parse(Identity(), [documents[0], documents[1] with { RetrievedUtc = Now.AddSeconds(1) }], Now).State);
    }
    [Theory]
    [InlineData("?model=2035")][InlineData("#august-2026-updates")][InlineData("/extra")]
    public void BrowserSourceAllowlistDoesNotWidenToQueriesFragmentsOrOtherPages(string suffix) =>
        Assert.False(SurfaceFirmwareHistorySources.IsTrustedHistoryUri(new(SurfaceFirmwareHistorySources.History.AbsoluteUri + suffix)));
    [Fact]
    public void EscapedAndCredentialSourcesAreRejected()
    {
        Assert.False(SurfaceFirmwareHistorySources.IsTrustedHistoryUri(new(SurfaceFirmwareHistorySources.History.AbsoluteUri.Replace("updates/", "%75pdates/"))));
        Assert.False(SurfaceFirmwareHistorySources.IsTrustedHistoryUri(new("https://user@support.microsoft.com/en-us/surface/updates/surface-laptop-6-for-business-update-history")));
        Assert.False(SurfaceFirmwareHistorySources.IsTrustedHistoryUri(new("https://support.microsoft.com/en-us/surface/updates/other/../surface-laptop-6-for-business-update-history")));
        Assert.False(SurfaceFirmwareHistorySources.IsTrustedHistoryUri(new("https://support.microsoft.com:444/en-us/surface/updates/surface-laptop-6-for-business-update-history")));
        Assert.True(SurfaceFirmwareHistorySources.IsTrustedHistoryUri(SurfaceFirmwareHistorySources.History));
    }
    [Fact]
    public async Task SuccessSendsOnlyTwoFixedGetRequestsAndNoIdentityOrCredentials()
    {
        var transport = new FixtureTransport(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.DoesNotContain("2035", request.ToString()); Assert.DoesNotContain("unrelated-installed", request.ToString());
            return Html(request.RequestUri == SurfaceFirmwareHistorySources.Identity ? IdentityHtml() : HistoryHtml());
        });
        using var client = new SurfaceFirmwareHistoryClient(transport);
        Assert.Equal(CheckState.Available, (await client.CheckAsync(Identity(), CancellationToken.None)).State);
        Assert.Equal(2, transport.Count);
    }
    [Theory]
    [InlineData(301, CheckState.Unavailable)][InlineData(403, CheckState.Unavailable)][InlineData(404, CheckState.Unavailable)]
    [InlineData(429, CheckState.Unavailable)][InlineData(503, CheckState.Failed)]
    public async Task RedirectDeniedAndFailedRequestsCannotBecomeCurrent(int status, CheckState state)
    {
        var transport = new FixtureTransport(_ => new((HttpStatusCode)status) { Headers = { Location = new("https://example.invalid/firmware.bin") } });
        using var client = new SurfaceFirmwareHistoryClient(transport);
        var result = await client.CheckAsync(Identity(), CancellationToken.None);
        Assert.Equal(state, result.State); Assert.Empty(result.Items); Assert.Equal(1, transport.Count);
    }
    [Fact]
    public async Task PartialHttpResponseIsRefusedEvenWhenItsHtmlWouldOtherwiseQualify()
    {
        var transport = new FixtureTransport(request =>
        {
            var response = Html(request.RequestUri == SurfaceFirmwareHistorySources.Identity ? IdentityHtml() : HistoryHtml());
            response.StatusCode = HttpStatusCode.PartialContent;
            return response;
        });
        using var client = new SurfaceFirmwareHistoryClient(transport);
        var result = await client.CheckAsync(Identity(), CancellationToken.None);
        Assert.Equal(CheckState.Failed, result.State); Assert.Empty(result.Items); Assert.Equal(1, transport.Count);
    }
    [Theory]
    [InlineData("offline", CheckState.Failed)][InlineData("utf8", CheckState.Unknown)][InlineData("bytes", CheckState.Unknown)]
    [InlineData("origin", CheckState.Unknown)][InlineData("type", CheckState.Unknown)]
    public async Task InvalidTransportEvidenceIsNotInterpreted(string mode, CheckState expected)
    {
        var transport = new FixtureTransport(_ =>
        {
            if (mode == "offline") throw new HttpRequestException("secret-provider-details");
            var response = Html(IdentityHtml());
            if (mode == "utf8") response.Content = new ByteArrayContent([0xff, 0xff]);
            if (mode == "bytes") response.Content = new ByteArrayContent(new byte[SurfaceFirmwareHistorySources.MaximumDocumentBytes + 1]);
            if (mode == "origin") response.RequestMessage = new(HttpMethod.Get, "https://example.invalid/");
            response.Content.Headers.ContentType = new(mode == "type" ? "application/octet-stream" : "text/html");
            return response;
        });
        using var client = new SurfaceFirmwareHistoryClient(transport);
        var result = await client.CheckAsync(Identity(), CancellationToken.None);
        Assert.Equal(expected, result.State); Assert.Empty(result.Items); Assert.DoesNotContain("secret-provider-details", result.Message);
    }
    [Fact]
    public async Task CancellationIsPropagatedBeforeNetworkAndDuringRequest()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var transport = new FixtureTransport(_ => throw new InvalidOperationException());
        using var client = new SurfaceFirmwareHistoryClient(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(Identity(), cancellation.Token));
        Assert.Equal(0, transport.Count);
        using var during = new CancellationTokenSource();
        using var pending = new SurfaceFirmwareHistoryClient(new BlockingTransport());
        var check = pending.CheckAsync(Identity(), during.Token); during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.ThrowsAny<OperationCanceledException>(() => SurfaceFirmwareHistoryParser.Parse(Identity(), Documents(), Now, cancellation.Token));
    }
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private sealed class FixtureTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++; var response = respond(request); response.RequestMessage ??= request; return Task.FromResult(response);
        }
    }
    private sealed class BlockingTransport : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }
    }
}
