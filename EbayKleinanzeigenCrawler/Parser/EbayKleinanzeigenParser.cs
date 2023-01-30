﻿using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using HtmlAgilityPack;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KleinanzeigenCrawler.Parser
{
    public class EbayKleinanzeigenParser : IParser
    {
        private readonly ILogger _logger;

        private const string InvalidHtml = "<html><head><meta charset=\"utf-8\"><script>"; // When the source code is obfuscated with JS

        public EbayKleinanzeigenParser(ILogger logger)
        {
            _logger = logger;
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
            // TODO: An URL with query params seems to forget the query keywords and searches for anything. Do not allow ? in URLs.
            // e.g. /s-anzeigen/96123/anzeige:angebote/lescha-betonmischer/c0-l6895?distance=50&maxPrice=100
            if (resultPage.Text.StartsWith(InvalidHtml))
            {
                throw new HtmlParseException("Invalid HTML detected. Skipping parsing");
            }

            List<HtmlNode> results = resultPage.DocumentNode
                .SelectNodes("//ul[@id='srchrslt-adtable']")?
                .Descendants("article")
                .Where(div => div.GetAttributeValue("class", "").Contains("aditem"))
                .ToList();

            if (results is null)
            {
                // When no results are found
                yield break;
            }

            foreach (HtmlNode result in results)
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
                    _logger.Debug("Skipping Pro-Shop link");
                    continue;
                }

                Uri link = result
                    .SelectNodes("div[@class='aditem-main']//h2//a")?
                    .Select(n => n.Attributes.SingleOrDefault(a => a.Name == "href"))
                    .Where(l => l is not null)
                    .Select(l => new Uri($"https://www.ebay-kleinanzeigen.de{l.Value}"))
                    .SingleOrDefault();

                string price = result
                    .SelectNodes("div/div/div/p[@class='aditem-main--middle--price-shipping--price']")?
                    .Select(d => d.InnerText)
                    .SingleOrDefault()?
                    .Trim();
                
                string date = result
                    .SelectNodes("div/div/div[@class='aditem-main--top--right']")?
                    .Select(d => d.InnerText)
                    .SingleOrDefault()?
                    .Trim();

                // TODO: Write some error statistics and notify admin about too many errors to detect changed HTML syntax

                if (link is null)
                {
                    _logger.Error("Could not parse link");
                    continue;
                }

                if (date is null)
                {
                    _logger.Error("Could not parse date");
                }
                
                if (price is null)
                {
                    _logger.Error("Could not parse price");
                }

                yield return new Result { Link = link, CreationDate = date ?? "?" , Price = price ?? "?" };
            }
        }

        public bool IsMatch(HtmlDocument document, Subscription subscription)
        {
            if (subscription.IncludeKeywords is null)
            {
                throw new InvalidOperationException("IncludeKeywords cannot be null");
            }

            if (subscription.ExcludeKeywords is null)
            {
                throw new InvalidOperationException("ExcludeKeywords cannot be null");
            }

            if (document.DocumentNode.InnerHtml.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
            {
                _logger.Warning("Tried to parse ad which does not exist anymore");
                return false;
            }

            string title = ParseTitle(document);
            string descriptionText = ParseDescriptionText(document);

            if (title is null || descriptionText is null)
            {
                throw new NullReferenceException("Could not parse title or description text");
            }

            bool allIncludeKeywordsFound = HtmlContainsAllIncludeKeywords(subscription, title + descriptionText);
            bool excludeKeywordsFound = HtmlContainsAnyExcludeKeywords(subscription, title + descriptionText);
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

        private bool HtmlContainsAllIncludeKeywords(Subscription subscription, string descriptionText)
        {
            if (subscription.IncludeKeywords.Count == 0)
            {
                return true;
            }

            // For a keyword "foo | bar", only one of the disjunct keywords must be included
            List<List<string>> disjunctionGroups = subscription.IncludeKeywords
                .Where(str => str.Contains("|"))
                .Select(str => str
                    .Split("|")
                    .Select(keyword => keyword.Trim())
                    .ToList()
                )
                .ToList();

            foreach (List<string> group in disjunctionGroups)
            {
                bool anyOfGroupInText = group.Any(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));
                if (!anyOfGroupInText)
                {
                    return false;
                }
            }

            bool allNonDisjunctiveKeywordsFound = subscription.IncludeKeywords
                .Where(k => !k.Contains("|"))
                .All(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));

            return allNonDisjunctiveKeywordsFound;
        }

        private bool HtmlContainsAnyExcludeKeywords(Subscription subscription, string descriptionText)
        {
            if (subscription.ExcludeKeywords.Count == 0)
            {
                return false;
            }

            return subscription.ExcludeKeywords.Any(k => descriptionText.Contains(k, StringComparison.InvariantCultureIgnoreCase));
        }
    }
}
