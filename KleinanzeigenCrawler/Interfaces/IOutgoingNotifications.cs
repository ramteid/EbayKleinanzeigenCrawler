using System.Threading.Tasks;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

public interface IOutgoingNotifications
{
    Task NotifySubscribers(Subscription subscription, Result newLink);
}
