using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// The outcome of looking up a subscriber and registering it if necessary
/// </summary>
public sealed record SubscriberRegistration(Subscriber Subscriber, bool IsNew);
