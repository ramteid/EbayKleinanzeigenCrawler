using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Monitoring;
using Serilog;

namespace KleinanzeigenCrawler.Parser.Implementations;

public class KleinanzeigenParser : ParserBase
{
    private const string BaseUrl = "https://www.kleinanzeigen.de";
    
    /// <summary>
    /// This interval is used by Kleinanzeigen. They only allow 40 queries every 5 minutes. Above that, they obfuscate their HTML.
    /// To make sure not to exceed this limit, it is hard-coded here.
    /// </summary>
    protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
    protected override uint AllowedRequestsPerTimespan => 40;
    protected override string InvalidHtml => "<html><head><meta charset=\"utf-8\"><script>";

    public KleinanzeigenParser(ILogger logger, IQueryExecutor queryExecutor, MetricsService metricsService = null) 
        : base(logger, queryExecutor, metricsService) { }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        // TODO: An URL with query params seems to forget the query keywords and searches for anything. Do not allow ? in URLs.
        // e.g. /s-anzeigen/96123/anzeige:angebote/lescha-betonmischer/c0-l6895?distance=50&maxPrice=100
        if (resultPage.ParsedText.StartsWith(InvalidHtml) || resultPage.ParsedText.Contains("429 Too many requests from"))
        {
            Logger.Warning("Invalid HTML detected");
            return false;
        }

        if (resultPage.ParsedText.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return false;
        }

        if (ParseResults(resultPage)?.Count > 0 || GetTotalResultCount(resultPage) == 0)
        {
            return true;
        }

        Logger.Warning("Search page contains neither recognized result cards nor an explicit zero result count");
        return false;
    }

    protected override bool ShouldSkipResult(HtmlNode result)
    {
        var isProShopLink =
            result
                .Descendants("div")
                .Any(d => HasClass(d, "badge-hint-pro-small-srp"))
            || result
                .Descendants("i")
                .Any(d => HasClass(d, "icon-feature-topad"));

        if (isProShopLink)
        {
            // Filter out Pro-Shop links
            Logger.Verbose("Skipping Pro-Shop link");
            return true;
        }

        return false;
    }

    public override List<Uri> GetAdditionalPages(HtmlDocument document)
    {
        var additionalPages = document.DocumentNode.Descendants("a")
            .Where(dx => HasClass(dx, "pagination-page") || dx.GetAttributeValue("rel", "") == "next"
                || dx.GetAttributeValue("aria-label", "").StartsWith("Seite ", StringComparison.Ordinal)
                || dx.GetAttributeValue("aria-label", "") == "Nächste")
            .SelectMany(d => d.Attributes.Where(a => a.Name == "href"))
            .Select(d => d.Value)
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => ResolveLink(BaseUrl, l))
            .Where(l => l != null && l.AbsolutePath.StartsWith("/s-", StringComparison.Ordinal)
                && !l.AbsolutePath.StartsWith("/s-anzeige/", StringComparison.Ordinal)
                && Regex.IsMatch(l.AbsolutePath, @"/seite:\d+/", RegexOptions.CultureInvariant))
            .Distinct()
            .ToList();
        return additionalPages;
    }

    protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
    {
        return resultPage.DocumentNode
            .SelectNodes("//ul[@id='srchrslt-adtable']")?
            .Descendants("article")
            .Where(article => HasClass(article, "aditem") || article.Attributes["data-adid"] != null)
            .ToList();
    }

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var href = result.GetAttributeValue("data-href", null)
            ?? result.SelectSingleNode(".//h3//a[@href] | .//div[contains(concat(' ', normalize-space(@class), ' '), ' aditem-image ')]//a[@href]")?.GetAttributeValue("href", null);
        var link = ResolveLink(BaseUrl, href);
        return link?.AbsolutePath.StartsWith("/s-anzeige/", StringComparison.Ordinal) == true ? link : null;
    }

    protected override string ParseResultDate(HtmlNode result)
    {
        return ReadText(result.SelectSingleNode(".//time | .//*[contains(concat(' ', normalize-space(@class), ' '), ' aditem-main--top--right ')]"));
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        return ReadText(result.Descendants("p").FirstOrDefault(node =>
            HasClass(node, "aditem-main--middle--price-shipping--price")
            || HasClass(node, "text-title3") && HasClass(node, "font-strong") && HasClass(node, "text-secondary")));
    }

    protected override string ParseTitle(HtmlDocument document)
    {
        if (document.ParsedText.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return null;
        }

        var titleNode = document.DocumentNode.SelectSingleNode("//h1[@id='viewad-title']");

        return ReadText(titleNode);
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        var descriptionNode = document.DocumentNode
            .SelectSingleNode("//*[@id='viewad-description-text']");

        return ReadText(descriptionNode);
    }

    /// <summary>
    /// Parses the total number of results from the breadcrumb summary.
    /// Examples: 
    ///   "1 - 25 von 47.981 Ergebnissen für „pc spiele" in Deutschland"
    ///   "1 von 1 Ergebnis für „rare item" in Deutschland"
    /// </summary>
    public override int? GetTotalResultCount(HtmlDocument document)
    {
        try
        {
            var breadcrumbNode = document.DocumentNode.SelectSingleNode("//*[@id='srp-breadcrumb-summary']")
                ?? document.DocumentNode
                .Descendants("span")
                .FirstOrDefault(span => span.GetAttributeValue("class", "").Contains("breadcrump-summary"));

            if (breadcrumbNode == null)
            {
                Logger.Verbose("Breadcrumb summary not found");
                return null;
            }

            var text = ReadText(breadcrumbNode); // e.g., "1 - 25 von 47.981 Ergebnissen für „pc spiele" in Deutschland"
            if (text.StartsWith("Es wurden keine Ergebnisse", StringComparison.Ordinal))
            {
                return 0;
            }
            
            // Extract the number after "von" and before the result or category label
            var vonIndex = text.IndexOf("von", StringComparison.OrdinalIgnoreCase);
            var resultLabelIndex = vonIndex < 0 ? -1 : text.IndexOf(' ', Math.Min(vonIndex + 4, text.Length));
            
            if (vonIndex == -1 || resultLabelIndex == -1 || vonIndex >= resultLabelIndex)
            {
                Logger.Warning($"Could not parse breadcrumb format: {text}");
                return null;
            }

            var length = resultLabelIndex - vonIndex - 3;
            if (length < 0)
            {
                Logger.Warning($"Invalid substring bounds in breadcrumb: {text}");
                return null;
            }
            var numberText = text.Substring(vonIndex + 3, length).Trim();
            
            // Handle edge case where numberText might be empty or contain only whitespace before removing separators
            if (string.IsNullOrWhiteSpace(numberText))
            {
                Logger.Warning($"Empty number text extracted from breadcrumb: {text}");
                return null;
            }
            
            // Remove thousand separators (dots)
            numberText = numberText.Replace(".", "").Replace(",", "");
            
            if (int.TryParse(numberText, out var count))
            {
                Logger.Information($"Parsed total result count: {count}");
                return count;
            }
            
            Logger.Warning($"Could not parse number from breadcrumb: {numberText}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Error parsing total result count from breadcrumb");
            return null;
        }
    }
}
