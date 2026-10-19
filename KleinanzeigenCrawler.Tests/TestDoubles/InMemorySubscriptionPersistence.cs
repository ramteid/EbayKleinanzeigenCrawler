using System;
using System.Collections.Generic;
using System.Linq;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Tests.TestDoubles;

/// <summary>
/// Keeps subscribers in memory so that tests can observe the effects of registrations and updates
/// </summary>
public class InMemorySubscriptionPersistence : ISubscriptionPersistence
{
    private readonly List<Subscriber> _subscribers = new();

    public void AddSubscriber(Subscriber subscriber)
    {
        if (!_subscribers.Any(s => IsSameSubscriber(s, subscriber)))
        {
            _subscribers.Add(subscriber);
        }
    }

    public void UpdateSubscriber(Subscriber subscriber)
    {
        _subscribers.RemoveAll(s => IsSameSubscriber(s, subscriber));
        _subscribers.Add(subscriber);
    }

    public Subscriber[] GetSubscribers() => _subscribers.ToArray();

    public List<Subscription> GetEnabledSubscriptions() =>
        _subscribers.SelectMany(s => s.Subscriptions).Where(s => s.Enabled).ToList();

    public void EnsureFirstRunCompletedAndSave(Subscription subscriptionId) => throw new NotSupportedException();

    private static bool IsSameSubscriber(Subscriber a, Subscriber b) => a.Channel == b.Channel && a.Id == b.Id;
}
