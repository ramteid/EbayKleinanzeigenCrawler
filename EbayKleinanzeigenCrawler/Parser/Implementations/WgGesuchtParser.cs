using HtmlAgilityPack;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;

namespace EbayKleinanzeigenCrawler.Parser.Implementations;

public class WgGesuchtParser : ParserBase
{
    private const string BaseUrl = "https://www.wg-gesucht.de";
    protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
    protected override uint AllowedRequestsPerTimespan => 40;
    protected override string InvalidHtml => "n/a";

    public WgGesuchtParser(ILogger logger, IQueryExecutor queryExecutor, IErrorStatistics errorStatistics) : base(logger, queryExecutor, errorStatistics) { }

    public override List<Uri> GetAdditionalPages(HtmlDocument document)
    {
        // wg-gesucht.de uses JQuery pagination, which cannot be parsed
        return Enumerable.Empty<Uri>().ToList();
    }

    protected override bool EnsureValidHtml(HtmlDocument resultPage)
    {
        if (!resultPage.Text.Contains("wgg_card offer_list_item"))
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
        var results = resultPage.DocumentNode
            .SelectNodes(".//div[contains(@class, 'wgg_card') and contains(@class, 'offer_list_item')]")
            .ToList();
        // var r = results.Single(r=>r.InnerHtml.Contains("wohnungen-in-Freiburg-im-Breisgau.43.2.1.0.html?asset_id=9924079&amp;pu=12685206&amp;sort_column=1&amp;sort_order=0"));
        // var i = results.IndexOf(r);
        // var a = r.InnerText.Contains("premium sticky ad");
        return results;
    }

    protected override Uri ParseResultLink(HtmlNode result)
    {
        var link = result
            .SelectNodes(".//h3/a")
            .Select(n => n.Attributes.SingleOrDefault(a => a.Name == "href"))
            .Where(l => l is not null)
            .Select(l => new Uri($"{BaseUrl}{l.Value}"))
            .SingleOrDefault();
        return link;
    }

    protected override string ParseResultDate(HtmlNode result)
    {
        var date = result
            .SelectNodes(".//div/span")?
            .FirstOrDefault(s => s.InnerText.Contains("Online:"))?
            .InnerHtml?
            .Trim();
        return date;
    }

    protected override string ParseResultPrice(HtmlNode result)
    {
        var price = result
            .SelectNodes(".//div/b")?
            .FirstOrDefault(s => s.InnerText.Contains("€") || s.InnerText.Contains("&euro"))?
            .InnerHtml
            .Trim()
            .Replace("&euro;", "€");
        return price;
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
            .InnerHtml
            .Trim() ?? "";

        var description = document.DocumentNode
            .SelectNodes(".//*[@id='freitext_0_content']")?
            .FirstOrDefault()?
            .InnerText
            .Trim() ?? "";

        return addressInfo + description;
    }
}