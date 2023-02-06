using System.Collections.Generic;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface ISubscriptionPersistence
{
    List<Subscription> GetEnabledSubscriptions();
    void AddSubscriber(Subscriber subscriber);
    Subscriber[] GetSubscribers();
    bool RestoreData();
    void SaveData();
}