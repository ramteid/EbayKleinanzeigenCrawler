using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace KleinanzeigenCrawler.Models;

/// <summary>
/// The messenger through which a subscriber talks to the crawler
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum MessengerChannel
{
    // Telegram is the zero value, so persisted subscribers without a channel load as Telegram subscribers
    Telegram = 0,
    Signal = 1
}
