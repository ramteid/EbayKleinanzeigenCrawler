using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

/// <summary>
/// Remembers which users who are not subscribers contacted the bot and how often they entered a wrong access password
/// </summary>
public interface IAccessAttemptPersistence
{
    /// <returns>The number of wrong passwords the user entered, or null if the user did not contact the bot before</returns>
    int? GetFailedAttempts(MessengerChannel channel, string userId);

    void SetFailedAttempts(MessengerChannel channel, string userId, int failedAttempts);

    void Remove(MessengerChannel channel, string userId);
}
