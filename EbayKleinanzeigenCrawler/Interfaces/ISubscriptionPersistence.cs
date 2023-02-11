using System.Collections.Generic;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface ISubscriptionPersistence
{
    bool RestoreData();
    void SaveData();
    List<Subscription> GetEnabledSubscriptions();
    void AddSubscriber(Subscriber subscriber);
    Subscriber[] GetSubscribers();
    void EnsureFirstRunCompletedAndSave(Subscription subscriptionId);
}