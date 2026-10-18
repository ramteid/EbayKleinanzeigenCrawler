using System;
using System.Collections.Generic;
using KleinanzeigenCrawler.Models;
using HtmlAgilityPack;

namespace KleinanzeigenCrawler.Interfaces;

public interface IParser
{
    IQueryExecutor QueryExecutor { get; }

    List<Uri> GetAdditionalPages(HtmlDocument document);
    IEnumerable<Result> ParseLinks(HtmlDocument document);
    bool IsMatch(HtmlDocument document, Subscription subscription);
    
    /// <summary>
    /// Gets the total number of results for a search, if available.
    /// Returns null if the parser doesn't support this or if parsing fails.
    /// </summary>
    int? GetTotalResultCount(HtmlDocument document);
}