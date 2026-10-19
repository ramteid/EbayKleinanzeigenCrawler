namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// What a messenger has to do with the message of a user
/// </summary>
public enum AdmissionStatus
{
    /// <summary>The sender is a subscriber. The message is handled as usual.</summary>
    Member,

    /// <summary>The sender was just registered, because new users do not need a password. The message is handled as usual.</summary>
    Registered,

    /// <summary>The message contained the access password and the sender is a subscriber now. The message is not handled any further.</summary>
    Granted,

    /// <summary>The sender may not use the bot (yet). The reply is sent instead of handling the message.</summary>
    Refused,

    /// <summary>The sender is blocked. The message is neither answered nor handled.</summary>
    Ignored
}

/// <summary>
/// The decision of the <see cref="AccessGate"/> about a message
/// </summary>
/// <param name="Reply">The text to send to the sender, for <see cref="AdmissionStatus.Granted"/> and <see cref="AdmissionStatus.Refused"/></param>
public sealed record Admission(AdmissionStatus Status, string Reply = null)
{
    public static Admission Member { get; } = new(AdmissionStatus.Member);

    public static Admission Registered { get; } = new(AdmissionStatus.Registered);

    public static Admission Ignored { get; } = new(AdmissionStatus.Ignored);

    public static Admission Granted(string reply) => new(AdmissionStatus.Granted, reply);

    public static Admission Refused(string reply) => new(AdmissionStatus.Refused, reply);
}
