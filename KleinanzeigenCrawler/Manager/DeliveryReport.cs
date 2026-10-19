using System;
using System.Collections.Generic;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// The outcome of notifying the recipients of one messenger about a result
/// </summary>
/// <param name="Delivered">The number of recipients who received the notification</param>
/// <param name="TransientFailures">Failures that may go away, such as an unreachable messenger service or rate limiting.
/// Failures that trying again cannot fix (e.g. a user who blocked the bot) are only logged.</param>
public sealed record DeliveryReport(int Delivered, IReadOnlyList<Exception> TransientFailures)
{
    public static DeliveryReport NoRecipients { get; } = new(0, Array.Empty<Exception>());
}
