using HtmlAgilityPack;
using System;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Interfaces;

public interface IQueryExecutor
{
    void Initialize(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan, string invalidHtml);
    Task<HtmlDocument> GetHtml(Uri url);
}