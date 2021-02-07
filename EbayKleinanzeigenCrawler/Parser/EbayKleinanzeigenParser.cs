using System;
using System.Collections.Generic;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using HtmlAgilityPack;

namespace EbayKleinanzeigenCrawler.Parser
{
    public class EbayKleinanzeigenParser : IParser
    {
        private Subscription Subscription { get; }

        private const string InvalidHtml = "<html><head><meta charset=\"utf-8\"><script>"; // When the source code is obfuscated with JS

        public EbayKleinanzeigenParser(Subscription subscription)
        {
            Subscription = subscription;
        }

        public List<Uri> GetAdditionalPages(HtmlDocument document)
        {
            List<Uri> additionalPages = document.DocumentNode.Descendants("a")
                .Where(dx => dx.Attributes.Any(ab => ab.Name == "class" && ab.Value == "pagination-page"))
                .SelectMany(d => d.Attributes.Where(a => a.Name == "href"))
                .Select(d => d.Value)
                .Where(l => !string.IsNullOrEmpty(l))
                .Select(l => new Uri($"https://www.ebay-kleinanzeigen.de{l}"))
                .ToList();
            return additionalPages;
        }

        public IEnumerable<Result> ParseLinks(HtmlDocument resultPage)
        {
            if (resultPage.Text.StartsWith(InvalidHtml))
            {
                throw new HtmlParseException("Invalid HTML detected. Skipping parsing");
            }

            var results = resultPage.DocumentNode
                .Descendants("article")
                .Where(div => div.GetAttributeValue("class", "").Contains("aditem"))
                .Select(d => d.Descendants())
                .ToList();

            foreach (IEnumerable<HtmlNode> result in results)
            {
                var link = result
                    .Where(d => d.Attributes.Any(a => a.Value == "aditem-main"))
                    .SelectMany(d => d.Descendants())
                    .Where(d => d.Name == "a")
                    .Select(d => d.Attributes.SingleOrDefault(d => d.Name == "href"))
                    .Where(d => !d.Value.Contains("/pro/")) // Filter out Pro-Shop links
                    .Select(l => new Uri($"https://www.ebay-kleinanzeigen.de{l.Value}"))
                    .SingleOrDefault();

                var date = result
                    .SingleOrDefault(d => d.Attributes.Any(a => a.Value == "aditem-addon"))?
                    .InnerText?.Trim();

                yield return new Result { Link = link, CreationDate = date };
            }
        }

        public bool IsMatch(HtmlDocument document, Subscription subscription)
        {
            if (Subscription.IncludeKeywords is null)
            {
                throw new InvalidOperationException("IncludeKeywords cannot be null");
            }

            if (Subscription.ExcludeKeywords is null)
            {
                throw new InvalidOperationException("ExcludeKeywords cannot be null");
            }

            string title = ParseTitle(document);
            string descriptionText = ParseDescriptionText(document);

            if (title is null || descriptionText is null)
            {
                throw new HtmlParseException("Could not parse title or description text");
            }

            bool allIncludeKeywordsFound = HtmlContainsAllIncludeKeywords(title + descriptionText);
            bool excludeKeywordsFound = HtmlContainsAnyExcludeKeywords(title + descriptionText);
            return allIncludeKeywordsFound && !excludeKeywordsFound;
        }

        private string ParseTitle(HtmlDocument document)
        {
            return document.DocumentNode
                .Descendants("h1")
                .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-title"))?
                .InnerHtml;
        }
        
        private string ParseDescriptionText(HtmlDocument document)
        {
            return document.DocumentNode
                .Descendants("p")
                .SingleOrDefault(div => div.GetAttributeValue("id", "").Contains("viewad-description-text"))?
                .InnerHtml;
        }

        private bool HtmlContainsAllIncludeKeywords(string descriptionText)
        {
            if (Subscription.IncludeKeywords.Count == 0)
            {
                return true;
            }

            // For a keyword "foo | bar", only one of the disjunct keywords must be included
            List<List<string>> disjunctionGroups = Subscription.IncludeKeywords
                .Where(str => str.Contains("|"))
                .Select(str => str.Split("|")
                    .Select(keyword => keyword.Trim())
                    .ToList()
                )
                .ToList();

            foreach (var group in disjunctionGroups)
            {
                bool anyOfGroupInText = group.Any(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));
                if (!anyOfGroupInText)
                {
                    return false;
                }
            }

            bool allNonDisjunctiveKeywordsFound = Subscription.IncludeKeywords
                .Where(k => !k.Contains("|"))
                .All(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));

            return allNonDisjunctiveKeywordsFound;
        }

        private bool HtmlContainsAnyExcludeKeywords(string descriptionText)
        {
            if (Subscription.ExcludeKeywords.Count == 0)
            {
                return false;
            }

            return Subscription.ExcludeKeywords.Any(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));
        }
    }
}
