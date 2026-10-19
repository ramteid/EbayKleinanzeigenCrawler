using System;
using System.Linq;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Parser;
using KleinanzeigenCrawler.Parser.Implementations;
using Moq;
using Serilog;
using Xunit;

namespace KleinanzeigenCrawler.Tests.Parser;

public class CurrentLayoutParserTests
{
    [Fact]
    public void Kleinanzeigen_ShouldParseCardsWithoutImageLinksAndIgnoreDuplicatesAndUnrelatedArticles()
    {
        var parser = CreateParser(true);
        var document = Load(@"
            <article data-adid='999' data-href='/s-anzeige/unrelated/999-1-1'></article>
            <ul id='srchrslt-adtable'>
              <li><article class='p-medium flex' data-adid='123' data-href='/s-anzeige/wohnung/123-203-1'>
                <div><p class='text-secondary extra font-strong text-title3'>1.200 &euro;</p>
                <p class='text-title3 line-through'>1.500 €</p><time>Heute, 12:00</time></div>
              </article></li>
              <li><article data-adid='123' data-href='/s-anzeige/wohnung/123-203-1'></article></li>
              <li><article class='not-aditem'><a href='/s-anzeige/noise/9-1-1'>Noise</a></article></li>
            </ul>");

        var results = parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Link.AbsoluteUri.Should().Be("https://www.kleinanzeigen.de/s-anzeige/wohnung/123-203-1");
        results[0].Price.Should().Be("1.200 €");
        results[0].CreationDate.Should().Be("Heute, 12:00");
    }

    [Fact]
    public void Kleinanzeigen_ShouldParseLegacyCardsAndSkipPromotedShopCards()
    {
        var parser = CreateParser(true);
        var document = Load(@"
            <ul id='srchrslt-adtable'>
              <li><article class='extra aditem'><div class='extra aditem-image'>
                <a href='/s-anzeige/wohnung/123-203-1'>Wohnung</a></div>
                <p class='extra aditem-main--middle--price-shipping--price'>VB</p></article></li>
              <li><article class='aditem' data-href='/s-anzeige/shop/456-203-1'>
                <div class='badge-hint-pro-small-srp'></div></article></li>
            </ul>");

        var results = parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Price.Should().Be("VB");
        results[0].Link.AbsolutePath.Should().Be("/s-anzeige/wohnung/123-203-1");
    }

    [Fact]
    public void WgGesucht_ShouldParseAnchorCardsWithReorderedClassesAndIgnoreRecommendations()
    {
        var parser = CreateParser(false);
        var document = Load(@"
            <a class='wgg-list-card offer_list_item' href='/wohnungen-in-Berlin.999.html'></a>
            <main id='main_column'>
              <a class='extra offer_list_item  wgg-list-card' href='/wohnungen-in-Freiburg.123.html'>
                <div class='price-row noprint'><span class='extra card-main-text'>3.050 &euro;</span></div>
                <div><span class='online-since-icon' title='Online:'>update</span>
                     <span class='card-subtitle'>13 Std.</span></div>
              </a>
              <a class='wgg-list-card offer_list_item' href='/wohnungen-in-Freiburg.123.html'></a>
              <a class='wgg-list-card-extra offer_list_item' href='/wohnungen-in-Berlin.456.html'></a>
            </main>");

        var results = parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Link.AbsoluteUri.Should().Be("https://www.wg-gesucht.de/wohnungen-in-Freiburg.123.html");
        results[0].Price.Should().Be("3.050 €");
        results[0].CreationDate.Should().Be("Online: 13 Std.");
    }

    [Theory]
    [InlineData(true, "https://evil.invalid/s-anzeige/test/123-1-1")]
    [InlineData(true, "//evil.invalid/s-anzeige/test/123-1-1")]
    [InlineData(true, "javascript:alert(1)")]
    [InlineData(true, "http://www.kleinanzeigen.de/s-anzeige/test/123-1-1")]
    [InlineData(true, "https://www.kleinanzeigen.de.evil.invalid/s-anzeige/test/123-1-1")]
    [InlineData(true, "https://user@www.kleinanzeigen.de/s-anzeige/test/123-1-1")]
    [InlineData(true, "/s-test/k0")]
    [InlineData(true, "")]
    [InlineData(false, "https://evil.invalid/wohnungen.123.html")]
    [InlineData(false, "//evil.invalid/wohnungen.123.html")]
    [InlineData(false, "javascript:alert(1)")]
    [InlineData(false, "http://www.wg-gesucht.de/wohnungen.123.html")]
    [InlineData(false, "https://www.wg-gesucht.de.evil.invalid/wohnungen.123.html")]
    [InlineData(false, "/wohnungen-in-Freiburg.43.2.1.0.html")]
    [InlineData(false, "/anbieter.html")]
    [InlineData(false, "")]
    public void ParseLinks_ShouldSkipInvalidLinksAndContinueWithTheNextCard(bool kleinanzeigen, string href)
    {
        var parser = CreateParser(kleinanzeigen);
        var document = Load(kleinanzeigen
            ? $"<ul id='srchrslt-adtable'><article data-adid='1' data-href='{href}'></article><article data-adid='2' data-href='/s-anzeige/valid/2-1-1'></article></ul>"
            : $"<main id='main_column'><a class='wgg-list-card offer_list_item' href='{href}'></a><a class='wgg-list-card offer_list_item' href='/wohnungen-in-Freiburg.123.html'></a></main>");

        var results = parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Link.AbsolutePath.Should().Be(kleinanzeigen ? "/s-anzeige/valid/2-1-1" : "/wohnungen-in-Freiburg.123.html");
        results[0].Price.Should().BeEmpty();
        results[0].CreationDate.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "<span id='srp-breadcrumb-summary'>1 - 25 von 1.234 Ergebnissen für test</span>", 1234)]
    [InlineData(true, "<span id='srp-breadcrumb-summary'>1 von 1 Ergebnis für test</span>", 1)]
    [InlineData(true, "<span id='srp-breadcrumb-summary'>1 - 25 von 327 Mietwohnungen in Freiburg</span>", 327)]
    [InlineData(true, "<span id='srp-breadcrumb-summary'>Es wurden keine Ergebnisse für test gefunden,</span>", 0)]
    [InlineData(false, "<div id='main_column'><h1>Mietwohnungen</h1><h2>2 Wohnungen | 123 Angebote in allen Kategorien</h2></div>", 2)]
    [InlineData(false, "<div id='main_column'><h1>Wohnungen</h1><h2>0 Wohnungen | 3 Angebote in allen Kategorien</h2></div>", 0)]
    [InlineData(false, "<div id='main_column'><h1>Häuser</h1><h2>1 Haus | 3 Angebote in allen Kategorien</h2></div>", 1)]
    public void GetTotalResultCount_ShouldUseTheSelectedCategory(bool kleinanzeigen, string html, int expected)
    {
        var parser = CreateParser(kleinanzeigen);

        var count = parser.GetTotalResultCount(Load(html));

        count.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseLinks_ShouldRejectChallengePagesWithoutInventingZeroResults(bool kleinanzeigen)
    {
        var parser = CreateParser(kleinanzeigen);
        var document = Load("<html><title>Security verification</title><body>Please enable JavaScript</body></html>");

        var results = parser.ParseLinks(document).ToList();
        var count = parser.GetTotalResultCount(document);

        results.Should().BeEmpty();
        count.Should().BeNull();
    }

    [Fact]
    public void Kleinanzeigen_ShouldOnlyFollowUniqueSameSitePaginationLinks()
    {
        var parser = CreateParser(true);
        var document = Load(@"
            <a class='extra pagination-page' href='/s-wohnung/seite:2/k0'>2</a>
            <a rel='next' href='/s-wohnung/seite:2/k0'>Next</a>
            <a aria-label='Seite 2' href='/s-wohnung/seite:2/k0'>2</a>
            <a aria-label='Nächste' href='/s-wohnung/seite:2/k0'>Next</a>
            <a class='pagination-page' href='https://evil.invalid/s-wohnung/k0'>Bad</a>
            <a rel='next' href='/s-anzeige/advert/123-1-1'>Ad</a>");

        var pages = parser.GetAdditionalPages(document);

        pages.Should().ContainSingle();
        pages[0].AbsolutePath.Should().Be("/s-wohnung/seite:2/k0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsMatch_ShouldReadDetailTitlesDescriptionsAddressesAndEntities(bool kleinanzeigen)
    {
        var parser = CreateParser(kleinanzeigen);
        var document = Load(kleinanzeigen
            ? "<h1 id='viewad-title'>Wohnung &amp; Garten</h1><div id='viewad-description-text'>Schöne Wohnung<br>in Gundelfingen</div>"
            : "<h1 class='extra detailed-view-title'><span>Wohnung &amp; Garten</span></h1><a href='#map_container'><span>79194<br>Gundelfingen</span></a><div id='ad_description_text'><div id='freitext_0'>Schöne Wohnung<br>mit Terrasse</div></div>");
        var subscription = new Subscription { IncludeKeywords = new() { "Wohnung & Garten", "Gundelfingen", "Schöne Wohnung" } };

        var matches = parser.IsMatch(document, subscription);
        subscription.ExcludeKeywords.Add("Schöne Wohnung");
        var excluded = parser.IsMatch(document, subscription);

        matches.Should().BeTrue();
        excluded.Should().BeFalse();
    }

    [Fact]
    public void WgGesucht_ShouldAcceptDotsInDistrictNames()
    {
        var parser = CreateParser(false);
        var document = Load("<a class='wgg-list-card offer_list_item' href='/wohnungen-in-Freiburg-St.-Georgen.123.html'></a>");

        var results = parser.ParseLinks(document).ToList();

        results.Should().ContainSingle();
        results[0].Link.AbsolutePath.Should().Be("/wohnungen-in-Freiburg-St.-Georgen.123.html");
    }

    [Fact]
    public void WgGesucht_ShouldFollowUniquePaginationLinksAndPreserveFilters()
    {
        var parser = CreateParser(false);
        var document = Load(@"
            <meta property='og:url' content='https://www.wg-gesucht.de/wohnungen-in-Freiburg.43.2.1.0.html'>
            <a class='black_text_link' href='/wohnungen-in-Berlin.8.2.1.1.html'>Outside</a>
            <main id='main_column'>
              <a class='black_text_link' href='/wohnungen-in-Berlin.8.2.1.1.html'>Other search</a>
              <a class='extra black_text_link' href='/wohnungen-in-Freiburg.43.2.1.1.html?sMin=50&amp;city_id=43'>2</a>
              <a class='black_text_link' href='/wohnungen-in-Freiburg.43.2.1.1.html?sMin=50&amp;city_id=43'>Next</a>
              <a class='black_text_link' href='https://evil.invalid/wohnungen.43.2.1.2.html'>Bad</a>
              <a class='black_text_link' href='/wohnungen-in-Freiburg.43.2.1.0.html'>1</a>
              <a class='black_text_link' href='/wohnungen-in-Freiburg.123.html'>Ad</a>
            </main>");

        var pages = parser.GetAdditionalPages(document);

        pages.Should().ContainSingle();
        pages[0].AbsoluteUri.Should().Be("https://www.wg-gesucht.de/wohnungen-in-Freiburg.43.2.1.1.html?sMin=50&city_id=43");
    }

    [Fact]
    public void Kleinanzeigen_ShouldPreserveInlineWordsAndIgnoreNonContentText()
    {
        var parser = CreateParser(true);
        var document = Load("<h1 id='viewad-title'>Gundel<b>fingen</b></h1><div id='viewad-description-text'><p>Garten</p>Terrasse<script>verboten</script><style>verboten</style><!-- verboten --></div>");
        var subscription = new Subscription { IncludeKeywords = new() { "Gundelfingen", "Garten Terrasse" }, ExcludeKeywords = new() { "verboten" } };

        var matches = parser.IsMatch(document, subscription);

        matches.Should().BeTrue();
    }

    [Fact]
    public void WgGesucht_ShouldNotMatchAnAddressWhenTheDescriptionIsMissing()
    {
        var parser = CreateParser(false);
        var document = Load("<h1 class='detailed-view-title'>Wohnung</h1><a href='#map_container'>Gundelfingen</a>");

        var matches = parser.IsMatch(document, new Subscription { IncludeKeywords = new() { "Gundelfingen" } });

        matches.Should().BeFalse();
    }

    private static ParserBase CreateParser(bool kleinanzeigen) => kleinanzeigen
        ? new KleinanzeigenParser(Mock.Of<ILogger>(), Mock.Of<IQueryExecutor>())
        : new WgGesuchtParser(Mock.Of<ILogger>(), Mock.Of<IQueryExecutor>());

    private static HtmlDocument Load(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
    }
}
