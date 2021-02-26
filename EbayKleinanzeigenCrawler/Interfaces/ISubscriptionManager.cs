using System.Collections.Generic;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface ISubscriptionManager
    {
        List<Subscription> GetDistinctEnabledSubscriptions();
    }
}
