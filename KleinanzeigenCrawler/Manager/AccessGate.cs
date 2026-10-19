using System.Security.Cryptography;
using System.Text;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Decides whether the sender of a message may use the bot on one messenger channel.
/// Subscribers always may. If an access password is configured, a new user has to send it once to become a subscriber.
/// A user who sends a wrong password too often is blocked for good and gets no answers anymore.
/// </summary>
public sealed class AccessGate
{
    public const int MaxFailedAttempts = 10;

    private const string PasswordPrompt = "This bot is protected. Please send the access password to continue.";
    private const string RegistrationDisabledReply = "New user registrations are currently disabled. Please contact the maintainer.";
    private const string GrantedReply = "Access granted.";
    private const string BlockedReply = "Wrong password. Too many failed attempts: you are blocked and will not get any more answers.";

    private readonly SubscriptionService _subscriptions;
    private readonly IAccessAttemptPersistence _attempts;
    private readonly bool _allowNewUsers;
    private readonly byte[] _passwordHash;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    public AccessGate(
        SubscriptionService subscriptions,
        IAccessAttemptPersistence attempts,
        MessagingConfiguration messaging,
        ILogger logger)
    {
        _subscriptions = subscriptions;
        _attempts = attempts;
        _allowNewUsers = messaging.AllowNewUsers;
        _passwordHash = string.IsNullOrWhiteSpace(messaging.AccessPassword) ? null : Hash(messaging.AccessPassword);
        _logger = logger;
    }

    private MessengerChannel Channel => _subscriptions.Channel;

    /// <param name="userId">The ID of the sender within the channel</param>
    /// <param name="text">The text of the message, or null if the sender did not send text (e.g. it pressed a button)</param>
    public Admission Admit(string userId, string text)
    {
        // The messages of one user are handled one after another, but the checks must not depend on it:
        // parallel guesses must not exceed the limit of failed attempts
        lock (_lock)
        {
            if (_subscriptions.IsSubscriber(userId))
            {
                return Admission.Member;
            }

            if (!_allowNewUsers)
            {
                _logger.Warning("New {Channel} user registration blocked for userId: {UserId}", Channel, userId);
                return Admission.Refused(RegistrationDisabledReply);
            }

            if (_passwordHash is null)
            {
                _subscriptions.RegisterSubscriber(userId);
                return Admission.Registered;
            }

            return CheckPassword(userId, text);
        }
    }

    private Admission CheckPassword(string userId, string text)
    {
        var failedAttempts = _attempts.GetFailedAttempts(Channel, userId);
        if (failedAttempts >= MaxFailedAttempts)
        {
            _logger.Verbose("Ignoring a message of the blocked {Channel} user {UserId}", Channel, userId);
            return Admission.Ignored;
        }

        if (text is not null && IsPassword(text))
        {
            return Grant(userId);
        }

        if (failedAttempts is null)
        {
            // The first message is not counted, because the user was not asked for the password before
            _attempts.SetFailedAttempts(Channel, userId, 0);
            _logger.Information("{Channel} user {UserId} has to enter the access password", Channel, userId);
            return Admission.Refused(PasswordPrompt);
        }

        // A message without text cannot be the password and is not counted either
        return text is null
            ? Admission.Refused(PasswordPrompt)
            : RegisterFailedAttempt(userId, failedAttempts.Value + 1);
    }

    private Admission Grant(string userId)
    {
        _subscriptions.RegisterSubscriber(userId);
        _attempts.Remove(Channel, userId);
        _logger.Information("{Channel} user {UserId} entered the access password", Channel, userId);
        return Admission.Granted(GrantedReply);
    }

    private Admission RegisterFailedAttempt(string userId, int failedAttempts)
    {
        // The attempt is saved before the reply is sent, so that a crash cannot give out free guesses
        _attempts.SetFailedAttempts(Channel, userId, failedAttempts);
        if (failedAttempts >= MaxFailedAttempts)
        {
            _logger.Warning("{Channel} user {UserId} entered a wrong access password {FailedAttempts} times and is blocked", Channel, userId, failedAttempts);
            return Admission.Refused(BlockedReply);
        }

        _logger.Warning("{Channel} user {UserId} entered a wrong access password ({FailedAttempts} of {MaxAttempts} attempts)", Channel, userId, failedAttempts, MaxFailedAttempts);
        var remaining = MaxFailedAttempts - failedAttempts;
        return Admission.Refused($"Wrong password. {remaining} {(remaining == 1 ? "attempt" : "attempts")} left.");
    }

    // Messengers may add surrounding whitespace, the configured password has none
    private bool IsPassword(string text) =>
        CryptographicOperations.FixedTimeEquals(Hash(text.Trim()), _passwordHash);

    // Hashing both sides to the same length lets the comparison take the same time for every input
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
