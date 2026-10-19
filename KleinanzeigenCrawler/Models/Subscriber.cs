using System.Collections.Generic;

namespace KleinanzeigenCrawler.Models;

public class Subscriber
{
    /// <summary>
    /// Identifies the subscriber within its channel (e.g. the Telegram chat ID or the Signal account ID)
    /// </summary>
    public string Id { get; init; }
    public MessengerChannel Channel { get; init; }
    public List<Subscription> Subscriptions { get; set; } = new();
}
