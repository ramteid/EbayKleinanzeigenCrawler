using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.ErrorHandling;
using EbayKleinanzeigenCrawler.Interfaces;
using HtmlAgilityPack;
using Serilog;

namespace EbayKleinanzeigenCrawler.Query;

public class QueryExecutor : IQueryExecutor
{
    private TimeSpan _timeToWaitBetweenMaxAmountOfRequests;
    private uint _allowedRequestsPerTimespan;

    private readonly QueryCounter _queryCounter;
    private readonly IErrorStatistics _errorStatistics;
    private readonly IUserAgentProvider _userAgentProvider;
    private readonly ILogger _logger;
    private string _invalidHtml;

    public QueryExecutor(ILogger logger, QueryCounter queryCounter, IErrorStatistics errorStatistics, IUserAgentProvider userAgentProvider)
    {
        _logger = logger;
        _queryCounter = queryCounter;
        _errorStatistics = errorStatistics;
        _userAgentProvider = userAgentProvider;
    }

    public void Initialize(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan, string invalidHtml)
    {
        _timeToWaitBetweenMaxAmountOfRequests = timeToWaitBetweenMaxAmountOfRequests;
        _allowedRequestsPerTimespan = allowedRequestsPerTimespan;
        _invalidHtml = invalidHtml;
    }

    public async Task<HtmlDocument> GetHtml(Uri url)
    {
        _logger.Information($"Loading URL: {url}");

        try 
        {
            // Allow retrying once after a 429/Retry-After response was found to avoid temporarily skipping an link
            var firstTry = await TryHttpRequest(url);
            if (firstTry is not null)
            {
                return firstTry;
            }
            else
            {
                _errorStatistics.AmendErrorStatistic(ErrorType.HttpRequest);
                _logger.Debug("First try failed. Trying one more time.");
                var secondTry = await TryHttpRequest(url);
                if (secondTry is not null)
                {
                    return secondTry;
                }
                else
                {
                    return null;
                }
            }
        }
        catch (Exception e)
        {
            _errorStatistics.AmendErrorStatistic(ErrorType.HttpRequest);
            _logger.Error(e, $"HTTP request failed: {e.Message}");
            return null;
        }
    }

    private async Task<HtmlDocument> TryHttpRequest(Uri url)
    {
        _queryCounter.WaitForAcquiringPermissionForQuery(_timeToWaitBetweenMaxAmountOfRequests, _allowedRequestsPerTimespan, acquire: true);
        var httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });

        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        var userAgent = _userAgentProvider.GetRandomUserAgent();
        requestMessage.Headers.Add("user-agent", userAgent);
        var response = await httpClient.SendAsync(requestMessage);

        var htmlDocument = new HtmlDocument();
        var html = await response.Content.ReadAsStringAsync();
        htmlDocument.LoadHtml(html);
        if (ValidateResponse(url, response, html))
        {
            return htmlDocument;
        }
        else
        {
            // _logger.Warning(html.ReplaceLineEndings(""));
            File.WriteAllText(Path.Join("data", $"validateResponse_{Guid.NewGuid()}"), html);
            return null;
        }
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
            _logger.Error($"Server responded with error code '{(int)response.StatusCode} {response.StatusCode}'");
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