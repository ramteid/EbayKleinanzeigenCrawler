using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KleinanzeigenCrawler.Parser
{
    public abstract class ParserBase : IParser
    {
        protected readonly ILogger Logger;

        public ParserBase(ILogger logger)
        {
            Logger = logger;
        }

        public abstract string InvalidHtml { get; }

        protected abstract void EnsureValidHtml(HtmlDocument resultPage);

        public abstract List<Uri> GetAdditionalPages(HtmlDocument document);

        protected abstract List<HtmlNode> ParseResults(HtmlDocument resultPage);

        protected abstract bool ShouldSkipResult(HtmlNode result);

        protected abstract Uri ParseResultLink(HtmlNode result);

        protected abstract string ParseResultDate(HtmlNode result);

        protected abstract string ParseResultPrice(HtmlNode result);

        protected abstract string ParseTitle(HtmlDocument document);

        protected abstract string ParseDescriptionText(HtmlDocument document);

        public IEnumerable<Result> ParseLinks(HtmlDocument resultPage)
        {
            EnsureValidHtml(resultPage);
            List<HtmlNode> results = ParseResults(resultPage);

            if (results is null)
            {
                // When no results are found
                yield break;
            }

            foreach (HtmlNode result in results)
            {
                if (ShouldSkipResult(result))
                {
                    continue;
                }

                // Validation must happen in the implementations
                Uri link = ParseResultLink(result);
                string date = ParseResultDate(result);
                string price = ParseResultPrice(result);
                yield return new Result { Link = link, CreationDate = date ?? "", Price = price ?? "" };
            }
        }

        public virtual bool IsMatch(HtmlDocument document, Subscription subscription)
        {
            if (document.DocumentNode.InnerHtml.Contains("Die gewünschte Anzeige ist nicht mehr verfügbar"))
            {
                Logger.Warning("Tried to parse ad which does not exist anymore");
                return false;
            }

            if (subscription.IncludeKeywords is null)
            {
                throw new InvalidOperationException("IncludeKeywords cannot be null");
            }

            if (subscription.ExcludeKeywords is null)
            {
                throw new InvalidOperationException("ExcludeKeywords cannot be null");
            }

            string title = ParseTitle(document);
            if (string.IsNullOrWhiteSpace(title))
            {
                Logger.Error(document.DocumentNode.InnerHtml);
                throw new InvalidOperationException("Could not parse title");
            }

            string descriptionText = ParseDescriptionText(document);
            if (string.IsNullOrWhiteSpace(descriptionText))
            {
                Logger.Error(document.DocumentNode.InnerHtml);
                throw new InvalidOperationException("Could not parse description");
            }

            bool allIncludeKeywordsFound = HtmlContainsAllIncludeKeywords(subscription, title + descriptionText);
            bool excludeKeywordsFound = HtmlContainsAnyExcludeKeywords(subscription, title + descriptionText);
            return allIncludeKeywordsFound && !excludeKeywordsFound;
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
