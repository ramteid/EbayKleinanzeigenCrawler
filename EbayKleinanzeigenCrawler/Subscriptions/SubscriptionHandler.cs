using System;
using System.Collections.Generic;
using System.Linq;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Query;
using HtmlAgilityPack;
using Serilog;
using EbayKleinanzeigenCrawler.Interfaces;
using System.Threading;
using Serilog.Events;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Subscriptions
{
    public class SubscriptionHandler
    {
        private readonly IOutgoingNotifications _outgoingNotifications;
        private readonly IParserProvider _parserProvider;
        private readonly QueryExecutor _queryExecutor;
        private readonly QueryCounter _queryCounter;
        private readonly ILogger _logger;
        private readonly ISubscriptionPersistence _subscriptionPersistence;
        private readonly IAlreadyProcessedUrlsPersistence _alreadyProcessedUrlsPersistence;

        public SubscriptionHandler(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger, 
            QueryExecutor queryExecutor, QueryCounter queryCounter,
            ISubscriptionPersistence subscriptionPersistence, IAlreadyProcessedUrlsPersistence alreadyProcessedUrlsPersistence)
        {
            _outgoingNotifications = outgoingNotifications;
            _parserProvider = parserProvider;
            _queryExecutor = queryExecutor;
            _queryCounter = queryCounter;
            _logger = logger;
            _subscriptionPersistence = subscriptionPersistence;
            _alreadyProcessedUrlsPersistence = alreadyProcessedUrlsPersistence;
        }

        public void Run()
        {
            _alreadyProcessedUrlsPersistence.RestoreData();
            while (true)
            {
                List<Subscription> subscriptions = _subscriptionPersistence.GetEnabledSubscriptions();
                _logger.Information($"Found {subscriptions.Count} enabled subscriptions");
                foreach (Subscription subscription in subscriptions)
                {
                    _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");
                    
                    var alreadyProcessedUrls = _alreadyProcessedUrlsPersistence.GetAlreadyProcessedLinksForSubscripition(subscription.Id);
                    ProcessSubscription(subscription, alreadyProcessedUrls);
                    
                    _logger.Information($"Finished processing subscription '{subscription.Title}' {subscription.Id}");
                    _alreadyProcessedUrlsPersistence.SaveData();
                }

                // Avoid flooding the API and the logs
                _logger.Information("Processed all subscriptions. Waiting to resume.");
                Thread.Sleep(TimeSpan.FromSeconds(60));
                while (!_queryCounter.AcquirePermissionForQuery(LogEventLevel.Verbose))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                }
            }
        }

        private void ProcessSubscription(Subscription subscription, List<AlreadyProcessedUrl> alreadyProcessedLinks)
        {
            bool firstRun = alreadyProcessedLinks.Count == 0;
            var parser = _parserProvider.GetParser(subscription);

            List<Result> newResults;
            try
            {
                newResults = GetNewLinks(parser, subscription, alreadyProcessedLinks);
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

            CheckForMatches(parser, subscription, alreadyProcessedLinks, newResults, firstRun);

            _queryExecutor.FreeCache(alreadyProcessedLinks);
        }

        private List<Result> GetNewLinks(IParser parser, Subscription subscription, List<AlreadyProcessedUrl> alreadyProcessedLinks)
        {
            HtmlDocument document = _queryExecutor.GetHtml(subscription.QueryUrl, useCache: false);

            // TODO: Only check additional pages, if it's the first run for the subscription OR the first run after application restart
            var additionalPages = parser.GetAdditionalPages(document); // TODO: Limit to n pages?
            _logger.Information($"{additionalPages.Count} additional pages found");

            var newResults = new List<Result>();

            for (int i = additionalPages.Count - 1; i >= 0; i--)
            {
                var page = additionalPages.ElementAt(i);

                var documentNextPage = _queryExecutor.GetHtml(page, useCache: false);

                var linksFromAdditionalPage = parser.ParseLinks(documentNextPage).ToList();
                UpdateAlreadyProcessedLinkLastFoundDate(alreadyProcessedLinks, linksFromAdditionalPage);
                _logger.Information($"Found {linksFromAdditionalPage.Count} links on page {i + 2}");

                var newLinksFromAdditionalPage = linksFromAdditionalPage
                    .Where(l => !alreadyProcessedLinks.Select(a => a.Uri).Contains(l.Link))
                    .Reverse() // Arrange the oldest entries at the beginning
                    .ToList();
                _logger.Information($"{newLinksFromAdditionalPage.Count} links of them are new");

                newResults.AddRange(newLinksFromAdditionalPage);
            }

            var linksFromFirstPage = parser.ParseLinks(document).ToList();
            UpdateAlreadyProcessedLinkLastFoundDate(alreadyProcessedLinks, linksFromFirstPage);
            _logger.Information($"Found {linksFromFirstPage.Count} links on page 1");

            var newResultsPage1 = linksFromFirstPage
                .Where(l => !alreadyProcessedLinks.Select(a => a.Uri).Contains(l.Link))
                .Reverse()
                .ToList();
            _logger.Information($"{newResultsPage1.Count} links of them are new");

            return newResults.Concat(newResultsPage1).ToList();
        }

        private static void UpdateAlreadyProcessedLinkLastFoundDate(List<AlreadyProcessedUrl> alreadyProcessedLinks, List<Result> linksFromAdditionalPage)
        {
            foreach (var link in linksFromAdditionalPage)
            {
                var processedLink = alreadyProcessedLinks.SingleOrDefault(l => l.Uri.Equals(link));
                if (processedLink is not null)
                {
                    processedLink.LastFound = DateTime.Now;
                }
            }
        }

        private void CheckForMatches(IParser parser, Subscription subscription, List<AlreadyProcessedUrl> alreadyProcessedLinks, List<Result> newResults, bool firstRun)
        {
            foreach (Result result in newResults)
            {
                bool match;
                HtmlDocument document;
                try
                {
                    document = _queryExecutor.GetHtml(result.Link);
                    match = parser.IsMatch(document, subscription);
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

                if (match)
                {
                    if (firstRun && !subscription.InitialPull)
                    {
                        _logger.Debug($"Not notifying about match: {result.Link}");
                        continue;
                    }

                    _logger.Information($"Found match: {result.Link}");
                    _outgoingNotifications.NotifySubscribers(subscription, result);
                }
                else
                {
                    _logger.Debug($"No match: {result.Link}");
                }

                alreadyProcessedLinks.Add(new AlreadyProcessedUrl
                {
                    Uri = result.Link,
                    LastFound = DateTime.Now
                });
            }
        }
    }
}
