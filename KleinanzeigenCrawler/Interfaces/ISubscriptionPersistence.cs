using System.Collections.Generic;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

public interface ISubscriptionPersistence
{
    List<Subscription> GetEnabledSubscriptions();
    void AddSubscriber(Subscriber subscriber);
    Subscriber[] GetSubscribers();
    void EnsureFirstRunCompletedAndSave(Subscription subscriptionId);
    void UpdateSubscriber(Subscriber subscriber);
}