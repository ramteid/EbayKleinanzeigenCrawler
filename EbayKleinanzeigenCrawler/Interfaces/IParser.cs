﻿using System;
using System.Collections.Generic;
using EbayKleinanzeigenCrawler.Models;
using HtmlAgilityPack;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface IParser
{
    IQueryExecutor QueryExecutor { get; }

    List<Uri> GetAdditionalPages(HtmlDocument document);
    IEnumerable<Result> ParseLinks(HtmlDocument document);
    bool IsMatch(HtmlDocument document, Subscription subscription);
}