using System.Collections.Generic;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces
{
    public interface ISubscriptionPersistence
    {
        List<Subscription> GetEnabledSubscriptions();
        void AddSubscriber(Subscriber subscriber);
        Subscriber[] GetSubscribers();
        bool RestoreData();
        void SaveData();
    }
}
