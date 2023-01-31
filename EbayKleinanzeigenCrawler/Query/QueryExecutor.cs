using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using KleinanzeigenCrawler.Models;
using HtmlAgilityPack;
using Serilog;
using EbayKleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Query
{
    public class QueryExecutor
    {
        private readonly ConcurrentDictionary<Uri, (DateTime dateAdded, HtmlDocument html)> _uriCache = new();
        private readonly QueryCounter _queryCounter;
        private readonly ILogger _logger;

        // TODO: invalid HTML should be parser-specific and known to IParser implementation only
        private const string InvalidHtml = "<html><head><meta charset=\"utf-8\"><script>";

        public QueryExecutor(ILogger logger, QueryCounter queryCounter)
        {
            // TODO: persist cache?
            _logger = logger;
            _queryCounter = queryCounter;
        }

        public HtmlDocument GetHtml(Uri url, bool useCache = true)
        {
            if (useCache && _uriCache.TryGetValue(url, out (DateTime, HtmlDocument) cachedValue))
            {
                _logger.Information($"Loaded from Cache: {url}");
                return cachedValue.Item2;
            }
            
            DateTime startTime = DateTime.Now;
            
            while (DateTime.Now < startTime + _queryCounter.TimeToWaitBetweenMaxAmountOfRequests)
            {
                if (_queryCounter.AcquirePermissionForQuery())
                {
                    _logger.Information($"Loading URL: {url}");
                    var webGet = new HtmlWeb();
                    HtmlDocument document = webGet.Load(url);

                    if (document.Text.StartsWith(InvalidHtml))
                    {
                        throw new HtmlParseException($"Invalid HTML detected for {url}");
                    }

                    (DateTime dateAdded, HtmlDocument html) newTuple = (DateTime.Now, document);
                    _uriCache.AddOrUpdate(url, addValue: newTuple, updateValueFactory: (_, __) => newTuple);
                    return document;
                }

                _logger.Information($"Awaiting query permission for {url}");
                Thread.Sleep(TimeSpan.FromSeconds(20));
            }

            throw new Exception($"Timeout exceeded waiting for URL {url}");
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
