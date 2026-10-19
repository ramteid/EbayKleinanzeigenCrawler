using System.Threading.Tasks;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

public interface IOutgoingNotifications
{
    /// <summary>
    /// Notifies the subscribers of the subscription about a new result.
    /// Throws if a notification could not be delivered for a reason that may go away, so that the caller can try again later.
    /// </summary>
    Task NotifySubscribers(Subscription subscription, Result newLink);
}
