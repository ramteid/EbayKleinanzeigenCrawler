using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Monitoring;
using HtmlAgilityPack;
using Serilog;

namespace KleinanzeigenCrawler.Query;

public class QueryExecutor : IQueryExecutor, IDisposable
{
    private TimeSpan _timeToWaitBetweenMaxAmountOfRequests;
    private uint _allowedRequestsPerTimespan;

    private readonly QueryCounter _queryCounter;
    private readonly IUserAgentProvider _userAgentProvider;
    private readonly ILogger _logger;
    private readonly MetricsService _metricsService;
    private string _invalidHtml;
    private readonly HttpClient _httpClient;

    public QueryExecutor(ILogger logger, QueryCounter queryCounter, IUserAgentProvider userAgentProvider, MetricsService metricsService = null)
    {
        _logger = logger;
        _queryCounter = queryCounter;
        _userAgentProvider = userAgentProvider;
        _metricsService = metricsService;
        
        // Reuse HttpClient to avoid port exhaustion
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
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
        
        // Derive provider from URL
        var provider = url.Host.Contains("kleinanzeigen") ? "Kleinanzeigen" : 
                       url.Host.Contains("wg-gesucht") ? "WgGesucht" : "Unknown";

        try 
        {
            // Allow retrying once after a 429/Retry-After response was found to avoid temporarily skipping an link
            var firstTry = await TryHttpRequest(url, provider);
            if (firstTry.Document is not null)
            {
                return firstTry.Document;
            }
            
            ThrowIfPageDoesNotExist(url, firstTry.StatusCode);
            
            _logger.Debug("First try failed. Trying one more time.");
            var secondTry = await TryHttpRequest(url, provider);
            if (secondTry.Document is not null)
            {
                return secondTry.Document;
            }

            ThrowIfPageDoesNotExist(url, secondTry.StatusCode);
            _logger.Warning($"Both HTTP attempts failed for {url}");
            return null;
        }
        catch (PageNotFoundException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.Error(e, $"HTTP request failed: {e.Message}");
            _metricsService?.RecordHttpRequest(provider, 0, false);
            _metricsService?.RecordException("QueryExecutor", e.GetType().Name);
            return null;
        }
    }

    private static void ThrowIfPageDoesNotExist(Uri url, HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new PageNotFoundException(url, statusCode);
        }
    }

    private async Task<(HtmlDocument Document, HttpStatusCode StatusCode)> TryHttpRequest(Uri url, string provider)
    {
        await _queryCounter.WaitForAcquiringPermissionForQueryAsync(_timeToWaitBetweenMaxAmountOfRequests, _allowedRequestsPerTimespan);
        
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        var userAgent = _userAgentProvider.GetRandomUserAgent();
        requestMessage.Headers.Add("user-agent", userAgent);
        using var response = await SendMeasuredAsync(requestMessage, provider);

        var htmlDocument = new HtmlDocument();
        var html = await response.Content.ReadAsStringAsync();
        htmlDocument.LoadHtml(html);
        
        var statusCode = (int)response.StatusCode;
        
        if (await ValidateResponse(url, response, html))
        {
            _metricsService?.RecordHttpRequest(provider, statusCode, true);
            return (htmlDocument, response.StatusCode);
        }
        else
        {
            _metricsService?.RecordHttpRequest(provider, statusCode, false);
            
            // Don't notify on technical errors
            if ((int)response.StatusCode < 500 || (int)response.StatusCode >= 600)
            {
                _logger.Warning("Unexpected response {StatusCode} for {Url}", statusCode, url);
                _metricsService?.RecordException("QueryExecutor", "HttpRequestError");
            }
            return (null, response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendMeasuredAsync(HttpRequestMessage requestMessage, string provider)
    {
        // Only the HTTP exchange is measured, not the wait for the rate limit
        using var requestTimer = _metricsService?.MeasureRequestDuration(provider);
        return await _httpClient.SendAsync(requestMessage);
    }

    private async Task<bool> ValidateResponse(Uri url, HttpResponseMessage response, string html)
    {
        var retryAfterHeader = response.Headers.FirstOrDefault(h => h.Key == "Retry-After");
        // ReSharper disable once ConstantConditionalAccessQualifier
        if (!string.IsNullOrWhiteSpace(retryAfterHeader.Value?.FirstOrDefault() ?? ""))
        {
            _logger.Warning("Server responded with Retry-After header to indicate there are too many requests.");
            if (int.TryParse(retryAfterHeader.Value.FirstOrDefault(), out var timeToWait))
            {
                _logger.Information($"Waiting {timeToWait} seconds");
                await Task.Delay(TimeSpan.FromSeconds(timeToWait));
                return false;
            }

            _logger.Error("Found Retry-After header but no value for the time to wait. Waiting 30 seconds ...");
            await Task.Delay(TimeSpan.FromSeconds(30));
            return false;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || html.Contains("429 Too many requests"))
        {
            _logger.Warning("Too many requests detected but no Retry-After header found. Waiting 30 seconds ...");
            await Task.Delay(TimeSpan.FromSeconds(30));
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

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
