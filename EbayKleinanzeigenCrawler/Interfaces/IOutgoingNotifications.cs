using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IOutgoingNotifications
    {
        void NotifySubscribers(Subscription subscription, Result newLink);
    }
}
