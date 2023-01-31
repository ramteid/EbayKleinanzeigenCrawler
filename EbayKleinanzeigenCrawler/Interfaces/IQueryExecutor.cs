using EbayKleinanzeigenCrawler.Models;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IQueryExecutor
    {
        void FreeCache(List<AlreadyProcessedUrl> alreadyProcessedUrls);
        bool GetHtml(Uri url, bool useCache, string invalidHtml, out HtmlDocument htmlDocument);
    }
}