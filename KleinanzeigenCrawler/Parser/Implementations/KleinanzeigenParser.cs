using System;
using System.Collections.Generic;
using System.Linq;
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

        return true;
    }

    protected override bool ShouldSkipResult(HtmlNode result)
    {
        var isProShopLink =
            result
                .Descendants("div")
                .Any(d => d.Attributes.Any(a => a.Value == "badge-hint-pro-small-srp"))
            || result
                .Descendants("i")
                .Any(d => d.Attributes.Any(a => a.Value.Contains("icon-feature-topad")));

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
            .Where(dx => dx.Attributes.Any(ab => ab.Name == "class" && ab.Value == "pagination-page"))
            .SelectMany(d => d.Attributes.Where(a => a.Name == "href"))
            .Select(d => d.Value)
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => 
            {
                if (l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(l);
                }
                return new Uri($"{BaseUrl}{l}");
            })
            .ToList();
        return additionalPages;
    }

    protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
    {
        return resultPage.DocumentNode
            .SelectNodes("//ul[@id='srchrslt-adtable']")?
            .Descendants("article")
            .Where(div => div.GetAttributeValue("class", "").Contains("aditem"))
            .ToList();
    }

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var link = result
            .SelectNodes("div[@class='aditem-image']//a")?
            .Select(n => n.Attributes.SingleOrDefault(a => a.Name == "href"))
            .Where(l => l is not null)
            .Select(l => 
            {
                var url = l.Value;
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(url);
                }
                return new Uri($"{BaseUrl}{url}");
            })
            .FirstOrDefault();

        return link;
    }

    protected override string ParseResultDate(HtmlNode result)
    {
        var date = result
            .SelectNodes("div/div/div[@class='aditem-main--top--right']")?
            .Select(d => d.InnerText)
            .SingleOrDefault()?
            .Trim();
        return date;
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        var price = result
            .SelectNodes("div/div/div/p[@class='aditem-main--middle--price-shipping--price']")?
            .Select(d => d.InnerText)
            .SingleOrDefault()?
            .Trim();
        return price;
    }

    protected override string ParseTitle(HtmlDocument document)
    {
        if (document.ParsedText.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return null;
        }

        var titleNode = document.DocumentNode
            .Descendants("h1")
            .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-title"));

        return titleNode != null ? System.Net.WebUtility.HtmlDecode(titleNode.InnerText).Trim() : null;
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        var descriptionNode = document.DocumentNode
            .Descendants("p")
            .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-description-text"));

        return descriptionNode != null ? System.Net.WebUtility.HtmlDecode(descriptionNode.InnerText).Trim() : null;
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
            var breadcrumbNode = document.DocumentNode
                .Descendants("span")
                .FirstOrDefault(span => span.GetAttributeValue("class", "").Contains("breadcrump-summary"));

            if (breadcrumbNode == null)
            {
                Logger.Verbose("Breadcrumb summary not found");
                return null;
            }

            var text = breadcrumbNode.InnerText; // e.g., "1 - 25 von 47.981 Ergebnissen für „pc spiele" in Deutschland"
            
            // Extract the number after "von" and before "Ergebnis" or "Ergebnissen"
            var vonIndex = text.IndexOf("von", StringComparison.OrdinalIgnoreCase);
            var ergebnisIndex = text.IndexOf("Ergebnis", StringComparison.OrdinalIgnoreCase);
            
            if (vonIndex == -1 || ergebnisIndex == -1 || vonIndex >= ergebnisIndex)
            {
                Logger.Warning($"Could not parse breadcrumb format: {text}");
                return null;
            }

            var length = ergebnisIndex - vonIndex - 3;
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
