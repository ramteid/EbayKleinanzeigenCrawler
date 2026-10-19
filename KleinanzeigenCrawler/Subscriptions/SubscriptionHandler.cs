using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Monitoring;
using KleinanzeigenCrawler.Query;
using Serilog;
using Serilog.Events;

namespace KleinanzeigenCrawler.Subscriptions;

public class SubscriptionHandler
{
    /// <summary>
    /// A result that cannot be handled this many times in a row (its page cannot be loaded or notifying about it fails) is given up,
    /// so that a permanently broken ad neither uses up the request budget nor blocks the newer results
    /// </summary>
    public const int MaxLoadAttempts = 3;

    private readonly IOutgoingNotifications _outgoingNotifications;
    private readonly IParserProvider _parserProvider;
    private readonly ILogger _logger;
    private readonly ISubscriptionPersistence _subscriptionPersistence;
    private readonly IUrlTracker _urlTracker;
    private readonly AppConfiguration _config;
    private readonly MetricsService _metricsService;
    private readonly ConcurrentDictionary<(Guid SubscriptionId, Uri Link), int> _failedAttempts = new();

    public SubscriptionHandler(IOutgoingNotifications outgoingNotifications, IParserProvider parserProvider, ILogger logger,
        ISubscriptionPersistence subscriptionPersistence, IUrlTracker urlTracker,
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
        _urlTracker.VerifyConnection();
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
                        var providerName = GetProviderName(parser);
                        _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");

                        try
                        {
                            var completedSuccessfully = await ProcessSubscriptionAsync(subscription, parser);
                            _metricsService?.RecordSubscriptionProcessed(providerName, completedSuccessfully);

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

    /// <summary>
    /// Notifies about the new matching results of one subscription and remembers every result that was handled
    /// </summary>
    /// <returns>True if all result pages were loaded and parsed</returns>
    public async Task<bool> ProcessSubscriptionAsync(Subscription subscription, IParser parser)
    {
        var (newResults, completedSuccessfully) = await GetNewResultsAsync(parser, subscription.QueryUrl, subscription.Id);

        if (completedSuccessfully)
        {
            // Record number of results found
            _metricsService?.ObserveResultsPerSubscription(newResults.Count);
        }

        // Without an initial pull, the results of the first run only serve as the baseline for detecting new ones
        var checkForMatches = subscription.FirstRunCompleted || subscription.InitialPull;
        foreach (var newResult in newResults)
        {
            if (checkForMatches && !await TryNotifyIfMatchAsync(parser, subscription, newResult))
            {
                continue;
            }

            // Remembering each result right away prevents repeated notifications when a later result fails
            await _urlTracker.MarkProcessedAsync(subscription.Id, newResult.Link);
        }

        ForgetFailedAttemptsOfResultsNoLongerListed(subscription.Id, newResults);

        return completedSuccessfully;
    }

    private void ForgetFailedAttemptsOfResultsNoLongerListed(Guid subscriptionId, List<Result> listedNewResults)
    {
        var listedLinks = listedNewResults.Select(result => result.Link).ToHashSet();
        foreach (var key in _failedAttempts.Keys.Where(key => key.SubscriptionId == subscriptionId && !listedLinks.Contains(key.Link)))
        {
            _failedAttempts.TryRemove(key, out _);
        }
    }
    
    private async Task<(List<Result> NewResults, bool CompletedSuccessfully)> GetNewResultsAsync(IParser parser, Uri firstPageUrl, Guid subscriptionId)
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
            var pageHtml = page == firstPageUrl
                ? firstPageHtml
                : await TryLoadAdditionalPageAsync(parser, page);

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
                if (!seen.Add(link.Link))
                {
                    // already counted from another page
                    continue;
                }

                if (await _urlTracker.RefreshIfProcessedAsync(subscriptionId, link.Link))
                {
                    oldLinksCount++;
                }
                else
                {
                    pageNewResults.Add(link);
                    newLinksCount++;
                }
            }

            // Reverse the results from this page so they are in oldest-to-newest order
            // (assuming ParseLinks returns newest-to-oldest)
            pageNewResults.Reverse();
            newResults.AddRange(pageNewResults);

            _logger.Information($"Found {links.Count} links on page {i + 1}, (new: {newLinksCount}, old: {oldLinksCount})");
        }
        // newResults now contains all new results ordered from oldest to newest across all pages

        return (newResults, allPagesFetchedSuccessfully);
    }

    private async Task<HtmlAgilityPack.HtmlDocument> TryLoadAdditionalPageAsync(IParser parser, Uri page)
    {
        try
        {
            return await parser.QueryExecutor.GetHtml(page);
        }
        catch (PageNotFoundException e)
        {
            // The result list became shorter in the meantime
            _logger.Warning(e, "Result page {Page} does not exist anymore", page);
            return null;
        }
    }

    /// <returns>False if the result could not be handled and is retried during the next run</returns>
    private async Task<bool> TryNotifyIfMatchAsync(IParser parser, Subscription subscription, Result result)
    {
        HtmlAgilityPack.HtmlDocument htmlDocument;
        try
        {
            htmlDocument = await parser.QueryExecutor.GetHtml(result.Link);
        }
        catch (PageNotFoundException)
        {
            // The ad was deleted after it was listed, so there is nothing to retry
            _logger.Information("Skipping {Link} because the ad does not exist anymore", result.Link);
            return true;
        }

        if (htmlDocument is null)
        {
            return !ShouldRetry(subscription, result, "load");
        }

        bool isMatch;
        try
        {
            isMatch = parser.IsMatch(htmlDocument, subscription);
        }
        catch (Exception e)
        {
            // Parsing the same page again would fail again, so the result is not retried
            _logger.Error(e, "Skipping this link");
            _metricsService?.RecordException("SubscriptionHandler.IsMatch", e.GetType().Name);
            return true;
        }

        if (isMatch)
        {
            LogMatchDetails(subscription, result, htmlDocument);
            _logger.Information($"Found match: {result.Link}");
            try
            {
                await _outgoingNotifications.NotifySubscribers(subscription, result);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Notifying about {Link} for subscription '{Title}' {Id} failed", result.Link, subscription.Title, subscription.Id);
                _metricsService?.RecordException("SubscriptionHandler.Notify", e.GetType().Name);
                return !ShouldRetry(subscription, result, "notify");
            }
        }
        else
        {
            _logger.Debug($"No match: {result.Link}");
        }

        _failedAttempts.TryRemove((subscription.Id, result.Link), out _);
        return true;
    }

    private bool ShouldRetry(Subscription subscription, Result result, string action)
    {
        var key = (subscription.Id, result.Link);
        var attempts = _failedAttempts.AddOrUpdate(key, 1, (_, previousAttempts) => previousAttempts + 1);
        if (attempts < MaxLoadAttempts)
        {
            _logger.Warning("Could not {Action} {Link} for subscription '{Title}' {Id} (attempt {Attempt} of {MaxAttempts}). Retrying during the next run.",
                action, result.Link, subscription.Title, subscription.Id, attempts, MaxLoadAttempts);
            return true;
        }

        _failedAttempts.TryRemove(key, out _);
        _logger.Error("Giving up on {Link} for subscription '{Title}' {Id} after {MaxAttempts} failed attempts to {Action} it",
            result.Link, subscription.Title, subscription.Id, MaxLoadAttempts, action);
        return false;
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
