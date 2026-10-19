using System;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Interfaces;

/// <summary>
/// Remembers which result URLs were already handled for a subscription.
/// All members throw if the state cannot be read or written, so that an unknown state is never mistaken for a new URL.
/// </summary>
public interface IUrlTracker
{
    void VerifyConnection();

    /// <summary>
    /// Extends the retention of an already processed URL
    /// </summary>
    /// <returns>False if the URL has not been processed yet</returns>
    Task<bool> RefreshIfProcessedAsync(Guid subscriptionId, Uri url);

    Task MarkProcessedAsync(Guid subscriptionId, Uri url);
}
