using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Monitoring;
using KleinanzeigenCrawler.Parser;
using KleinanzeigenCrawler.Persistence;
using Serilog;
using Serilog.Events;

namespace KleinanzeigenCrawler.Subscriptions;

public class SubscriptionHandler
{
    private readonly IOutgoingNotifications _outgoingNotifications;
    private readonly IParserProvider _parserProvider;
    private readonly ILogger _logger;
    private readonly ISubscriptionPersistence _subscriptionPersistence;
    private readonly RedisUrlTracker _urlTracker;
    private readonly AppConfiguration _config;
    private readonly MetricsService _metricsService;

    public SubscriptionHandler(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger,
        ISubscriptionPersistence subscriptionPersistence, RedisUrlTracker urlTracker,
        AppConfiguration config, MetricsService metricsService = null)
    {
        _outgoingNotifications = outgoingNotifications;
        _parserProvider = parserProvider;
        _logger = logger;
        _subscriptionPersistence = subscriptionPersistence;
        _urlTracker = urlTracker;
        _config = config;
        _metricsService = metricsService;
    }

    public async Task ProcessAllSubscriptionsAsync()
    {
        _urlTracker.RestoreData();
        while (true)
        {
            var subscriptions = _subscriptionPersistence.GetEnabledSubscriptions();
            var subscribers = _subscriptionPersistence.GetSubscribers();
            var activeSubscribers = subscribers.Count(s => s.Subscriptions.Any(sub => sub.Enabled));
            
            _logger.Information($"Found {subscriptions.Count} enabled subscriptions");
            
            // Update metrics
            _metricsService?.UpdateActiveSubscriptions(subscriptions.Count);
            _metricsService?.UpdateActiveSubscribers(activeSubscribers);

            var subscriptionsWithParsers = new List<(IParser Parser, Subscription Subscription)>();
            foreach (var subscription in subscriptions)
            {
                try
                {
                    var parser = _parserProvider.GetParser(subscription);
                    if (parser is null)
                    {
                        _logger.Error("No parser was registered for subscription '{Title}' {Id}. Skipping.", subscription.Title, subscription.Id);
                        continue;
                    }
                    
                    subscriptionsWithParsers.Add((parser, subscription));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to resolve parser for subscription '{Title}' {Id}. Skipping subscription.", subscription.Title, subscription.Id);
                    _metricsService?.RecordException("SubscriptionHandler.GetParser", ex.GetType().Name);
                }
            }

            var subscriptionsGroupedByParser = subscriptionsWithParsers
                .GroupBy(subscriptionWithParser => subscriptionWithParser.Parser.GetType())
                .ToList();
            
            // Process multiple platforms asynchronously, while processing subscriptions within one platform sequentially to go easy on the API
            var parserGroupedTasks = subscriptionsGroupedByParser
                .Select(async subscriptionsForOneParser =>
                {
                    // This parserGroup contains all subscriptions for one platform, e. g. Kleinanzeigen
                    foreach (var subscriptionWithParser in subscriptionsForOneParser)
                    {
                        var subscription = subscriptionWithParser.Subscription;
                        var parser = subscriptionWithParser.Parser;
                        _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");

                        try
                        {
                            var completedSuccessfully = await ProcessSubscriptionWithRedisAsync(subscription, parser);
                            var providerName = GetProviderName(parser);
                            _metricsService?.RecordSubscriptionProcessed(providerName, true);

                            if (completedSuccessfully)
                            {
                                _subscriptionPersistence.EnsureFirstRunCompletedAndSave(subscription);
                            }
                            else
                            {
                                _logger.Debug($"First run not marked as completed for subscription '{subscription.Title}' {subscription.Id} due to incomplete processing.");
                            }
                        }
                        catch (Exception e)
                        {
                            var providerName = GetProviderName(parser);
                            _logger.Error(e, $"Cancelled processing subscription '{subscription.Title}' {subscription.Id}");
                            _metricsService?.RecordSubscriptionProcessed(providerName, false);
                            _metricsService?.RecordException("SubscriptionHandler", e.GetType().Name);
                            continue;
                        }

                        _logger.Information($"Finished processing subscription '{subscription.Title}' {subscription.Id}");
                    }
                });
            await Task.WhenAll(parserGroupedTasks);
            
            // Avoid flooding the API and the logs
            _logger.Information("Processed all subscriptions. Waiting to resume.");
            await Task.Delay(TimeSpan.FromSeconds(_config.Crawler.CheckIntervalSeconds));
        }
    }

