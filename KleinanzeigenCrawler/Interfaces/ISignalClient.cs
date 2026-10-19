using System;
using System.Threading;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

/// <summary>
/// Gateway to the Signal network
/// </summary>
public interface ISignalClient
{
    /// <summary>
    /// Sends a text message to a recipient
    /// </summary>
    Task SendAsync(string recipientId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Passes incoming messages to the handler until cancelled and reconnects when the connection is lost.
    /// The handler must return quickly.
    /// </summary>
    Task ReceiveAsync(Action<SignalMessage> onMessage, CancellationToken cancellationToken);
}
