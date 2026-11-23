using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Monitoring;
using Serilog;

namespace KleinanzeigenCrawler.Parser.Implementations;

/// <summary>
/// Parser for wohnverdient.de, the real estate portal of the Badische Zeitung.
/// </summary>
public class WohnverdientParser : ParserBase
{
    // www.wohnverdient.de answers with a redirect to plain http, which HttpClient refuses to follow, so the bare domain is the canonical host
    private const string BaseUrl = "https://wohnverdient.de";

    // The detail pages split the text of an ad into several fields. These sections hold what the author wrote,
    // the others (price, rooms, living space, energy certificate, ...) are structured data and would only produce false keyword matches.
    private static readonly string[] DescriptionSections = ["Objektbeschreibung", "Ausstattung"];

    // wohnverdient.de does not publish a request limit and answers normal browsers without any challenge.
    // The budget is below the one of the other providers because the site is much smaller than Kleinanzeigen.
    protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
    protected override uint AllowedRequestsPerTimespan => 30;

    // wohnverdient.de is behind Cloudflare. A challenge page starts like this, so it is never mistaken for a result page.
    protected override string InvalidHtml => "<!DOCTYPE html><html lang=\"en-US\"><head><title>Just a moment...</title>";

    public WohnverdientParser(ILogger logger, IQueryExecutor queryExecutor, MetricsService metricsService = null)
        : base(logger, queryExecutor, metricsService) { }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        if (ParseResults(resultPage)?.Count > 0 || GetTotalResultCount(resultPage) == 0)
        {
            return true;
        }

        var text = resultPage.ParsedText;
        Logger.Warning(text.Contains("Just a moment...", StringComparison.Ordinal)
            ? "Cloudflare answered with a challenge page instead of the search page"
            : text.Contains("301 Moved Permanently", StringComparison.Ordinal)
                ? "The search URL was answered with a redirect to plain http, which is not followed. Use the URL without www."
                : "Search page contains neither recognized result cards nor an explicit zero result count");
        return false;
    }

    /// <summary>
    /// Only the first page is read. Its URL carries the sorting by publication date, so the newest ads are on it.
    /// robots.txt of wohnverdient.de disallows the pagination (/seite-N/, /page-N), so it is not followed.
    /// </summary>
    public override List<Uri> GetAdditionalPages(HtmlDocument document) => new();

    protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
    {
        // Other places on the page (recommendations, sliders) contain links to ads as well. Only the cards of the result list have this id.
        return resultPage.DocumentNode
            .SelectNodes("//div[starts-with(@id, 'item_card_') and contains(concat(' ', normalize-space(@class), ' '), ' listing-item ')]")?
            .ToList();
    }

    protected override bool ShouldSkipResult(HtmlNode result) => false;

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var href = result.SelectSingleNode(".//a[@href][contains(concat(' ', normalize-space(@class), ' '), ' tricky-link ')]")
            ?.GetAttributeValue("href", null);
        var link = ResolveLink(BaseUrl, href);
        // /<category>/<location>/<title>-<id>, as opposed to category pages, advertiser pages and so on
        return link != null && Regex.IsMatch(link.AbsolutePath, @"^/[^/]+/[^/]+/[^/]+-\d+$", RegexOptions.CultureInvariant) ? link : null;
    }

    protected override string ParseResultDate(HtmlNode result)
    {
        // The list view and the gallery view both show "<b>Veröffentlicht am:</b> 21.11.26"
        var label = result.Descendants("b").FirstOrDefault(b => ReadText(b).StartsWith("Veröffentlicht am", StringComparison.Ordinal));
        return label == null ? null : ReadText(label.ParentNode);
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        return ReadText(result.SelectSingleNode(".//p[contains(concat(' ', normalize-space(@class), ' '), ' price ')]"));
    }

    protected override string ParseTitle(HtmlDocument document)
    {
        var titleNode = document.DocumentNode.SelectSingleNode("//*[contains(concat(' ', normalize-space(@class), ' '), ' dtl-title ')]/h1");
        if (titleNode == null)
        {
            // An ad that does not exist anymore is answered with a redirect to the category list, which has no such title
            Logger.Warning("Ad page has no title. The ad probably does not exist anymore.");
            return null;
        }

        return ReadText(titleNode);
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        var sections = document.DocumentNode
            .SelectNodes("//*[contains(concat(' ', normalize-space(@class), ' '), ' item-info-row ')]")?
            .Where(row => DescriptionSections.Contains(ReadText(row.SelectSingleNode(".//h2")), StringComparer.Ordinal))
            .Select(ReadText)
            .Where(text => text.Length > 0)
            .ToList();

        if (sections == null || sections.Count == 0)
        {
            return "";
        }

        // The place is shown next to the title and is often the only hint at the district in addition to the text of the ad
        var location = ReadText(document.DocumentNode.SelectSingleNode("//*[contains(concat(' ', normalize-space(@class), ' '), ' item-location ')]"));
        return location + " " + string.Join(" ", sections);
    }

    /// <summary>
    /// Parses the total number of results from the heading of the list or from the page title.
    /// Examples: "Wohnungen ▸ 10 Objekt(e) für "Mietangebote" in 79106 Freiburg im Breisgau, Deutschland" (heading),
    /// "Mietangebote - Wohnungen 0 Objekt(e) in Freiburg im Breisgau" (title, the only place that names a count of 0).
    /// </summary>
    public override int? GetTotalResultCount(HtmlDocument document)
    {
        var heading = ReadText(document.DocumentNode.SelectSingleNode("//h1"));
        var title = ReadText(document.DocumentNode.SelectSingleNode("//title"));
        foreach (var text in new[] { heading, title })
        {
            var match = Regex.Match(text, @"(?<![\d.])(\d{1,3}(?:\.\d{3})*|\d+)\s+Objekt\(e\)", RegexOptions.CultureInvariant);
            if (match.Success && int.TryParse(match.Groups[1].Value.Replace(".", ""), out var total))
            {
                return total;
            }
        }

        Logger.Verbose("No result count found on the page");
        return null;
    }
}
