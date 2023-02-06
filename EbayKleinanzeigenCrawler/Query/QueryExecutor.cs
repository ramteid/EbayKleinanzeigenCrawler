using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using EbayKleinanzeigenCrawler.Interfaces;
using HtmlAgilityPack;
using Serilog;

namespace EbayKleinanzeigenCrawler.Query;

public class QueryExecutor : IQueryExecutor
{
    /// <summary>
    /// This interval is used by EbayKleinanzeigen. They only allow 40 queries every 5 minutes. Above that, they obfuscate their HTML.
    /// To make sure not to exceed this limit, it is hard-coded here.
    /// </summary>
    private TimeSpan _timeToWaitBetweenMaxAmountOfRequests;
    private uint _allowedRequestsPerTimespan;

    private readonly QueryCounter _queryCounter;
    private readonly ILogger _logger;
    private string _invalidHtml;

    public QueryExecutor(ILogger logger, QueryCounter queryCounter)
    {
        // TODO: persist cache?
        _logger = logger;
        _queryCounter = queryCounter;
    }

    public void Initialize(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan, string invalidHtml)
    {
        _timeToWaitBetweenMaxAmountOfRequests = timeToWaitBetweenMaxAmountOfRequests;
        _allowedRequestsPerTimespan = allowedRequestsPerTimespan;
        _invalidHtml = invalidHtml;
    }

    public bool GetHtml(Uri url, out HtmlDocument htmlDocument)
    {
        _logger.Information($"Loading URL: {url}");

        try 
        {
            // Allow retrying once after a 429/Retry-After response was found to avoid temporarily skipping an link
            var firstTry = TryHttpRequest(url, out htmlDocument);
            if (!firstTry)
            {
                _logger.Debug("First try failed. Trying one more time.");
                var secondTry = TryHttpRequest(url, out htmlDocument);
                if (!secondTry)
                {
                    return false;
                }
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, $"HTTP request failed: {e.Message}");
            htmlDocument = null;
            return false;
        }

        return true;
    }

    private bool TryHttpRequest(Uri url, out HtmlDocument htmlDocument)
    {
        _queryCounter.WaitForAcquiringPermissionForQuery(_timeToWaitBetweenMaxAmountOfRequests, _allowedRequestsPerTimespan);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var httpClient = new HttpClient(handler);
        var response = httpClient.GetAsync(url).Result;
        htmlDocument = new HtmlDocument();
        var html = response.Content.ReadAsStringAsync().Result;
        htmlDocument.LoadHtml(html);
        return ValidateResponse(url, response, html);
    }

    private bool ValidateResponse(Uri url, HttpResponseMessage response, string html)
    {
        var retryAfterHeader = response.Headers.FirstOrDefault(h => h.Key == "Retry-After");
        // ReSharper disable once ConstantConditionalAccessQualifier
        if (!string.IsNullOrWhiteSpace(retryAfterHeader.Value?.FirstOrDefault() ?? ""))
        {
            _logger.Warning("Server responded with Retry-After header to indicate there are too many requests.");
            if (int.TryParse(retryAfterHeader.Value.FirstOrDefault(), out var timeToWait))
            {
                _logger.Information($"Waiting {timeToWait} seconds");
                Thread.Sleep(TimeSpan.FromSeconds(timeToWait));
                return false;
            }

            _logger.Error("Found Retry-After header but no value for the time to wait. Waiting 30 seconds ...");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return false;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || html.Contains("429 Too many requests"))
        {
            _logger.Warning("Too many requests detected but no Retry-After header found. Waiting 30 seconds ...");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return false;
        }

        if ((int)response.StatusCode >= 400)
        {
            _logger.Error($"Server responded with error code '{response.StatusCode}'");
            return false;
        }

        if (html.StartsWith(_invalidHtml))
        {
            _logger.Error($"Invalid HTML detected for {url}");
            return false;
        }

        return true;
    }

    public bool WaitUntilQueriesArePossibleAgain()
    {
        return _queryCounter.WaitForAcquiringPermissionForQuery(_timeToWaitBetweenMaxAmountOfRequests, _allowedRequestsPerTimespan, acquire: false);
    }
}