    private async Task<bool> ProcessSubscriptionWithRedisAsync(Subscription subscription, IParser parser)
    {
        var (newResults, completedSuccessfully) = await GetNewLinksWithRedis(parser, subscription.QueryUrl, _urlTracker, subscription.Id);

        if (completedSuccessfully)
        {
            // Record number of results found
            _metricsService?.ObserveResultsPerSubscription(newResults.Count);
        }

        var processedUrls = new List<AlreadyProcessedUrl>();
        foreach (var newResult in newResults)
        {
            if (subscription.FirstRunCompleted || subscription.InitialPull)
            {
                await CheckForMatchAsync(parser, subscription, newResult);
            }
            
            processedUrls.Add(new AlreadyProcessedUrl
            {
                Uri = newResult.Link,
                LastFound = DateTime.UtcNow
            });
        }
        
        if (processedUrls.Any())
        {
            await _urlTracker.AddOrUpdateAsync(subscription.Id, processedUrls);
            _urlTracker.PersistData();
        }

        return completedSuccessfully;
    }
    
    private async Task<(List<Result> NewResults, bool CompletedSuccessfully)> GetNewLinksWithRedis(IParser parser, Uri firstPageUrl, RedisUrlTracker redisTracker, Guid subscriptionId)
    {
        var firstPageHtml = await parser.QueryExecutor.GetHtml(firstPageUrl);
        if (firstPageHtml is null)
        {
            return (new List<Result>(), false);
        }

        var allPagesFetchedSuccessfully = true;
        var additionalPages = parser.GetAdditionalPages(firstPageHtml);
        _logger.Debug($"{additionalPages.Count + 1} pages found");
        
        var pages = new List<Uri> { firstPageUrl }
            .Concat(additionalPages)
            .ToList();

        var newResults = new List<Result>();

        // Avoid duplicates in case the parser returns the same link multiple times
        var seen = new HashSet<Uri>();

        for (var i = pages.Count - 1; i >= 0; i--)
        {
            var page = pages[i];
            HtmlAgilityPack.HtmlDocument pageHtml;
            if (page == firstPageUrl)
            {
                pageHtml = firstPageHtml;
            }
            else
            {
                pageHtml = await parser.QueryExecutor.GetHtml(page);
            }

            if (pageHtml is null)
            {
                allPagesFetchedSuccessfully = false;
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
                _metricsService?.RecordException("SubscriptionHandler.ParseLinks", e.GetType().Name);
                allPagesFetchedSuccessfully = false;
                continue;
            }

            var newLinksCount = 0;
            var oldLinksCount = 0;

            var pageNewResults = new List<Result>();
            foreach (var link in links)
            {
                if (seen.Contains(link.Link))
                {
                    // already counted from another page
                    continue;
                }

                var isProcessed = redisTracker.IsUrlProcessed(subscriptionId, link.Link);
                if (isProcessed)
                {
                    redisTracker.UpdateLastFound(subscriptionId, link.Link);
                    oldLinksCount++;
                    seen.Add(link.Link);
                }
                else
                {
                    pageNewResults.Add(link);
                    newLinksCount++;
                    seen.Add(link.Link);
                }
            }

            // Reverse the results from this page so they are in oldest-to-newest order
            // (assuming ParseLinks returns newest-to-oldest)
            pageNewResults.Reverse();

            if (pageNewResults.Count > 0)
            {
                newResults.AddRange(pageNewResults);
            }

            _logger.Information($"Found {links.Count} links on page {i + 1}, (new: {newLinksCount}, old: {oldLinksCount})");
        }
        // newResults now contains all new results ordered from oldest to newest across all pages

        return (newResults, allPagesFetchedSuccessfully);
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
            _metricsService?.RecordException("SubscriptionHandler.IsMatch", e.GetType().Name);
            return;
        }

