using HtmlAgilityPack;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;

namespace EbayKleinanzeigenCrawler.Parser.Implementations;

public class EbayKleinanzeigenParser : ParserBase
{
    private const string BaseUrl = "https://www.ebay-kleinanzeigen.de";
    
    /// <summary>
    /// This interval is used by EbayKleinanzeigen. They only allow 40 queries every 5 minutes. Above that, they obfuscate their HTML.
    /// To make sure not to exceed this limit, it is hard-coded here.
    /// </summary>
    protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
    protected override uint AllowedRequestsPerTimespan => 40;
    protected override string InvalidHtml => "<html><head><meta charset=\"utf-8\"><script>";

    public EbayKleinanzeigenParser(ILogger logger, IQueryExecutor queryExecutor, IErrorStatistics errorStatistics) : base(logger, queryExecutor, errorStatistics) { }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        // TODO: An URL with query params seems to forget the query keywords and searches for anything. Do not allow ? in URLs.
        // e.g. /s-anzeigen/96123/anzeige:angebote/lescha-betonmischer/c0-l6895?distance=50&maxPrice=100
        if (resultPage.Text.StartsWith(InvalidHtml) || resultPage.Text.Contains("429 Too many requests from"))
        {
            Logger.Warning("Invalid HTML detected");
            return false;
        }

        if (resultPage.Text.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
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
            .Select(l => new Uri($"{BaseUrl}{l}"))
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
            .SelectNodes("div[@class='aditem-main']//h2//a")?
            .Select(n => n.Attributes.SingleOrDefault(a => a.Name == "href"))
            .Where(l => l is not null)
            .Select(l => new Uri($"{BaseUrl}{l.Value}"))
            .SingleOrDefault();

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
        if (document.Text.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
        {
            Logger.Warning("Tried to parse ad which does not exist anymore");
            return null;
        }

        return document.DocumentNode
            .Descendants("h1")
            .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-title"))?
            .InnerHtml;
    }

    protected override string ParseDescriptionText(HtmlDocument document)
    {
        return document.DocumentNode
            .Descendants("p")
            .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-description-text"))?
            .InnerHtml;
    }
}