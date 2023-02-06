using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface IOutgoingNotifications
{
    Task NotifySubscribers(Subscription subscription, Result newLink);
}