        if (isMatch)
        {
            LogMatchDetails(subscription, result, htmlDocument);
            _logger.Information($"Found match: {result.Link}");
            await _outgoingNotifications.NotifySubscribers(subscription, result);
        }
        else
        {
            _logger.Debug($"No match: {result.Link}");
        }
    }

    private static string GetProviderName(IParser parser)
    {
        var name = parser.GetType().Name;
        if (name.EndsWith("Parser", StringComparison.OrdinalIgnoreCase))
        {
            return name.Substring(0, name.Length - "Parser".Length);
        }

        return name;
    }

    private void LogMatchDetails(Subscription subscription, Result result, HtmlAgilityPack.HtmlDocument htmlDocument)
    {
        if (!_logger.IsEnabled(LogEventLevel.Debug))
        {
            return;
        }

        var htmlContent = htmlDocument.DocumentNode?.OuterHtml;
        if (string.IsNullOrWhiteSpace(htmlContent))
        {
            _logger.Debug("Match {Link} for subscription '{Title}' {Id} has no HTML content available for diagnostics.", result.Link, subscription.Title, subscription.Id);
            return;
        }

        var keywordSnippets = FindKeywordSnippets(subscription, htmlContent);
        if (keywordSnippets.Count == 0)
        {
            var includeKeywords = subscription.IncludeKeywords is null || subscription.IncludeKeywords.Count == 0
                ? "none (match allowed without include keywords)"
                : string.Join(", ", subscription.IncludeKeywords);

            _logger.Debug("Match {Link} for subscription '{Title}' {Id} satisfied filters but no keyword snippet could be extracted. Include keywords: {Keywords}", result.Link, subscription.Title, subscription.Id, includeKeywords);
            return;
        }

        var details = string.Join(" | ", keywordSnippets.Select(snippet => $"{snippet.Keyword}: \"{snippet.Snippet}\""));
        _logger.Debug("Match {Link} for subscription '{Title}' {Id} matched keywords. Details: {Details}", result.Link, subscription.Title, subscription.Id, details);
    }

    private static List<(string Keyword, string Snippet)> FindKeywordSnippets(Subscription subscription, string htmlContent)
    {
        var snippets = new List<(string Keyword, string Snippet)>();
        if (subscription.IncludeKeywords is null || subscription.IncludeKeywords.Count == 0)
        {
            return snippets;
        }

        foreach (var keywordEntry in subscription.IncludeKeywords)
        {
            if (string.IsNullOrWhiteSpace(keywordEntry))
            {
                continue;
            }

            var keywordOptions = keywordEntry.Contains('|')
                ? keywordEntry.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[] { keywordEntry.Trim() };

            foreach (var option in keywordOptions)
            {
                if (string.IsNullOrWhiteSpace(option))
                {
                    continue;
                }

                var snippet = ExtractSnippet(htmlContent, option);
                if (snippet is not null)
                {
                    snippets.Add((option, snippet));
                }
            }

            if (snippets.Count >= 5)
            {
                break;
            }
        }

        return snippets;
    }

    private static string ExtractSnippet(string htmlContent, string keyword, int contextLength = 200)
    {
        var index = htmlContent.IndexOf(keyword, StringComparison.InvariantCultureIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var halfLength = contextLength / 2;
        var start = Math.Max(0, index - halfLength);
        var length = Math.Min(contextLength, htmlContent.Length - start);
        var snippet = htmlContent.Substring(start, length);

        return snippet
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();
    }
}
