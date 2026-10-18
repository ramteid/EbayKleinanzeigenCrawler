using System;
using System.Collections.Generic;

namespace KleinanzeigenCrawler.Models;

public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; }
    public Uri QueryUrl { get; set; }
    public List<string> IncludeKeywords { get; set; } = new();
    public List<string> ExcludeKeywords { get; set; } = new();
    public bool InitialPull { get; set; }
    public bool Enabled { get; set; }
    public bool FirstRunCompleted { get; set; } = false;
}