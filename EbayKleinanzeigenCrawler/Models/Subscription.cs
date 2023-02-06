using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Models;

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; }
    public Uri QueryUrl { get; set; }
    public List<string> IncludeKeywords { get; set; }
    public List<string> ExcludeKeywords { get; set; }
    public bool InitialPull { get; set; }
    public bool Enabled { get; set; }
}