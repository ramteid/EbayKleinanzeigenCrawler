using System.Collections.Generic;

namespace KleinanzeigenCrawler.Models;

public class Subscriber
{
    public string Id { get; init; }
    public List<Subscription> Subscriptions { get; set; } = new();
}
