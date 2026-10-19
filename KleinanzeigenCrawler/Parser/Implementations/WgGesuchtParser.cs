using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Monitoring;
using Serilog;

namespace KleinanzeigenCrawler.Parser.Implementations;

public class WgGesuchtParser : ParserBase
{
    private const string BaseUrl = "https://www.wg-gesucht.de";
    protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
    protected override uint AllowedRequestsPerTimespan => 40;
    protected override string InvalidHtml => "n/a";

    public WgGesuchtParser(ILogger logger, IQueryExecutor queryExecutor, MetricsService metricsService = null) 
        : base(logger, queryExecutor, metricsService) { }

    public override List<Uri> GetAdditionalPages(HtmlDocument document)
    {
        // Pagination links carry the active search filters in their query string.
        var canonical = ResolveLink(BaseUrl, document.DocumentNode.SelectSingleNode("//meta[@property='og:url']")?.GetAttributeValue("content", null));
        var searchPrefix = canonical == null ? null : Regex.Replace(canonical.AbsolutePath, @"\.\d+\.html$", ".");
        return document.DocumentNode.SelectNodes("//*[@id='main_column']//a[@href]")?
            .Where(node => HasClass(node, "black_text_link"))
            .Select(node => ResolveLink(BaseUrl, node.GetAttributeValue("href", "")))
            .Where(link => link != null && Regex.IsMatch(link.AbsolutePath, @"\.\d+\.\d+\.\d+\.[1-9]\d*\.html$", RegexOptions.CultureInvariant))
            .Where(link => searchPrefix == null || link.AbsolutePath.StartsWith(searchPrefix, StringComparison.Ordinal))
            .Distinct()
            .ToList() ?? new List<Uri>();
    }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        if (ParseResults(resultPage)?.Count > 0 || GetTotalResultCount(resultPage) == 0)
        {
            return true;
        }

