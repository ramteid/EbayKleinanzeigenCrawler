using System;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

/// <summary>
/// A messenger that handles the conversations with its subscribers and delivers notifications to them
/// </summary>
public interface IMessenger : IDisposable
{
    MessengerChannel Channel { get; }

    /// <summary>
    /// Starts receiving messages in the background
    /// </summary>
    void Start();

    /// <summary>
    /// Notifies the recipients of this messenger about a new result. Delivery problems are reported, not thrown.
    /// </summary>
    Task<DeliveryReport> NotifyAsync(Subscription subscription, Result newResult);
}
