namespace KleinanzeigenCrawler.Models;

/// <summary>
/// A text message that a user sent to the bot in a one-to-one Signal chat
/// </summary>
/// <param name="SenderId">The stable account ID of the sender, which is also the recipient ID for replies</param>
/// <param name="Text">The message text</param>
public sealed record SignalMessage(string SenderId, string Text);