        Logger.Warning("Search page contains neither recognized result cards nor an explicit zero result count");
        return false;
    }

    protected override bool ShouldSkipResult(HtmlNode result)
    {
        // premium ads apprently lead to a seller page with multiple ads, which is currently not supported  
        var isPremiumAd = result
            .Attributes
            .Any(a => a.Name == "onclick" && a.Value.Contains("premium sticky ad"));
        return isPremiumAd;
    }

    protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
    {
        var container = resultPage.DocumentNode.SelectSingleNode("//*[@id='main_column']") ?? resultPage.DocumentNode;
        return container
            .SelectNodes(".//*[contains(concat(' ', normalize-space(@class), ' '), ' offer_list_item ')]")?
            .Where(node => node.Name == "a" && HasClass(node, "wgg-list-card")
                || node.Name == "div" && HasClass(node, "wgg_card"))
            .ToList();
    }

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var href = result.Name == "a" ? result.GetAttributeValue("href", null)
            : result.SelectSingleNode(".//h3/a[@href]")?.GetAttributeValue("href", null);
        var link = ResolveLink(BaseUrl, href);
        return link != null && Regex.IsMatch(link.AbsolutePath, @"^/(?!.*\.\d+\.\d+\.\d+\.\d+\.html$)[^/]+\.\d+\.html$", RegexOptions.CultureInvariant) ? link : null;
    }

    protected override string ParseResultDate(HtmlNode result)
    {
        var onlineIcon = result.Descendants("span").FirstOrDefault(node => HasClass(node, "online-since-icon"));
        if (onlineIcon != null)
        {
            var date = ReadText(onlineIcon.SelectSingleNode("following-sibling::span[1]"));
            return date.Length == 0 ? "" : "Online: " + date;
        }

        var dateNode = result
            .SelectNodes(".//div/span")?
            .FirstOrDefault(s => s.InnerText.Contains("Online:"));

        if (dateNode == null)
        {
            return null;
        }

        return ReadText(dateNode);
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        // Check for both decoded (€) and encoded (&euro;) forms
        // HtmlAgilityPack may or may not decode entities depending on how HTML is loaded
        var priceNode = result.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' price-row ')]//*[contains(concat(' ', normalize-space(@class), ' '), ' card-main-text ')]")
            ?? result
            .SelectNodes(".//div/b")?
            .FirstOrDefault(s => s.InnerText.Contains('€') || s.InnerText.Contains("&euro;"));

        if (priceNode == null)
        {
            return null;
        }

        return ReadText(priceNode);
    }

    protected override string ParseTitle(HtmlDocument document)
    {
        return ReadText(document.DocumentNode.SelectSingleNode(".//h1[@id='sliderTopTitle' or contains(concat(' ', normalize-space(@class), ' '), ' detailed-view-title ')]"));
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        var addressNode = document.DocumentNode.SelectSingleNode(".//a[@href='#map_container']")
            ?? document.DocumentNode
            .SelectNodes(".//div[contains(@class, 'col-sm-4') and contains(@class, 'mb10')]")?
            .FirstOrDefault();

        var descriptionNode = document.DocumentNode.SelectSingleNode(".//*[@id='ad_description_text'] | .//*[@id='freitext_0_content']");
        var description = ReadText(descriptionNode);
        if (string.IsNullOrWhiteSpace(description))
        {
            return "";
        }

        return ReadText(addressNode) + " " + description;
    }

    /// <summary>
    /// Parses the total number of results from the list view title.
    /// Example: "WG-Zimmer, 1-Zimmer-Wohnungen, Mietwohnungen, Häuser in Freiburg im Breisgau: 0 Angebote"
    /// or "... in Freiburg im Breisgau: 123 Angebote"
    /// </summary>
    public override int? GetTotalResultCount(HtmlDocument document)
    {
        try
        {
            var summary = document.DocumentNode.SelectSingleNode("//*[@id='main_column']//h1/following-sibling::h2[1]");
            if (summary != null)
            {
                var match = Regex.Match(ReadText(summary), @"^([\d.]+)\s+(?:Wohnungen?|WG-Zimmer|1-Zimmer-Wohnungen?|Häuser|Haus|Angebote?)\b", RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups[1].Value.Replace(".", ""), out var total))
                {
                    return total;
                }
            }

            var titleNode = document.DocumentNode
                .Descendants("h1")
                .FirstOrDefault(h1 => h1.GetAttributeValue("class", "").Contains("list-view-title"));

            if (titleNode == null)
            {
                Logger.Verbose("List view title not found");
                return null;
            }

            var text = titleNode.InnerText.Trim(); // e.g., "... in Freiburg im Breisgau: 123 Angebote"
            
            // Extract the number before "Angebote" (or "Angebot" for singular)
            var angeboteIndex = text.LastIndexOf("Angebot", StringComparison.OrdinalIgnoreCase);
            
            if (angeboteIndex == -1)
            {
                Logger.Warning($"Could not find 'Angebot' in list view title: {text}");
                return null;
            }

            // Find the colon before the number
            var colonIndex = text.LastIndexOf(':', angeboteIndex);
            
            if (colonIndex == -1)
            {
                Logger.Warning($"Could not find ':' in list view title: {text}");
                return null;
            }

            // Extract text between colon and "Angebot"
            var length = angeboteIndex - colonIndex - 1;
            if (length < 0)
            {
                Logger.Warning($"Invalid substring bounds in list view title: {text}");
                return null;
            }
            var numberText = text.Substring(colonIndex + 1, length).Trim();
            
            // Handle edge case where numberText might be empty or contain only whitespace before removing separators
            if (string.IsNullOrWhiteSpace(numberText))
            {
                Logger.Warning($"Empty number text extracted from list view title: {text}");
                return null;
            }
            
            // Remove thousand separators (dots or commas that might be used)
            numberText = numberText.Replace(".", "").Replace(",", "");
            
            if (int.TryParse(numberText, out var count))
            {
                Logger.Information($"Parsed total result count: {count}");
                return count;
            }
            
            Logger.Warning($"Could not parse number from list view title: {numberText}");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Error parsing total result count from list view title");
            return null;
        }
    }
}
