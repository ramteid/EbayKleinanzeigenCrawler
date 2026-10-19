using System;
using System.Collections.Generic;
using System.Linq;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Monitoring;
using HtmlAgilityPack;
using Serilog;
// ReSharper disable VirtualMemberCallInConstructor

namespace KleinanzeigenCrawler.Parser;

public abstract class ParserBase : IParser
{
    protected readonly ILogger Logger;
    private readonly IQueryExecutor _queryExecutor;
    private readonly MetricsService _metricsService;

    public IQueryExecutor QueryExecutor => _queryExecutor;

    protected ParserBase(ILogger logger, IQueryExecutor queryExecutor, MetricsService metricsService = null)
    {
        Logger = logger;
        _queryExecutor = queryExecutor;
        _metricsService = metricsService;
        _queryExecutor.Initialize(
            timeToWaitBetweenMaxAmountOfRequests: TimeToWaitBetweenMaxAmountOfRequests, 
            allowedRequestsPerTimespan: AllowedRequestsPerTimespan,
            invalidHtml: InvalidHtml
        );
    }

    protected abstract TimeSpan TimeToWaitBetweenMaxAmountOfRequests { get; }
    protected abstract uint AllowedRequestsPerTimespan { get; }

    protected abstract string InvalidHtml { get; }

    protected abstract bool EnsureValidHtml(HtmlDocument resultPage);

    public abstract List<Uri> GetAdditionalPages(HtmlDocument document);

    protected abstract List<HtmlNode> ParseResults(HtmlDocument resultPage);

    protected abstract bool ShouldSkipResult(HtmlNode result);

    protected abstract Uri ParseResultLink(HtmlNode result);

    protected abstract string ParseResultDate(HtmlNode result);

    protected abstract string ParseResultPrice(HtmlNode result);

    protected abstract string ParseTitle(HtmlDocument document);

    protected abstract string ParseDescriptionText(HtmlDocument document);

    /// <summary>
    /// Gets the total number of results for a search from the HTML document.
    /// Each parser must implement this to extract the result count from the site-specific HTML structure.
    /// Returns null if the count cannot be determined.
    /// </summary>
    public abstract int? GetTotalResultCount(HtmlDocument document);

    public IEnumerable<Result> ParseLinks(HtmlDocument resultPage)
    {
        var providerName = GetType().Name.Replace("Parser", "");
        
        if (!EnsureValidHtml(resultPage))
        {
            Logger.Warning($"[{providerName}] Skipping parsing link due to invalid HTML");
            _metricsService?.RecordParseError(providerName, "InvalidHtml");
            yield break;
        }
        
        List<HtmlNode> results = null;
        try
        {
            results = ParseResults(resultPage);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[{providerName}] Error calling ParseResults");
            _metricsService?.RecordParseError(providerName, "ParseResultsException");
            yield break;
        }
        
        if (results is null)
        {
            // When no results are found - this is normal, not an error
            yield break;
        }

        foreach (var result in results)
        {
            if (ShouldSkipResult(result))
            {
                continue;
            }

            Uri link = null;
            try
            {
                link = ParseResultLink(result);
                if (link is null)
                {
                    Logger.Error($"[{providerName}] Could not parse link - returned null");
                    _metricsService?.RecordParseError(providerName, "ParseResultLink");
                    continue; // Skip this result, but continue with others
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"[{providerName}] Exception parsing result link");
                _metricsService?.RecordParseError(providerName, "ParseResultLinkException");
                continue;
            }

            // For some ads on some platforms it is normal not to have a date or price displayed.
            // Handle these cases gracefully and treat date and price as optional.
            string date = null;
            string price = null;
            
            try
            {
                date = ParseResultDate(result);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, $"[{providerName}] Error parsing date for {link}, continuing without date");
                _metricsService?.RecordParseError(providerName, "ParseResultDate");
            }
            
            try
            {
                price = ParseResultPrice(result);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, $"[{providerName}] Error parsing price for {link}, continuing without price");
                _metricsService?.RecordParseError(providerName, "ParseResultPrice");
            }

            yield return new Result { Link = link, CreationDate = date ?? "", Price = price ?? "" };
        }
    }

    public virtual bool IsMatch(HtmlDocument document, Subscription subscription)
    {
        var providerName = GetType().Name.Replace("Parser", "");
        
        string title = null;
        try
        {
            title = ParseTitle(document);
            if (string.IsNullOrWhiteSpace(title))
            {
                Logger.Error($"[{providerName}] Could not parse title - returned null or empty");
                _metricsService?.RecordParseError(providerName, "ParseTitle");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[{providerName}] Exception parsing title");
            _metricsService?.RecordParseError(providerName, "ParseTitleException");
            return false;
        }

        string descriptionText = null;
        try
        {
            descriptionText = ParseDescriptionText(document);
            if (string.IsNullOrWhiteSpace(descriptionText))
            {
                Logger.Error($"[{providerName}] Could not parse description - returned null or empty");
                _metricsService?.RecordParseError(providerName, "ParseDescription");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[{providerName}] Exception parsing description");
            _metricsService?.RecordParseError(providerName, "ParseDescriptionException");
            return false;
        }

        // The line break prevents keywords from matching across the end of the title and the start of the description
        var text = title + "\n" + descriptionText;
        try
        {
            var allIncludeKeywordsFound = HtmlContainsAllIncludeKeywords(subscription, text);
            var excludeKeywordsFound = HtmlContainsAnyExcludeKeywords(subscription, text);
            return allIncludeKeywordsFound && !excludeKeywordsFound;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"[{providerName}] Exception checking keywords match");
            _metricsService?.RecordParseError(providerName, "KeywordMatchException");
            return false;
        }
    }

    private bool HtmlContainsAllIncludeKeywords(Subscription subscription, string text)
    {
        if (subscription.IncludeKeywords is null)
        {
            throw new InvalidOperationException("IncludeKeywords cannot be null");
        }

        // For a keyword "foo | bar", only one of the alternatives must be included
        foreach (var alternatives in subscription.IncludeKeywords.Select(SplitAlternatives).Where(alternatives => alternatives.Length > 0))
        {
            if (!alternatives.Any(alternative => text.Contains(alternative, StringComparison.InvariantCultureIgnoreCase)))
            {
                Logger.Verbose($"Not a match because no keyword found from '{string.Join(" | ", alternatives)}'");
                return false;
            }
        }

        return true;
    }

    private static bool HtmlContainsAnyExcludeKeywords(Subscription subscription, string text)
    {
        if (subscription.ExcludeKeywords is null)
        {
            throw new InvalidOperationException("ExcludeKeywords cannot be null");
        }

        return subscription.ExcludeKeywords
            .SelectMany(SplitAlternatives)
            .Any(alternative => text.Contains(alternative, StringComparison.InvariantCultureIgnoreCase));
    }

    // Empty alternatives, e.g. in "foo |", are dropped because they would match any text
    private static string[] SplitAlternatives(string keyword) =>
        keyword.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}