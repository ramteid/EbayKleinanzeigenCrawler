using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using EbayKleinanzeigenCrawler.Interfaces;
using System.Threading;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.Models;
using System.IO;

namespace EbayKleinanzeigenCrawler.Subscriptions;

public class SubscriptionHandler
{
    private readonly IOutgoingNotifications _outgoingNotifications;
    private readonly IParserProvider _parserProvider;
    private readonly ILogger _logger;
    private readonly ISubscriptionPersistence _subscriptionPersistence;
    private readonly IAlreadyProcessedUrlsPersistence _alreadyProcessedUrlsPersistence;
    private readonly IErrorStatistics _errorStatistics;

    public SubscriptionHandler(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger,
        ISubscriptionPersistence subscriptionPersistence, IAlreadyProcessedUrlsPersistence alreadyProcessedUrlsPersistence,
        IErrorStatistics errorStatistics)
    {
        _outgoingNotifications = outgoingNotifications;
        _parserProvider = parserProvider;
        _logger = logger;
        _subscriptionPersistence = subscriptionPersistence;
        _alreadyProcessedUrlsPersistence = alreadyProcessedUrlsPersistence;
        _errorStatistics = errorStatistics;
    }

    public async Task ProcessAllSubscriptionsAsync()
    {
        _alreadyProcessedUrlsPersistence.RestoreData();
        while (true)
        {
            var subscriptions = _subscriptionPersistence.GetEnabledSubscriptions();
            _logger.Information($"Found {subscriptions.Count} enabled subscriptions");

            var subscriptionsGroupedByParser = subscriptions
                .Select(subscription => (Parser: _parserProvider.GetParser(subscription), Subscription: subscription))
                .GroupBy(subscriptionWithParser => subscriptionWithParser.Parser.GetType().Name)
                .ToList();
            
            // Process multiple platforms asynchronously, while processing subscriptions within one platform sequentially to go easy on the API
            var parserGroupedTasks = subscriptionsGroupedByParser
                .Select(async subscriptionsForOneParser =>
                {
                    // This parserGroup contains all subscriptions for one platform, e. g. EbayKleinanzeigen
                    await Task.Run(async () => {
                        foreach (var subscriptionWithParser in subscriptionsForOneParser)
                        {
                            var subscription = subscriptionWithParser.Subscription;
                            var parser = subscriptionWithParser.Parser;
                            _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");

                            try
                            {
                                await ProcessSubscriptionAsync(subscription, parser);
                            }
                            catch (Exception e)
                            {
                                _logger.Error(e, $"Cancelled processing subscription '{subscription.Title}' {subscription.Id}");
                                _alreadyProcessedUrlsPersistence.SaveData();
                                continue;
                            }

                            _logger.Information($"Finished processing subscription '{subscription.Title}' {subscription.Id}");
                            _alreadyProcessedUrlsPersistence.SaveData();
                            _subscriptionPersistence.EnsureFirstRunCompletedAndSave(subscription);
                            _errorStatistics.NotifyOnThreshold();
                        }
                    });
                });
            await Task.WhenAll(parserGroupedTasks);
            
            // Avoid flooding the API and the logs
            _logger.Information("Processed all subscriptions. Waiting to resume.");
            Thread.Sleep(TimeSpan.FromSeconds(60));
        }
    }

    private async Task ProcessSubscriptionAsync(Subscription subscription, IParser parser)
    {
        var alreadyProcessedLinks = _alreadyProcessedUrlsPersistence.GetAlreadyProcessedLinksForSubscription(subscription.Id);
        var newResults = await GetNewLinks(parser, subscription.QueryUrl, alreadyProcessedLinks);
        foreach (var newResult in newResults)
        {
            if (subscription.FirstRunCompleted || subscription.InitialPull)
            {
                await CheckForMatchAsync(parser, subscription, newResult);
            }
            
            alreadyProcessedLinks.Add(new AlreadyProcessedUrl
            {
                Uri = newResult.Link,
                LastFound = DateTime.Now
            });
        }
    }

    private async Task<List<Result>> GetNewLinks(IParser parser, Uri firstPageUrl, List<AlreadyProcessedUrl> alreadyProcessedLinks)
    {
        var firstPageHtml = await parser.QueryExecutor.GetHtml(firstPageUrl);
        if (firstPageHtml is null)
        {
            return new List<Result>();
        }

        var additionalPages = parser.GetAdditionalPages(firstPageHtml);  // TODO: Limit to n pages?
        _logger.Debug($"{additionalPages.Count + 1} pages found");
        
        // TODO: Only check additional pages, if it's the first run for the subscription OR the first run after application restart
        var pages = new List<Uri> { firstPageUrl }
            .Concat(additionalPages)
            .ToList();

        var newResults = new List<Result>();

        for (var i = pages.Count - 1; i >= 0; i--)
        {
            var page = pages.ElementAt(i);

            var pageHtml = await parser.QueryExecutor.GetHtml(page);
            if (pageHtml is null)
            {
                continue;
            }

            List<Result> links;
            try
            {
                links = parser.ParseLinks(pageHtml).ToList();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error parsing links from page");
                continue;
            }

            UpdateAlreadyProcessedLinkLastFoundDate(alreadyProcessedLinks, links);

            var newLinks = links
                .Where(l => !alreadyProcessedLinks.Select(a => a.Uri).Contains(l.Link))
                .Reverse()  // Arrange the oldest entries at the beginning
                .ToList();

            newResults.AddRange(newLinks);

            if (links.Count == 0)
            {
                _logger.Error($"Found no links on page {i + 1}, which is considered an error");
                // _logger.Error(pageHtml.Text.ReplaceLineEndings(""));
                File.WriteAllText(Path.Join("data", $"links_{parser.GetType().Name[0]}_{Guid.NewGuid()}"), pageHtml.Text);
                _errorStatistics.AmendErrorStatistic(ErrorHandling.ErrorType.ParseLinks);
            }
            else
            {
                _logger.Information($"Found {links.Count} links on page {i + 1}, (new: {newLinks.Count}, old: {links.Count - newLinks.Count})");
            }
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

    private async Task CheckForMatchAsync(IParser parser, Subscription subscription, Result result)
    {
        var htmlDocument = await parser.QueryExecutor.GetHtml(result.Link);
        if (htmlDocument is null)
        {
            return;
        }
            
        bool isMatch;
        try
        {
            isMatch = parser.IsMatch(htmlDocument, subscription);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Skipping this link");
            return;
        }

        if (isMatch)
        {
            _logger.Information($"Found match: {result.Link}");
            await _outgoingNotifications.NotifySubscribers(subscription, result);
        }
        else
        {
            _logger.Debug($"No match: {result.Link}");
        }
    }
}