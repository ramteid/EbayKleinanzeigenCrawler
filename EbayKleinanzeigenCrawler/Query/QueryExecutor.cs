using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HtmlAgilityPack;
using Serilog;
using EbayKleinanzeigenCrawler.Models;
using System.Net.Http;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;

namespace KleinanzeigenCrawler.Query
{
    public class QueryExecutor : IQueryExecutor
    {
        private readonly ConcurrentDictionary<Uri, (DateTime dateAdded, HtmlDocument html)> _uriCache = new();
        private readonly QueryCounter _queryCounter;
        private readonly ILogger _logger;

        public QueryExecutor(ILogger logger, QueryCounter queryCounter)
        {
            // TODO: persist cache?
            _logger = logger;
            _queryCounter = queryCounter;
        }

        public bool GetHtml(Uri url, bool useCache, string invalidHtml, out HtmlDocument htmlDocument)
        {
            if (useCache && _uriCache.TryGetValue(url, out (DateTime, HtmlDocument) cachedValue))
            {
                _logger.Information($"Loaded from Cache: {url}");
                htmlDocument = cachedValue.Item2;
                return true;
            }

            _logger.Information($"Loading URL: {url}");

            try 
            {
                // Allow retrying once after a 429/Retry-After reponse was found to avoid temporarily skipping an link
                var firstTry = TryHttpRequest(url, invalidHtml, out htmlDocument);
                if (!firstTry)
                {
                    _logger.Debug("First try failed. Trying one more time.");
                    var secondTry = TryHttpRequest(url, invalidHtml, out htmlDocument);
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

            (DateTime dateAdded, HtmlDocument html) newTuple = (DateTime.Now, htmlDocument);
            _uriCache.AddOrUpdate(url, addValue: newTuple, updateValueFactory: (_, __) => newTuple);
            return true;
        }

        private bool TryHttpRequest(Uri url, string invalidHtml, out HtmlDocument htmlDocument)
        {
            _queryCounter.WaitForPermissionForQuery();
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            var httpClient = new HttpClient(handler);
            var response = httpClient.GetAsync(url).Result;
            htmlDocument = new HtmlDocument();
            var html = response.Content.ReadAsStringAsync().Result;
            htmlDocument.LoadHtml(html);
            return ValidateResponse(url, response, invalidHtml, html);
        }

        private bool ValidateResponse(Uri url, HttpResponseMessage response, string invalidHtml, string html)
        {
            var retryAfterHeader = response.Headers.FirstOrDefault(h => h.Key == "Retry-After");
            if (retryAfterHeader.Value is not null)
            {
                _logger.Warning($"Server responded with Retry-After header to indicate there are too many requests.");
                if (int.TryParse(retryAfterHeader.Value.FirstOrDefault(), out var timeToWait))
                {
                    _logger.Information($"Waiting {timeToWait} seconds");
                    Thread.Sleep(TimeSpan.FromSeconds(timeToWait));
                    return false;
                }
                else
                {
                    _logger.Error("Found Retry-After header but no value for the time to wait. Waiting 30 seconds ...");
                    Thread.Sleep(TimeSpan.FromSeconds(30));
                    return false;
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || html.Contains("429 Too many requests"))
            {
                _logger.Warning($"Too many requests detected but no Retry-After header found. Waiting 30 seconds ...");
                Thread.Sleep(TimeSpan.FromSeconds(30));
                return false;
            }
            
            if ((int)response.StatusCode >= 400)
            {
                _logger.Error($"Server responded with error code '{response.StatusCode}'");
                return false;
            }

            if (html.StartsWith(invalidHtml))
            {
                _logger.Error($"Invalid HTML detected for {url}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Removes entries from the cache, which were already processed more than one day ago to reduce memory usage.
        /// Hint: Inconsistency when multiple subscriptions contain the same URL.
        /// </summary>
        /// <param name="alreadyProcessedUrls">List of already processed URLs</param>
        public void FreeCache(List<AlreadyProcessedUrl> alreadyProcessedUrls)
        {
            foreach (var alreadyProcessedUrl in alreadyProcessedUrls)
            {
                if (_uriCache.TryGetValue(alreadyProcessedUrl.Uri, out (DateTime dateAdded, HtmlDocument html) cachedEntry)) // TODO: check if it makes sense to remove an already processed URL from the cache immediately.
                {
                    if (cachedEntry.dateAdded < DateTime.Now - TimeSpan.FromDays(1))
                    {
                        _uriCache.Remove(alreadyProcessedUrl.Uri, out (DateTime dateAdded, HtmlDocument html) _);
                    }
                }
            }
        }
    }
}
