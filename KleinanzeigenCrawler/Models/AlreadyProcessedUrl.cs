using System;

namespace KleinanzeigenCrawler.Models;

public class AlreadyProcessedUrl
{
    public Uri Uri { get; init; }
    public DateTime LastFound { get; set; }
}