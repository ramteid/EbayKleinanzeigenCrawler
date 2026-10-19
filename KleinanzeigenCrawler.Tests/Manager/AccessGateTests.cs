using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

public class AccessGateTests
{
    private const string Password = "correct-horse";
    private const string User = "user-1";

    private readonly InMemorySubscriptionPersistence _subscribers = new();
    private readonly InMemoryAccessAttemptPersistence _attempts = new();
    private readonly ILogger _logger = Mock.Of<ILogger>();

    private AccessGate CreateGate(
        MessengerChannel channel = MessengerChannel.Signal,
        string password = Password,
        bool allowNewUsers = true,
        ILogger logger = null)
    {
        var subscriptions = new SubscriptionService(_subscribers, Mock.Of<IParserProvider>(), _logger, channel);
        var messaging = new MessagingConfiguration { AllowNewUsers = allowNewUsers, AccessPassword = password };
        return new AccessGate(subscriptions, _attempts, messaging, logger ?? _logger);
    }

    private static void EnterWrongPasswords(AccessGate gate, int count, string user = User)
    {
        for (var i = 0; i < count; i++)
        {
            gate.Admit(user, "wrong");
        }
    }

    private bool IsSubscriber(MessengerChannel channel, string id) =>
        _subscribers.GetSubscribers().Any(s => s.Channel == channel && s.Id == id);

