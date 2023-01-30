using HtmlAgilityPack;
using KleinanzeigenCrawler.Parser;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EbayKleinanzeigenCrawler.Parser.Implementations
{
    public class ZypresseParser : ParserBase
    {
        private const string BaseUrl = "https://www.zypresse.com";

        public ZypresseParser(ILogger logger) : base(logger) { }

        public override List<Uri> GetAdditionalPages(HtmlDocument document)
        {
            var additionalPages = document.DocumentNode.Descendants("div")
                .Where(d => d.Attributes.Any(a => a.Name == "class" && a.Value.Contains("pageNaviList")))
                .SelectMany(d => d.Descendants("a"))
                .Where(d => d.Attributes.Any(a => a.Name == "class" && a.Value.Contains("pageNaviNextLink")))
                .Select(d => d.Attributes.SingleOrDefault(a => a.Name == "href"))
                .Select(a => a?.Value)
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => new Uri($"{BaseUrl}{l}"))
                .ToList();
            return additionalPages;
        }

        protected override void EnsureValidHtml(HtmlDocument resultPage)
        {
            var html = resultPage.DocumentNode.InnerHtml;
            if (!html.Contains("listAdlistAd"))
            {
                throw new Exception("Could not find any results in Zypresse page");
            }
        }

        protected override bool ShouldSkipResult(HtmlNode result)
        {
            // No criteria for Zypresse yet about when to skip a result
            return false;
        }

        protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
        {
            return resultPage.DocumentNode
                .SelectNodes("//ul[@id='listAdlistAd']")?
                .Descendants("li")
                .Where(div => div.GetAttributeValue("class", "").Contains("listEntryObject-ad"))
                .ToList();
        }

        protected override Uri ParseResultLink(HtmlNode result)
        {
            var link = result
                .SelectNodes("div/div/a")?
                .Select(n => n.Attributes.SingleOrDefault(a => a.Name == "href"))
                .Where(l => l is not null)
                .Select(l => new Uri($"{BaseUrl}{l.Value}"))
                .SingleOrDefault();

            if (link is null)
            {
                throw new Exception("Could not parse link");
            }

            return link;
        }

        protected override string ParseResultDate(HtmlNode result)
        {
            var date = result
                .SelectNodes("div/div/div/div/div/span[@class='date']")?
                .Select(d => d.InnerText)
                .SingleOrDefault()?
                .Trim();

            if (string.IsNullOrWhiteSpace(date))
            {
                Logger.Error("Could not parse date");
            }

            return date;
        }

        protected override string ParseResultPrice(HtmlNode result)
        {
            // Zypresse doesn't show a price on the result list page
            return "";
        }

        protected override string ParseTitle(HtmlDocument document)
        {
            return document.DocumentNode
                .SelectNodes("//*[@id=\"blockContentInner\"]/div[1]/div/div[1]/div/div[2]/div[2]")?
                .SingleOrDefault()?
                .InnerText ?? "";
        }

        protected override string ParseDescriptionText(HtmlDocument document)
        {
            return document.DocumentNode
                .SelectNodes("//*[@id=\"blockContentInner\"]/div[1]/div/div[1]/div/div[2]/div[3]/div")?
                .SingleOrDefault()?
                .InnerText ?? "";
        }
    }
}
