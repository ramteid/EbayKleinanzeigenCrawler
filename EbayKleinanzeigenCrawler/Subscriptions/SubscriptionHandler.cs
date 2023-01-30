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

namespace EbayKleinanzeigenCrawler.Subscriptions
{
    public class SubscriptionHandler
    {
        private readonly IOutgoingNotifications _outgoingNotifications;
        private readonly IParserProvider _parserProvider;
        private readonly QueryExecutor _queryExecutor;
        private readonly ILogger _logger;
        private readonly ISubscriptionPersistence _subscriptionPersistence;
        private readonly IAlreadyProcessedUrlsPersistence _alreadyProcessedUrlsPersistence;

        public SubscriptionHandler(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger, QueryExecutor queryExecutor,
            ISubscriptionPersistence subscriptionPersistence, IAlreadyProcessedUrlsPersistence alreadyProcessedUrlsPersistence)
        {
            _outgoingNotifications = outgoingNotifications;
            _parserProvider = parserProvider;
            _queryExecutor = queryExecutor;
            _logger = logger;
            _subscriptionPersistence = subscriptionPersistence;
            _alreadyProcessedUrlsPersistence = alreadyProcessedUrlsPersistence;
        }

        public void Run()
        {
            while (true)
            {
                List<Subscription> subscriptions = _subscriptionPersistence.GetEnabledSubscriptions();
                _logger.Information($"Found {subscriptions.Count} enabled subscriptions");
                foreach (Subscription subscription in subscriptions)
                {
                    _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");
                    List<Uri> alreadyProcessedUrls = _alreadyProcessedUrlsPersistence.GetOrAdd(subscription.Id);
                    ProcessSubscription(subscription, alreadyProcessedUrls);
                    _logger.Information($"Finished processing subscription '{subscription.Title}' {subscription.Id}");
                }
                _alreadyProcessedUrlsPersistence.SaveData();

                _logger.Information("Processed all subscriptions. Waiting 5 minutes...");
                Thread.Sleep(TimeSpan.FromMinutes(5));
            }
        }

        private void ProcessSubscription(Subscription subscription, List<Uri> alreadyProcessedLinks)
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

        private List<Result> GetNewLinks(IParser parser, Subscription subscription, List<Uri> alreadyProcessedLinks)
        {
            HtmlDocument document = _queryExecutor.GetHtml(subscription.QueryUrl, useCache: false);

            // TODO: Only check additional pages, if it's the first run for the subscription OR the first run after application restart
            List<Uri> additionalPages = parser.GetAdditionalPages(document); // TODO: Limit to n pages?
            _logger.Information($"{additionalPages.Count} additional pages found");

            var newResults = new List<Result>();

            for (int i = additionalPages.Count - 1; i >= 0; i--)
            {
                Uri page = additionalPages.ElementAt(i);

                HtmlDocument documentNextPage = _queryExecutor.GetHtml(page, useCache: false);

                List<Result> linksFromAdditionalPage = parser.ParseLinks(documentNextPage).ToList();
                _logger.Information($"Found {linksFromAdditionalPage.Count} links on page {i + 2}");

                List<Result> newLinksFromAdditionalPage = linksFromAdditionalPage
                    .Where(l => !alreadyProcessedLinks.Contains(l.Link))
                    .ToList();
                _logger.Information($"{newLinksFromAdditionalPage.Count} links of them are new");

                // Arrange the oldest entries at the beginning
                newLinksFromAdditionalPage.Reverse();

                newResults.AddRange(newLinksFromAdditionalPage);
            }

            List<Result> results = parser.ParseLinks(document).ToList();
            _logger.Information($"Found {results.Count} links on page 1");

            List<Result> newResultsPage1 = results
                .Where(l => !alreadyProcessedLinks.Contains(l.Link))
                .Reverse()
                .ToList();
            _logger.Information($"{newResultsPage1.Count} links of them are new");

            var x= newResults.Concat(newResultsPage1).ToList();
            return x;
        }

        private void CheckForMatches(IParser parser, Subscription subscription, List<Uri> alreadyProcessedLinks, List<Result> newResults, bool firstRun)
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

                alreadyProcessedLinks.Add(result.Link);
            }
        }
    }
}
