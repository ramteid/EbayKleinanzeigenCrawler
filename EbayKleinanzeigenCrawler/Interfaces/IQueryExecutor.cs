using HtmlAgilityPack;
using System;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface IQueryExecutor
{
    void Initialize(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan, string invalidHtml);
    bool GetHtml(Uri url, out HtmlDocument htmlDocument);
    bool WaitUntilQueriesArePossibleAgain();
}