    [Fact]
    public void Admit_ShouldLetSubscribersPassWithoutPassword()
    {
        // Arrange
        _subscribers.AddSubscriber(new Subscriber { Id = User, Channel = MessengerChannel.Signal });
        var gate = CreateGate();

        // Act
        var admission = gate.Admit(User, "/list");

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Member);
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().BeNull();
    }

    [Fact]
    public void Admit_ShouldRegisterNewUsersWhenNoPasswordIsConfigured()
    {
        // Arrange
        var gate = CreateGate(password: null);

        // Act
        var first = gate.Admit(User, "hello");
        var second = gate.Admit(User, "hello again");

        // Assert
        first.Status.Should().Be(AdmissionStatus.Registered);
        second.Status.Should().Be(AdmissionStatus.Member);
        _subscribers.GetSubscribers().Should().ContainSingle(s => s.Id == User && s.Channel == MessengerChannel.Signal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Admit_ShouldTreatABlankPasswordAsNoPassword(string blankPassword)
    {
        // Arrange
        var gate = CreateGate(password: blankPassword);

        // Act
        var admission = gate.Admit(User, "   ");

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Registered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Password)]
    public void Admit_ShouldRefuseNewUsersWhenRegistrationIsClosed(string configuredPassword)
    {
        // Arrange
        var gate = CreateGate(password: configuredPassword, allowNewUsers: false);

        // Act
        var withoutPassword = gate.Admit(User, "hello");
        var withPassword = gate.Admit(User, Password);

        // Assert
        withoutPassword.Status.Should().Be(AdmissionStatus.Refused);
        withoutPassword.Reply.Should().Contain("registrations are currently disabled");
        withPassword.Status.Should().Be(AdmissionStatus.Refused);
        withPassword.Reply.Should().Contain("registrations are currently disabled");
        _subscribers.GetSubscribers().Should().BeEmpty();
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().BeNull();
    }

    [Fact]
    public void Admit_ShouldStillLetSubscribersPassWhenRegistrationIsClosed()
    {
        // Arrange
        _subscribers.AddSubscriber(new Subscriber { Id = User, Channel = MessengerChannel.Signal });
        var gate = CreateGate(allowNewUsers: false);

        // Act
        var admission = gate.Admit(User, "/list");

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Member);
    }

    [Fact]
    public void Admit_ShouldAskNewUsersForThePasswordAndNotCountTheFirstMessage()
    {
        // Arrange
        var gate = CreateGate();

        // Act
        var admission = gate.Admit(User, "hello");

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Refused);
        admission.Reply.Should().Contain("access password");
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(0);
    }

    [Fact]
    public void Admit_ShouldAdmitANewUserWhoSendsThePasswordAsFirstMessage()
    {
        // Arrange
        var gate = CreateGate();

        // Act
        var admission = gate.Admit(User, Password);

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Granted);
        admission.Reply.Should().Be("Access granted.");
        IsSubscriber(MessengerChannel.Signal, User).Should().BeTrue();
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().BeNull();
    }

    [Fact]
    public void Admit_ShouldCountWrongPasswordsAndReportTheAttemptsLeft()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");

        // Act
        var first = gate.Admit(User, "wrong");
        var second = gate.Admit(User, "wrong again");

        // Assert
        first.Status.Should().Be(AdmissionStatus.Refused);
        first.Reply.Should().Be("Wrong password. 9 attempts left.");
        second.Reply.Should().Be("Wrong password. 8 attempts left.");
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(2);
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
    }

    [Fact]
    public void Admit_ShouldReportASingleAttemptLeftInTheSingular()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");
        EnterWrongPasswords(gate, AccessGate.MaxFailedAttempts - 2);

        // Act
        var admission = gate.Admit(User, "wrong");

        // Assert
        admission.Reply.Should().Be("Wrong password. 1 attempt left.");
    }

    [Fact]
    public void Admit_ShouldGrantAccessWithThePasswordAfterWrongAttempts()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");
        EnterWrongPasswords(gate, 3);

        // Act
        var granted = gate.Admit(User, Password);
        var next = gate.Admit(User, "/list");

        // Assert
        granted.Status.Should().Be(AdmissionStatus.Granted);
        IsSubscriber(MessengerChannel.Signal, User).Should().BeTrue();
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().BeNull();
        next.Status.Should().Be(AdmissionStatus.Member);
    }

    [Fact]
    public void Admit_ShouldIgnoreWhitespaceAroundThePassword()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");

        // Act
        var admission = gate.Admit(User, $"  {Password}\n");

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Granted);
    }

    [Theory]
    [InlineData("CORRECT-HORSE")]
    [InlineData("correct-hors")]
    [InlineData("correct-horse!")]
    [InlineData("correct horse")]
    public void Admit_ShouldRejectPasswordsThatDifferInAnyWay(string guess)
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");

        // Act
        var admission = gate.Admit(User, guess);

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Refused);
        admission.Reply.Should().StartWith("Wrong password.");
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
    }

    [Fact]
    public void Admit_ShouldBlockAUserAfterTenWrongPasswords()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");
        EnterWrongPasswords(gate, AccessGate.MaxFailedAttempts - 1);

        // Act
        var tenth = gate.Admit(User, "wrong");

        // Assert
        AccessGate.MaxFailedAttempts.Should().Be(10);
        tenth.Status.Should().Be(AdmissionStatus.Refused);
        tenth.Reply.Should().Contain("blocked");
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(10);
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
    }

    [Theory]
    [InlineData(Password)]
    [InlineData("wrong")]
    [InlineData("/start")]
    public void Admit_ShouldIgnoreAllMessagesOfABlockedUserEvenWithTheCorrectPassword(string text)
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");
        EnterWrongPasswords(gate, AccessGate.MaxFailedAttempts);

        // Act
        var admission = gate.Admit(User, text);

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Ignored);
        admission.Reply.Should().BeNull();
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(10);
    }

    [Fact]
    public void Admit_ShouldIgnoreAMessageWithoutTextOfABlockedUser()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");
        EnterWrongPasswords(gate, AccessGate.MaxFailedAttempts);

        // Act
        var admission = gate.Admit(User, null);

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Ignored);
    }

    [Fact]
    public void Admit_ShouldKeepAUserBlockedWhenTheGateIsCreatedAgain()
    {
        // Arrange
        var firstGate = CreateGate();
        firstGate.Admit(User, "hello");
        EnterWrongPasswords(firstGate, AccessGate.MaxFailedAttempts);
        var restartedGate = CreateGate();

        // Act
        var admission = restartedGate.Admit(User, Password);

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Ignored);
    }

    [Fact]
    public void Admit_ShouldNeitherCountNorAcceptMessagesWithoutText()
    {
        // Arrange
        var gate = CreateGate();

        // Act
        var results = Enumerable.Range(0, 3 * AccessGate.MaxFailedAttempts).Select(_ => gate.Admit(User, null)).ToList();

        // Assert
        results.Should().OnlyContain(admission => admission.Status == AdmissionStatus.Refused && admission.Reply.Contains("access password"));
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(0);
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
    }

    [Fact]
    public void Admit_ShouldKeepTheCountWhenAMessageWithoutTextArrives()
    {
        // Arrange
        var gate = CreateGate();
        gate.Admit(User, "hello");
        gate.Admit(User, "wrong");

        // Act
        var admission = gate.Admit(User, null);

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Refused);
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(1);
    }

    [Fact]
    public void Admit_ShouldTrackFailedAttemptsPerChannelAndUser()
    {
        // Arrange
        var signalGate = CreateGate(MessengerChannel.Signal);
        var telegramGate = CreateGate(MessengerChannel.Telegram);
        signalGate.Admit(User, "hello");
        EnterWrongPasswords(signalGate, AccessGate.MaxFailedAttempts);

        // Act
        var sameIdOnTelegram = telegramGate.Admit(User, "hello");
        var otherUserOnSignal = signalGate.Admit("user-2", "hello");

        // Assert
        sameIdOnTelegram.Status.Should().Be(AdmissionStatus.Refused);
        sameIdOnTelegram.Reply.Should().Contain("access password");
        otherUserOnSignal.Status.Should().Be(AdmissionStatus.Refused);
        otherUserOnSignal.Reply.Should().Contain("access password");
        _attempts.GetFailedAttempts(MessengerChannel.Telegram, User).Should().Be(0);
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(AccessGate.MaxFailedAttempts);
    }

    [Fact]
    public void Admit_ShouldRegisterTheSubscriberOnTheChannelOfTheGate()
    {
        // Arrange
        var telegramGate = CreateGate(MessengerChannel.Telegram);

        // Act
        telegramGate.Admit(User, Password);

        // Assert
        IsSubscriber(MessengerChannel.Telegram, User).Should().BeTrue();
        IsSubscriber(MessengerChannel.Signal, User).Should().BeFalse();
    }

    [Fact]
    public async Task Admit_ShouldNotEvaluateMoreWrongPasswordsThanAllowedWhenGuessesArriveInParallel()
    {
        // Arrange
        var gate = CreateGate();
        var admissions = new ConcurrentBag<Admission>();

        // Act
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => admissions.Add(gate.Admit(User, "wrong")))));

        // Assert
        // The first message is the prompt, the next ten are counted guesses and everything after the tenth is ignored
        admissions.Count(a => a.Status == AdmissionStatus.Refused).Should().Be(1 + AccessGate.MaxFailedAttempts);
        admissions.Count(a => a.Status == AdmissionStatus.Refused && a.Reply.Contains("blocked")).Should().Be(1);
        admissions.Count(a => a.Status == AdmissionStatus.Ignored).Should().Be(200 - 1 - AccessGate.MaxFailedAttempts);
        _attempts.GetFailedAttempts(MessengerChannel.Signal, User).Should().Be(AccessGate.MaxFailedAttempts);
    }

    [Fact]
    public void Admit_ShouldNotWriteEnteredTextToTheLog()
    {
        // Arrange
        const string wrongGuess = "hunter2-secret-guess";
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var gate = CreateGate(logger: logger);

        // Act
        gate.Admit(User, "hello-first-message");
        gate.Admit(User, wrongGuess);
        EnterWrongPasswords(gate, AccessGate.MaxFailedAttempts);
        gate.Admit(User, Password);
        gate.Admit("user-2", Password);

        // Assert
        sink.Messages.Should().NotBeEmpty();
        sink.Messages.Should().NotContain(message =>
            message.Contains(wrongGuess) || message.Contains(Password) || message.Contains("hello-first-message"));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public string[] Messages => _messages.ToArray();

        public void Emit(LogEvent logEvent) => _messages.Enqueue(logEvent.RenderMessage());
    }
}
