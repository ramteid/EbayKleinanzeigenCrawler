using System.Linq;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Parser.Implementations;
using Moq;
using Serilog;
using Xunit;

namespace KleinanzeigenCrawler.Tests.Parser;

public class WohnverdientParserTests
{
    private readonly WohnverdientParser _parser = new(Mock.Of<ILogger>(), Mock.Of<IQueryExecutor>());

    // Shortened copy of a result page of https://wohnverdient.de/mietangebote/wohnungen/79106-freiburg-im-breisgau-deutschland/
    private const string ListPage = """
        <html><head><title>Mietangebote - Wohnungen 2 Objekt(e) in Freiburg im Breisgau</title></head><body>
        <div class="large-9 cell item-list-header"><h1>Wohnungen ▸  2 Objekt(e)  für "Mietangebote" in 79106 Freiburg im Breisgau, Deutschland</h1></div>
        <div id="item_card_225169" class="listing-item ">
          <a href="/wohnungen/freiburg-im-breisgau-deutschland/helle-4-zi-wohnung-dg-freiburg-wiehre-225169" class="tricky-link">Helle 4-Zi-Wohnung, DG, Freiburg-Wiehre</a>
          <div class="list-content"><div class="item-head-wrapper"><p class="price">
              1.700 €
            </p><h2 class="item-title">Helle 4-Zi-Wohnung, DG, Freiburg-Wiehre</h2></div>
            <div class="listing-dtl"><table width="100%" class="item-features"><tr><td width="50%"><b>Veröffentlicht am:</b> 21.11.26     </td></table>
            <div class="list-item-para">Helle 4-Zi-Wohnung, 125 m, DG, ...</div><div class="dealer-name">privat</div></div></div>
        </div>
        <div class="listing-mid-item"></div>
        <div id="item_card_305502" class="listing-item ">
          <a href="/wohnungen/gundelfingen-deutschland/attraktives-wohnen-3-zi-305502" class="tricky-link">Attraktives Wohnen 3 Zi</a>
          <p class="price">980 €</p>
        </div>
        <h2 class="list-title">Aktuelle Verkaufsanzeigen</h2>
        <div class="slider featured-item" id="latest_list_for_sale"><div class="thumb">
          <a href="/haeuser/korb-deutschland/mehrgenerationenhaus-mit-gewerbebereich-garage-und-garten-301003" class="tricky-link">Mehrgenerationenhaus</a></div></div>
        </body></html>
        """;

    [Fact]
    public void ParseLinks_ShouldParseTheCardsOfTheResultListAndIgnoreRecommendations()
    {
        var results = _parser.ParseLinks(Load(ListPage)).ToList();

        results.Should().HaveCount(2);
        results[0].Link.AbsoluteUri.Should().Be("https://wohnverdient.de/wohnungen/freiburg-im-breisgau-deutschland/helle-4-zi-wohnung-dg-freiburg-wiehre-225169");
        results[0].Price.Should().Be("1.700 €");
        results[0].CreationDate.Should().Be("Veröffentlicht am: 21.11.26");
        results[1].Link.AbsolutePath.Should().Be("/wohnungen/gundelfingen-deutschland/attraktives-wohnen-3-zi-305502");
        results[1].Price.Should().Be("980 €");
        results[1].CreationDate.Should().BeEmpty();
    }

    [Fact]
    public void ParseLinks_ShouldParseTheGalleryView()
    {
        var document = Load("""
            <h1>Wohnungen ▸  1 Objekt(e)  für "Mietangebote"</h1>
            <div id="gallery-items"><div class="cell large-4">
              <div id="item_card_225169" class="gallery-item listing-item " data-equalizer-watch>
                <a href="/wohnungen/freiburg-im-breisgau-deutschland/helle-4-zi-wohnung-dg-freiburg-wiehre-225169" class="tricky-link">Helle 4-Zi-Wohnung</a>
                <div class="item-head-wrapper clearfix"><h2 class="item-title">Helle 4-Zi-Wohnung</h2><p class="price">
                  1.700 €
                </p></div>
                <div class="listing-dtl"><div class="item-features"><ul class="fa-no-bullet clearfix"><li><b>Veröffentlicht am:</b> 21.11.26</li></ul></div></div>
              </div></div></div>
            """);

        var results = _parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Price.Should().Be("1.700 €");
        results[0].CreationDate.Should().Be("Veröffentlicht am: 21.11.26");
    }

