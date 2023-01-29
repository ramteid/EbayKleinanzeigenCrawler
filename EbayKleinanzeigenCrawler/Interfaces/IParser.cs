using System;
using System.Collections.Generic;
using KleinanzeigenCrawler.Models;
using HtmlAgilityPack;

namespace KleinanzeigenCrawler.Interfaces
{
    public interface IParser
    {
        List<Uri> GetAdditionalPages(HtmlDocument document);
        IEnumerable<Result> ParseLinks(HtmlDocument document);
        bool IsMatch(HtmlDocument document, Subscription subscription);
    }
}
