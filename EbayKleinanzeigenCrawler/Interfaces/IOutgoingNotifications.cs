using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces
{
    public interface IOutgoingNotifications
    {
        void NotifySubscribers(Subscription subscription, Result newLink);
    }
}
