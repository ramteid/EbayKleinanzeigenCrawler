using KleinanzeigenCrawler.Models;
using HtmlAgilityPack;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using KleinanzeigenCrawler.Parser;

namespace EbayKleinanzeigenCrawler.Parser.Implementations
{
    public class EbayKleinanzeigenParser : ParserBase
    {
        private const string BaseUrl = "https://www.ebay-kleinanzeigen.de";
        public override string InvalidHtml { get => "<html><head><meta charset=\"utf-8\"><script>"; }
        public EbayKleinanzeigenParser(ILogger logger) : base(logger) { }

        protected override void EnsureValidHtml(HtmlDocument resultPage)
        {
            // TODO: An URL with query params seems to forget the query keywords and searches for anything. Do not allow ? in URLs.
            // e.g. /s-anzeigen/96123/anzeige:angebote/lescha-betonmischer/c0-l6895?distance=50&maxPrice=100
            if (resultPage.Text.StartsWith(InvalidHtml) || resultPage.Text.Contains("429 Too many requests from"))
            {
                throw new Exception("Invalid HTML detected. Skipping parsing");
            }
        }

        protected override bool ShouldSkipResult(HtmlNode result)
        {
            bool isProShopLink =
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
            List<Uri> additionalPages = document.DocumentNode.Descendants("a")
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

            if (link is null)
            {
                throw new Exception("Could not parse link");
            }

            return link;
        }

        protected override string ParseResultDate(HtmlNode result)
        {
            var date = result
                    .SelectNodes("div/div/div[@class='aditem-main--top--right']")?
                    .Select(d => d.InnerText)
                    .SingleOrDefault()?
                    .Trim();

            if (string.IsNullOrWhiteSpace(date))
            {
                Logger.Debug(result.InnerHtml);
                Logger.Warning("Could not parse date");
            }

            return date;
        }

        protected override string ParseResultPrice(HtmlNode result)
        {
            var price = result
                    .SelectNodes("div/div/div/p[@class='aditem-main--middle--price-shipping--price']")?
                    .Select(d => d.InnerText)
                    .SingleOrDefault()?
                    .Trim();

            if (string.IsNullOrWhiteSpace(price))
            {
                Logger.Debug(result.InnerHtml);
                Logger.Warning("Could not parse price");
            }

            return price;
        }

        protected override string ParseTitle(HtmlDocument document)
        {
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

        public override bool IsMatch(HtmlDocument document, Subscription subscription)
        {
            if (document.DocumentNode.InnerHtml.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
            {
                Logger.Warning("Tried to parse ad which does not exist anymore");
                return false;
            }

            return base.IsMatch(document, subscription);
        }
    }
}