    [Theory]
    [InlineData("https://evil.invalid/wohnungen/freiburg-deutschland/wohnung-123")]
    [InlineData("//evil.invalid/wohnungen/freiburg-deutschland/wohnung-123")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://wohnverdient.de/wohnungen/freiburg-deutschland/wohnung-123")]
    [InlineData("https://wohnverdient.de.evil.invalid/wohnungen/freiburg-deutschland/wohnung-123")]
    [InlineData("https://user@wohnverdient.de/wohnungen/freiburg-deutschland/wohnung-123")]
    [InlineData("/mietangebote/wohnungen/")]
    [InlineData("/mietangebote/wohnungen/seite-2/")]
    [InlineData("/anbieter/regina-weismann/mpmocJ8=")]
    [InlineData("/wohnungen/")]
    [InlineData("")]
    public void ParseLinks_ShouldSkipInvalidLinksAndContinueWithTheNextCard(string href)
    {
        var document = Load($"""
            <h1>Wohnungen ▸  2 Objekt(e)</h1>
            <div id="item_card_1" class="listing-item"><a class="tricky-link" href="{href}">Bad</a></div>
            <div id="item_card_2" class="listing-item"><a class="tricky-link" href="/wohnungen/freiburg-deutschland/wohnung-2">Good</a></div>
            """);

        var results = _parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Link.AbsolutePath.Should().Be("/wohnungen/freiburg-deutschland/wohnung-2");
        results[0].Price.Should().BeEmpty();
        results[0].CreationDate.Should().BeEmpty();
    }

    [Fact]
    public void ParseLinks_ShouldNotReportDuplicateCardsTwice()
    {
        var document = Load("""
            <h1>Wohnungen ▸  2 Objekt(e)</h1>
            <div id="item_card_1" class="listing-item"><a class="tricky-link" href="/wohnungen/freiburg-deutschland/wohnung-1">A</a></div>
            <div id="item_card_1" class="listing-item"><a class="tricky-link" href="/wohnungen/freiburg-deutschland/wohnung-1">A</a></div>
            """);

        _parser.ParseLinks(document).Should().ContainSingle();
    }

    [Fact]
    public void ParseLinks_ShouldAcceptAnExplicitZeroResultPage()
    {
        // The page of a search without results has no heading, only the title names the count. Its recommendations are no results.
        var document = Load("""
            <html><head><title>Mietangebote - Wohnungen 0 Objekt(e) in Freiburg im Breisgau</title></head><body>
            <div data-alert class="primary callout faf_callout">Für die von Ihnen eingegebene Suche wurden leider keine passenden Anzeigen gefunden</div>
            <div class="slider featured-item"><div class="thumb"><a class="tricky-link" href="/haeuser/korb-deutschland/haus-301003">Haus</a></div></div>
            </body></html>
            """);

        _parser.GetTotalResultCount(document).Should().Be(0);
        _parser.ParseLinks(document).Should().BeEmpty();
        // An explicit zero is a valid page, so it must not be reported as a parse error
        var logger = new Mock<ILogger>();
        new WohnverdientParser(logger.Object, Mock.Of<IQueryExecutor>()).ParseLinks(document).ToList();
        logger.Verify(l => l.Warning(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("<h1>Wohnungen ▸  10 Objekt(e)  für \"Mietangebote\" in 79106 Freiburg im Breisgau, Deutschland</h1>", 10)]
    [InlineData("<h1>Wohnungen ▸  1 Objekt(e)  für \"Mietangebote\"</h1>", 1)]
    [InlineData("<h1>Wohnungen ▸  1.234 Objekt(e)  für \"Mietangebote\"</h1>", 1234)]
    [InlineData("<title>Mietangebote - Wohnungen 0 Objekt(e) in Freiburg im Breisgau</title>", 0)]
    [InlineData("<title>Mietangebote - Wohnungen 12 Objekt(e) in Freiburg im Breisgau</title>", 12)]
    // The title of an unfiltered list names the count without a unit, only the heading is reliable
    [InlineData("<title>Mietangebote - Wohnungen 194</title><h1>Wohnungen ▸  194 Objekt(e)  für \"Mietangebote\"</h1>", 194)]
    public void GetTotalResultCount_ShouldReadTheHeadingOrTheTitle(string html, int expected)
    {
        _parser.GetTotalResultCount(Load(html)).Should().Be(expected);
    }

    [Theory]
    [InlineData("<title>Mietangebote - Wohnungen 194</title>")]
    [InlineData("<html><head><title>Just a moment...</title></head><body>Enable JavaScript and cookies to continue</body></html>")]
    [InlineData("")]
    public void GetTotalResultCount_ShouldNotInventACountForUnknownPages(string html)
    {
        _parser.GetTotalResultCount(Load(html)).Should().BeNull();
    }

    [Fact]
    public void ParseLinks_ShouldRejectChallengePagesWithoutInventingZeroResults()
    {
        var document = Load("<!DOCTYPE html><html lang=\"en-US\"><head><title>Just a moment...</title></head><body>Enable JavaScript and cookies to continue</body></html>");

        _parser.ParseLinks(document).Should().BeEmpty();
        _parser.GetTotalResultCount(document).Should().BeNull();
    }

    [Fact]
    public void ParseLinks_ShouldExplainTheRedirectOfTheWwwHost()
    {
        // www.wohnverdient.de redirects to http://wohnverdient.de, which HttpClient does not follow
        var logger = new Mock<ILogger>();
        var parser = new WohnverdientParser(logger.Object, Mock.Of<IQueryExecutor>());
        var document = Load("<html><head><title>301 Moved Permanently</title></head><body><center><h1>301 Moved Permanently</h1></center><hr><center>nginx</center></body></html>");

        parser.ParseLinks(document).Should().BeEmpty();

        logger.Verify(l => l.Warning(It.Is<string>(message => message.Contains("without www"))), Times.Once);
    }

    [Fact]
    public void GetAdditionalPages_ShouldNotFollowThePaginationBecauseRobotsTxtDisallowsIt()
    {
        var document = Load("""
            <ul class="pagination"><li class="current">1</li>
              <li><a href="/mietangebote/wohnungen/seite-2/">2</a></li>
              <li class="pagination-next"><a href="/mietangebote/wohnungen/seite-2/" rel="next">Weiter</a></li></ul>
            <link rel="next" href="/wohnungen//seite-2/" />
            """);

        _parser.GetAdditionalPages(document).Should().BeEmpty();
    }

    // Shortened copy of the detail page of an ad
    private const string DetailPage = """
        <html><body>
        <div class="cell large-6 medium-6 item-info-block"><div class="dtl-title"><h1>
        Helle 4-Zi-Wohnung, DG, Freiburg-Wiehre
        </h1><div class="item-location">
        Freiburg im Breisgau, Deutschland
        </div></div><div class="dtl-price">1.700 €</div></div>
        <div class="item-info">
        <div class="item-info-row"><div class="fa-lsize"><div class="grid-x"><div class="cell"><h2>Details</h2></div><div class="cell"><ul class="fa-no-bullet">
          <li><b>Wohnfläche:</b>&nbsp;
          125&nbsp;(m²)   </li><li><b>Anzahl Zimmer:</b>&nbsp;
          4   </li></ul></div></div></div></div>
        <div class="item-info-row"><div class="fa-lsize"><div class="grid-x"><div class="cell"><h2>Objektbeschreibung</h2></div><div class="cell"><ul class="fa-no-bullet">
          <li><b>Beschreibung:</b>&nbsp;
          Helle 4-Zi-Wohnung, schöne Raumaufteilung, Südbalkon.<br />
          Gerne an ruhiges Paar zu vermieten.   </li>
          <li><b>Lage:</b>&nbsp;
          Zähringen   </li>
          <li><b>Objekttyp:</b>&nbsp;
          Dachgeschoss   </li></ul></div></div></div></div>
        <div class="item-info-row"><div class="fa-lsize"><div class="grid-x"><div class="cell"><h2>Ausstattung</h2></div><div class="cell"><ul class="fa-no-bullet">
          <li><b>Ausstattung:</b>&nbsp;
          Balkon/Terrasse, Gäste-WC, Keller   </li></ul></div></div></div></div>
        <div class="item-info-row"><div class="fa-lsize"><div class="grid-x"><div class="cell"><h2>Weitere Details &amp; Energie</h2></div><div class="cell"><ul class="fa-no-bullet">
          <li><b>Letzte Modernisierung:</b>&nbsp; Komplettsanierung   </li></ul></div></div></div></div>
        </div>
        <div class="slider featured-item" id="similar-item"><div class="thumb"><div class="item-head"><h2 class="item-title">Zwei Freundinnen suchen ein</h2></div></div></div>
        </body></html>
        """;

    [Theory]
    // Title, place next to the title, free text of the ad, the district and the equipment
    [InlineData("Freiburg-Wiehre")]
    [InlineData("Freiburg im Breisgau")]
    [InlineData("Südbalkon")]
    [InlineData("Zähringen")]
    [InlineData("Gäste-WC")]
    [InlineData("zähringen|stühlinger")]
    public void IsMatch_ShouldSearchTheTitleThePlaceAndTheTextsOfTheAd(string includeKeyword)
    {
        var subscription = new Subscription { IncludeKeywords = new() { includeKeyword } };

        _parser.IsMatch(Load(DetailPage), subscription).Should().BeTrue();
    }

    [Theory]
    // Structured data and the other ads of the page are no text of the ad
    [InlineData("Anzahl Zimmer")]
    [InlineData("125")]
    [InlineData("Komplettsanierung")]
    [InlineData("Zwei Freundinnen")]
    public void IsMatch_ShouldIgnoreStructuredDataAndRecommendations(string keyword)
    {
        var include = new Subscription { IncludeKeywords = new() { keyword } };
        var exclude = new Subscription { ExcludeKeywords = new() { keyword } };

        _parser.IsMatch(Load(DetailPage), include).Should().BeFalse();
        _parser.IsMatch(Load(DetailPage), exclude).Should().BeTrue();
    }

    [Fact]
    public void IsMatch_ShouldApplyExcludeKeywordsAndKeepWordsApartAcrossLineBreaks()
    {
        var excluded = new Subscription { ExcludeKeywords = new() { "ruhiges Paar" } };
        var included = new Subscription { IncludeKeywords = new() { "Südbalkon. Gerne an ruhiges Paar" } };

        _parser.IsMatch(Load(DetailPage), excluded).Should().BeFalse();
        _parser.IsMatch(Load(DetailPage), included).Should().BeTrue();
    }

    [Theory]
    // Advertisers may name the street and the postal code, which is the only place of the district keywords of a search by postal code
    [InlineData("79108")]
    [InlineData("Denzlinger Straße")]
    [InlineData("79104|79108")]
    public void IsMatch_ShouldSearchTheAddressOfTheAd(string includeKeyword)
    {
        var document = Load("""
            <div class="dtl-title"><h1>Panoramablick</h1><div class="item-location">
              Denzlinger Straße , 79108 Freiburg im Breisgau, Deutschland
            </div></div>
            <div class="item-info-row"><h2>Objektbeschreibung</h2><ul><li><b>Beschreibung:</b>&nbsp; Erstvermietung nach Sanierung: 10 OG, 4 Zimmer</li></ul></div>
            """);

        _parser.IsMatch(document, new Subscription { IncludeKeywords = new() { includeKeyword } }).Should().BeTrue();
        _parser.IsMatch(document, new Subscription { IncludeKeywords = new() { "79104" } }).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_ShouldNotMatchAPageWithoutTitleBecauseDeletedAdsRedirectToTheCategoryList()
    {
        // The list page has a heading and cards, but no title of an ad
        var subscription = new Subscription();

        _parser.IsMatch(Load(ListPage), subscription).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_ShouldNotMatchAnAdWithoutText()
    {
        var document = Load("<div class='dtl-title'><h1>Wohnung</h1><div class='item-location'>Freiburg</div></div>");

        _parser.IsMatch(document, new Subscription { IncludeKeywords = new() { "Freiburg" } }).Should().BeFalse();
    }

    private static HtmlDocument Load(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }
}
