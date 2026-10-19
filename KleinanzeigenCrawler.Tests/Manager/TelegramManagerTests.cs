using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace KleinanzeigenCrawler.Tests.Manager;

public class TelegramManagerTests : IDisposable
{
    private readonly InMemorySubscriptionPersistence _persistence = new();
    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly ConcurrentQueue<SendMessageRequest> _sentMessages = new();
    private readonly Channel<Update> _incomingUpdates = Channel.CreateUnbounded<Update>();
    private readonly Subscription _subscription = new()
    {
        Title = "Berlin flats",
        QueryUrl = new Uri("https://www.kleinanzeigen.de/s-wohnung/berlin/c203l3331"),
        Enabled = true
    };
    private readonly Result _result = new() { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1"), Price = "900 EUR" };
    private readonly List<TelegramManager> _managers = new();
    private readonly TelegramManager _manager;

    public TelegramManagerTests()
    {
        _botClient
            .Setup(c => c.SendRequest(It.IsAny<IRequest<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Message>, CancellationToken>((request, _) => _sentMessages.Enqueue((SendMessageRequest)request))
            .ReturnsAsync(new Message());
        _botClient
            .Setup(c => c.SendRequest(It.IsAny<IRequest<Update[]>>(), It.IsAny<CancellationToken>()))
            .Returns<IRequest<Update[]>, CancellationToken>(async (_, cancellationToken) =>
                new[] { await _incomingUpdates.Reader.ReadAsync(cancellationToken) });

        _manager = CreateManager(new MessagingConfiguration());
    }

    public void Dispose()
    {
        _managers.ForEach(manager => manager.Dispose());
    }

    private TelegramManager CreateManager(MessagingConfiguration messaging)
    {
        var manager = new TelegramManager(
            _botClient.Object,
            _logger.Object,
            _persistence,
            new Mock<IParserProvider>().Object,
            messaging);
        _managers.Add(manager);
        return manager;
    }

    private async Task SendTextAsync(string chatId, string text)
    {
        await _incomingUpdates.Writer.WriteAsync(new Update
        {
            Message = new Message { Text = text, Chat = new Chat { Id = long.Parse(chatId) } }
        });
    }

    private Subscriber AddSubscriber(string id, MessengerChannel channel = MessengerChannel.Telegram, bool ownsSubscription = true)
    {
        var subscriber = new Subscriber
        {
            Id = id,
            Channel = channel,
            Subscriptions = ownsSubscription ? new List<Subscription> { _subscription } : new List<Subscription>()
        };
        _persistence.AddSubscriber(subscriber);
        return subscriber;
    }

    private void FailSends(params Exception[] failures)
    {
        var remaining = new Queue<Exception>(failures);
        _botClient
            .Setup(c => c.SendRequest(It.IsAny<IRequest<Message>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<Message>, CancellationToken>((request, _) => _sentMessages.Enqueue((SendMessageRequest)request))
            .Returns(() => remaining.Count > 0 ? Task.FromException<Message>(remaining.Dequeue()) : Task.FromResult(new Message()));
    }

    private static ApiRequestException TooManyRequests(int retryAfterSeconds) =>
        new("Too Many Requests", 429, new ResponseParameters { RetryAfter = retryAfterSeconds });

    [Fact]
    public void Channel_ShouldBeTelegram()
    {
        _manager.Channel.Should().Be(MessengerChannel.Telegram);
    }

    [Fact]
    public async Task IncomingStartCommand_ShouldRegisterTheChatAsTelegramSubscriberAndWelcomeIt()
    {
        // Arrange
        _manager.Start();

        // Act
        await SendTextAsync("1001", "/start");

        // Assert
        (await Wait.UntilAsync(() => _sentMessages.Any(m => m.Text.Contains("Welcome to Kleinanzeigen Crawler")))).Should().BeTrue();
        var subscriber = _persistence.GetSubscribers().Should().ContainSingle().Subject;
        subscriber.Id.Should().Be("1001");
        subscriber.Channel.Should().Be(MessengerChannel.Telegram);
        _sentMessages.Should().OnlyContain(m => m.ChatId.Identifier == 1001);
    }

    [Fact]
    public async Task IncomingMessage_ShouldNotReuseASignalSubscriberWithTheSameId()
    {
        // Arrange
        AddSubscriber("1001", MessengerChannel.Signal, ownsSubscription: false);
        _manager.Start();

        // Act
        await SendTextAsync("1001", "/start");

        // Assert
        (await Wait.UntilAsync(() => _sentMessages.Any(m => m.Text.Contains("Welcome to Kleinanzeigen Crawler")))).Should().BeTrue();
        _persistence.GetSubscribers().Select(s => s.Channel)
            .Should().BeEquivalentTo(new[] { MessengerChannel.Signal, MessengerChannel.Telegram });
    }

    [Fact]
    public async Task IncomingMessage_ShouldBeRefusedWhenRegistrationIsClosed()
    {
        // Arrange
        var closedManager = CreateManager(new MessagingConfiguration { AllowNewUsers = false });
        closedManager.Start();

        // Act
        await SendTextAsync("1001", "/start");

        // Assert
        (await Wait.UntilAsync(() => _sentMessages.Any(m => m.Text.Contains("registrations are currently disabled")))).Should().BeTrue();
        _persistence.GetSubscribers().Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_ShouldMessageOnlyTelegramSubscribersOwningTheSubscription()
    {
        // Arrange
        AddSubscriber("1001");
        AddSubscriber("2001", MessengerChannel.Signal);
        AddSubscriber("1002", ownsSubscription: false);

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        var message = _sentMessages.Should().ContainSingle().Subject;
        message.ChatId.Identifier.Should().Be(1001);
        message.Text.Should().Contain("https://www.kleinanzeigen.de/s-anzeige/flat/1").And.Contain("900 EUR");
        report.Delivered.Should().Be(1);
        report.TransientFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_ShouldNotMessageSubscribersWhoDisabledTheSubscription()
    {
        // Arrange
        AddSubscriber("1001");
        _subscription.Enabled = false;

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().BeEmpty();
        report.Should().BeSameAs(DeliveryReport.NoRecipients);
    }

    [Fact]
    public async Task NotifyAsync_ShouldRetryAfterTheTimeTelegramAsksToWait()
    {
        // Arrange
        AddSubscriber("1001");
        FailSends(TooManyRequests(0));

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().HaveCount(2);
        report.Delivered.Should().Be(1);
        report.TransientFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_ShouldReportATransientFailureAfterThreeAttempts()
    {
        // Arrange
        AddSubscriber("1001");
        FailSends(TooManyRequests(0), TooManyRequests(0), TooManyRequests(0), TooManyRequests(0));

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().HaveCount(3);
        report.Delivered.Should().Be(0);
        report.TransientFailures.Should().ContainSingle().Which.Should().BeOfType<ApiRequestException>();
    }

    [Fact]
    public async Task NotifyAsync_ShouldNotWaitLongerThanTheLimitForARateLimitButReportItAsTransient()
    {
        // Arrange
        AddSubscriber("1001");
        FailSends(TooManyRequests(3600));

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().ContainSingle();
        report.Delivered.Should().Be(0);
        report.TransientFailures.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task NotifyAsync_ShouldRetryTransientFailures(Exception failure)
    {
        // Arrange
        AddSubscriber("1001");
        FailSends(failure);

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().HaveCount(2);
        report.Delivered.Should().Be(1);
    }

    public static IEnumerable<object[]> TransientFailures()
    {
        yield return new object[] { new RequestException("Exception during making request") };
        yield return new object[] { new ApiRequestException("Bad Gateway", 502) };
    }

    [Fact]
    public async Task NotifyAsync_ShouldNeitherRetryNorReportRejectedMessagesAsTransient()
    {
        // Arrange
        AddSubscriber("1001");
        FailSends(new ApiRequestException("Bad Request: can't parse entities", 400));

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().ContainSingle();
        report.Delivered.Should().Be(0);
        report.TransientFailures.Should().BeEmpty("trying again cannot fix a rejected message");
    }

    [Fact]
    public async Task NotifyAsync_ShouldDisableTheSubscriptionsOfUsersWhoBlockedTheBot()
    {
        // Arrange
        var blocked = AddSubscriber("1001");
        FailSends(new ApiRequestException("Forbidden: bot was blocked by the user", 403));

        // Act
        var first = await _manager.NotifyAsync(_subscription, _result);
        var second = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().ContainSingle("a blocked user must not be messaged again for every match");
        blocked.Subscriptions.Should().OnlyContain(s => !s.Enabled);
        first.TransientFailures.Should().BeEmpty("trying again cannot fix a blocked bot");
        second.Should().BeSameAs(DeliveryReport.NoRecipients);
    }

    [Fact]
    public async Task NotifyAsync_ShouldKeepNotifyingOtherRecipientsAfterAFailure()
    {
        // Arrange
        AddSubscriber("1001");
        AddSubscriber("1002");
        FailSends(new ApiRequestException("Bad Request: chat not found", 400));

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        _sentMessages.Should().HaveCount(2);
        report.Delivered.Should().Be(1);
    }

    [Fact]
    public async Task NotifyAsync_ShouldCountDeliveriesAndMetricsPerRecipient()
    {
        // Arrange
        AddSubscriber("1001");
        AddSubscriber("1002");

        // Act
        var report = await _manager.NotifyAsync(_subscription, _result);

        // Assert
        report.Delivered.Should().Be(2);
        _sentMessages.Select(m => m.ChatId.Identifier).Should().BeEquivalentTo(new long?[] { 1001, 1002 });
    }
}
