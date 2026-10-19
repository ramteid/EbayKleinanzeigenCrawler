namespace KleinanzeigenCrawler.Models;

/// <summary>
/// How often a user who is not a subscriber entered a wrong access password. The user is identified by its channel and ID.
/// </summary>
public class AccessAttempt
{
    public MessengerChannel Channel { get; init; }
    public string Id { get; init; }
    public int FailedAttempts { get; set; }
}
