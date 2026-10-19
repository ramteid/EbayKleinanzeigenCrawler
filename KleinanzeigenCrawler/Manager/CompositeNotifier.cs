using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Notifies the subscribers of all messengers. A failing messenger does not prevent the others from notifying their subscribers.
/// The caller is told to try again only if nobody received the notification: after a partial delivery, a second attempt
/// would notify the recipients who already got the message once more.
/// </summary>
public class CompositeNotifier : IOutgoingNotifications
{
    private readonly IReadOnlyCollection<IMessenger> _messengers;

    public CompositeNotifier(IEnumerable<IMessenger> messengers)
    {
        _messengers = messengers.ToList();
    }

    public async Task NotifySubscribers(Subscription subscription, Result newLink)
    {
        var reports = await Task.WhenAll(_messengers.Select(messenger => NotifySafelyAsync(messenger, subscription, newLink)));

        var failures = reports.SelectMany(report => report.TransientFailures).ToList();
        if (reports.Sum(report => report.Delivered) == 0 && failures.Count > 0)
        {
            throw new AggregateException("The notification could not be delivered to anybody", failures);
        }
    }

    private static async Task<DeliveryReport> NotifySafelyAsync(IMessenger messenger, Subscription subscription, Result newLink)
    {
        try
        {
            return await messenger.NotifyAsync(subscription, newLink);
        }
        catch (Exception ex)
        {
            return new DeliveryReport(0, new[] { ex });
        }
    }
}
