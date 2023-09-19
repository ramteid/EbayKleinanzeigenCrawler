using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Models;

public class Subscriber
{
    public string Id { get; init; }
    public bool IsAdmin { get; init; } = false;
    public InputState State { get; set; } = InputState.Idle;
    public Subscription IncompleteSubscription { get; set; }
    public List<Subscription> Subscriptions { get; set; } = new();
}