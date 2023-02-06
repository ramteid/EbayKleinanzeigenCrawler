using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using EbayKleinanzeigenCrawler.Interfaces;
using System.Threading;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Subscriptions;

public class SubscriptionHandler
{
    private readonly IOutgoingNotifications _outgoingNotifications;
    private readonly IParserProvider _parserProvider;
    private readonly ILogger _logger;
    private readonly ISubscriptionPersistence _subscriptionPersistence;
    private readonly IAlreadyProcessedUrlsPersistence _alreadyProcessedUrlsPersistence;

    public SubscriptionHandler(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger,
        ISubscriptionPersistence subscriptionPersistence, IAlreadyProcessedUrlsPersistence alreadyProcessedUrlsPersistence)
    {
        _outgoingNotifications = outgoingNotifications;
        _parserProvider = parserProvider;
        _logger = logger;
        _subscriptionPersistence = subscriptionPersistence;
        _alreadyProcessedUrlsPersistence = alreadyProcessedUrlsPersistence;
    }

    public void ProcessAllSubscriptions()
    {
        _alreadyProcessedUrlsPersistence.RestoreData();
        while (true)
        {
            var subscriptions = _subscriptionPersistence.GetEnabledSubscriptions();
            _logger.Information($"Found {subscriptions.Count} enabled subscriptions");
            foreach (var subscription in subscriptions)
            {
                _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");
                    
                var alreadyProcessedUrls = _alreadyProcessedUrlsPersistence.GetAlreadyProcessedLinksForSubscription(subscription.Id);
                ProcessSubscription(subscription, alreadyProcessedUrls);
                    
                _logger.Information($"Finished processing subscription '{subscription.Title}' {subscription.Id}");
                _alreadyProcessedUrlsPersistence.SaveData();
            }

            // Avoid flooding the API and the logs
            _logger.Information("Processed all subscriptions. Waiting to resume.");
            Thread.Sleep(TimeSpan.FromSeconds(60));
        }
    }

    private void ProcessSubscription(Subscription subscription, List<AlreadyProcessedUrl> alreadyProcessedLinks)
    {
        var firstRun = alreadyProcessedLinks.Count == 0;
        var parser = _parserProvider.GetParser(subscription);

        var newResults = GetNewLinks(parser, subscription.QueryUrl, alreadyProcessedLinks);

        _logger.Information($"Analyzing {newResults.Count} new links, {alreadyProcessedLinks.Count} were already processed");

        CheckForMatches(parser, subscription, alreadyProcessedLinks, newResults, firstRun);
    }

    private List<Result> GetNewLinks(IParser parser, Uri firstPageUrl, List<AlreadyProcessedUrl> alreadyProcessedLinks)
    {
        if (!parser.GetQueryExecutor().GetHtml(firstPageUrl, htmlDocument: out var firstPageHtml))
        {
            return new List<Result>();
        }

        // TODO: Only check additional pages, if it's the first run for the subscription OR the first run after application restart

        var pages = parser.GetAdditionalPages(firstPageHtml);  // TODO: Limit to n pages?
        pages = new List<Uri> { firstPageUrl }
            .Concat(pages)
            .ToList();
        _logger.Debug($"{pages.Count} pages found");

        var newResults = new List<Result>();

        for (var i = pages.Count - 1; i >= 0; i--)
        {
            var page = pages.ElementAt(i);

            if (!parser.GetQueryExecutor().GetHtml(page, htmlDocument: out var htmlDocumentNextPage))
            {
                continue;
            }

            var links = parser.ParseLinks(htmlDocumentNextPage).ToList();
            UpdateAlreadyProcessedLinkLastFoundDate(alreadyProcessedLinks, links);

            var newLinks = links
                .Where(l => !alreadyProcessedLinks.Select(a => a.Uri).Contains(l.Link))
                .Reverse()  // Arrange the oldest entries at the beginning
                .ToList();

            _logger.Information($"Found {links.Count} links on page {i + 1}, there are {newLinks.Count} new links");

            newResults.AddRange(newLinks);
        }

        return newResults;
    }

    private static void UpdateAlreadyProcessedLinkLastFoundDate(List<AlreadyProcessedUrl> alreadyProcessedLinks, List<Result> linksFromAdditionalPage)
    {
        foreach (var result in linksFromAdditionalPage)
        {
            var processedLink = alreadyProcessedLinks.SingleOrDefault(l => l.Uri.Equals(result.Link));
            if (processedLink is not null)
            {
                processedLink.LastFound = DateTime.Now;
            }
        }
    }

    private void CheckForMatches(IParser parser, Subscription subscription, List<AlreadyProcessedUrl> alreadyProcessedLinks, List<Result> newResults, bool firstRun)
    {
        foreach (var result in newResults)
        {
            if (!parser.GetQueryExecutor().GetHtml(result.Link, htmlDocument: out var htmlDocument))
            {
                continue;
            }
                
            bool isMatch;
            try
            {
                isMatch = parser.IsMatch(htmlDocument, subscription);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Skipping this link");
                continue;
            }

            if (isMatch)
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