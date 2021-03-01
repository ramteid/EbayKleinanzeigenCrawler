using System;
using System.Collections.Generic;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using EbayKleinanzeigenCrawler.Query;
using HtmlAgilityPack;
using Serilog;

namespace EbayKleinanzeigenCrawler.Jobs
{
    public class CrawlJob
    {
        private readonly IOutgoingNotifications _outgoingNotifications;
        private readonly IParserProvider _parserProvider;
        private readonly QueryExecutor _queryExecutor;
        private readonly ILogger _logger;
        private IParser _parser;

        public CrawlJob(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger, QueryExecutor queryExecutor)
        {
            _outgoingNotifications = outgoingNotifications;
            _parserProvider = parserProvider;
            _queryExecutor = queryExecutor;
            _logger = logger;
        }

        public void Execute(Subscription subscription, List<Uri> alreadyProcessedLinks)
        {
            bool firstRun = alreadyProcessedLinks.Count == 0;
            _parser = _parserProvider.GetInstance(subscription);

            List<Result> newResults;
            try
            {
                newResults = GetNewLinks(subscription, alreadyProcessedLinks);
            }
            catch (HtmlParseException e)
            {
                // When HTML could not be parsed
                _logger.Warning(e.Message);
                return;
            }
            catch (Exception e)
            {
                _logger.Error(e, e.Message);
                return;
            }

            _logger.Information($"Analyzing {newResults.Count} new links, {alreadyProcessedLinks.Count} were already processed");

            CheckForMatches(subscription, alreadyProcessedLinks, newResults, firstRun);

            _queryExecutor.FreeCache(alreadyProcessedLinks);
        }

        private List<Result> GetNewLinks(Subscription subscription, List<Uri> alreadyProcessedLinks)
        {
            HtmlDocument document = _queryExecutor.GetHtml(subscription.QueryUrl, useCache: false);

            List<Uri> additionalPages = _parser.GetAdditionalPages(document); // TODO: Limit to n pages?
            _logger.Information($"{additionalPages.Count} additional pages found");

            var newResults = new List<Result>();
            
            for (int i = additionalPages.Count - 1; i >= 0; i--)
            {
                Uri page = additionalPages.ElementAt(i);

                HtmlDocument documentNextPage = _queryExecutor.GetHtml(page, useCache: false);

                List<Result> linksFromAdditionalPage = _parser.ParseLinks(documentNextPage).ToList();
                _logger.Information($"Found {linksFromAdditionalPage.Count} links on page {i + 2}");

                List<Result> newLinksFromAdditionalPage = linksFromAdditionalPage.Where(l => !alreadyProcessedLinks.Contains(l.Link)).ToList();
                _logger.Information($"{newLinksFromAdditionalPage.Count} links of them are new");
                
                // Arrange the oldest entries at the beginning
                newLinksFromAdditionalPage.Reverse();

                newResults.AddRange(newLinksFromAdditionalPage);
            }

            List<Result> results = _parser.ParseLinks(document).ToList();
            _logger.Information($"Found {results.Count} links on page 1");

            List<Result> newResultsPage1 = results.Where(l => !alreadyProcessedLinks.Contains(l.Link)).ToList();
            _logger.Information($"{newResultsPage1.Count} links of them are new");
            
            return newResults.Concat(newResultsPage1).ToList();
        }

        private void CheckForMatches(Subscription subscription, List<Uri> alreadyProcessedLinks, List<Result> newResults, bool firstRun)
        {
            foreach (Result result in newResults)
            {
                bool match;
                HtmlDocument document;
                try
                {
                    document = _queryExecutor.GetHtml(result.Link);
                    match = _parser.IsMatch(document, subscription);
                }
                catch (HtmlParseException e)
                {
                    // When HTML could not be parsed
                    _logger.Warning(e.Message);
                    continue;
                }
                catch (Exception e)
                {
                    _logger.Error(e, e.Message);
                    continue;
                }

                alreadyProcessedLinks.Add(result.Link);

                if (match)
                {
                    if (firstRun && !subscription.InitialPull)
                    {
                        _logger.Information($"Not notifying about match because it's the first run: {result.Link}");
                        continue;
                    }
                    
                    _logger.Information($"Found match: {result.Link}");
                    _outgoingNotifications.NotifySubscribers(subscription, result); // TODO: If we have two Subscribers with the same subscription, this will alert both on run for Subscriber 1. On run for Subscriber 2, both will be alerted again.
                }
                else
                {
                    _logger.Debug($"No match: {result.Link}");
                }
            }
        }
    }
}
