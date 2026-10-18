using System;
using System.Collections.Generic;
using System.Linq;
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
        // wg-gesucht.de uses JQuery pagination, which cannot be parsed
        return Enumerable.Empty<Uri>().ToList();
    }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        if (!resultPage.ParsedText.Contains("wgg_card offer_list_item"))
        {
            Logger.Warning("Could not find any results on page");
            return false;
        }

        return true;
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
        var nodes = resultPage.DocumentNode
            .SelectNodes(".//div[contains(@class, 'wgg_card') and contains(@class, 'offer_list_item')]");
        
        if (nodes == null)
        {
            return null;
        }
        
        return nodes.ToList();
    }

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var nodes = result.SelectNodes(".//h3/a");
        if (nodes == null)
        {
            return null;
        }
        
        var link = nodes
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
        var dateNode = result
            .SelectNodes(".//div/span")?
            .FirstOrDefault(s => s.InnerText.Contains("Online:"));

        if (dateNode == null)
        {
            return null;
        }

        return System.Net.WebUtility.HtmlDecode(dateNode.InnerText).Trim();
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        // Check for both decoded (€) and encoded (&euro;) forms
        // HtmlAgilityPack may or may not decode entities depending on how HTML is loaded
        var priceNode = result
            .SelectNodes(".//div/b")?
            .FirstOrDefault(s => s.InnerText.Contains('€') || s.InnerText.Contains("&euro;"));

        if (priceNode == null)
        {
            return null;
        }

        return System.Net.WebUtility.HtmlDecode(priceNode.InnerText).Trim();
    }

    protected override string ParseTitle(HtmlDocument document)
    {
        var title = document.DocumentNode
            .SelectNodes(".//h1[@id='sliderTopTitle']")?
            .FirstOrDefault()?
            .InnerText
            .Trim() ?? "";
        return title;
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        var addressInfo = document.DocumentNode
            .SelectNodes(".//div[contains(@class, 'col-sm-4') and contains(@class, 'mb10')]")?
            .FirstOrDefault()?
            .InnerText
            .Trim() ?? "";

        var description = document.DocumentNode
            .SelectNodes(".//*[@id='freitext_0_content']")?
            .FirstOrDefault()?
            .InnerText
            .Trim() ?? "";

        return addressInfo + " " + description;
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
