using Xunit;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

public class SignalManagerTests : IDisposable
{
    private const string Alice = "11111111-aaaa-bbbb-cccc-000000000001";
    private const string Bob = "22222222-aaaa-bbbb-cccc-000000000002";
    private const string Password = "correct-horse";
    private const string SearchUrl = "https://www.kleinanzeigen.de/s-wohnung/berlin/c203l3331";

    private readonly InMemorySubscriptionPersistence _persistence = new();
    private readonly InMemoryAccessAttemptPersistence _accessAttempts = new();
    private readonly FakeSignalClient _client = new();
    private readonly Mock<IParserProvider> _parserProvider = new();
    private readonly Mock<IQueryExecutor> _queryExecutor = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly SignalManager _manager;

    public SignalManagerTests()
    {
        _queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).ReturnsAsync((HtmlDocument)null);
        var parser = new Mock<IParser>();
        parser.SetupGet(p => p.QueryExecutor).Returns(_queryExecutor.Object);
        _parserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(parser.Object);

        _manager = new SignalManager(_client, _logger.Object, _persistence, _accessAttempts, _parserProvider.Object, new MessagingConfiguration());
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    private async Task StartAsync()
    {
        _manager.Start();
        await _client.ReceiveStarted;
    }

    private SignalManager CreateProtectedManager() => new(
        _client, _logger.Object, _persistence, _accessAttempts, _parserProvider.Object, new MessagingConfiguration { AccessPassword = Password });

    private async Task<bool> RepliesAsync(int count) => await Wait.UntilAsync(() => _client.Sent.Count >= count);

    private IEnumerable<string> RepliesTo(string recipient) => _client.Sent.Where(s => s.Recipient == recipient).Select(s => s.Text);

    private Subscriber AddSignalSubscriber(string id, params Subscription[] subscriptions)
    {
        var subscriber = new Subscriber { Id = id, Channel = MessengerChannel.Signal, Subscriptions = subscriptions.ToList() };
        _persistence.AddSubscriber(subscriber);
        return subscriber;
    }

    private static Subscription CreateSubscription(string title, bool enabled = true) => new()
    {
        Title = title,
        QueryUrl = new Uri(SearchUrl),
        Enabled = enabled
    };

    [Fact]
    public void Channel_ShouldBeSignal()
    {
        _manager.Channel.Should().Be(MessengerChannel.Signal);
    }

    [Fact]
    public async Task IncomingMessage_ShouldBeAnsweredToItsSender()
    {
        // Arrange
        await StartAsync();

        // Act
        _client.Emit(Alice, "/help");

        // Assert
        (await RepliesAsync(1)).Should().BeTrue();
        var (recipient, text) = _client.Sent.Single();
        recipient.Should().Be(Alice);
        text.Should().Contain("/add");
    }

    [Fact]
    public async Task IncomingMessage_ShouldRegisterTheSenderAsSignalSubscriber()
    {
        // Arrange
        await StartAsync();

        // Act
        _client.Emit(Alice, "hello");
        await RepliesAsync(1);

        // Assert
        var subscriber = _persistence.GetSubscribers().Should().ContainSingle().Subject;
        subscriber.Id.Should().Be(Alice);
        subscriber.Channel.Should().Be(MessengerChannel.Signal);
    }

    [Fact]
    public async Task IncomingMessage_ShouldAskAnUnknownSenderForThePasswordWhenOneIsConfigured()
    {
        // Arrange
        using var manager = CreateProtectedManager();
        manager.Start();
        await _client.ReceiveStarted;

        // Act
        _client.Emit(Alice, "/add");

        // Assert
        (await RepliesAsync(1)).Should().BeTrue();
        RepliesTo(Alice).Should().ContainSingle().Which.Should().Contain("access password").And.NotContain("Step 1 of 5");
        _persistence.GetSubscribers().Should().BeEmpty();
    }

    [Fact]
    public async Task Password_ShouldAdmitTheSenderAsSignalSubscriberOnce()
    {
        // Arrange
        using var manager = CreateProtectedManager();
        manager.Start();
        await _client.ReceiveStarted;

        // Act
        _client.Emit(Alice, "hello");
        _client.Emit(Alice, Password);
        _client.Emit(Alice, "/add");

        // Assert
        (await RepliesAsync(3)).Should().BeTrue();
        var replies = RepliesTo(Alice).ToList();
        replies[0].Should().Contain("access password");
        replies[1].Should().StartWith("Access granted.").And.Contain("Welcome to the Kleinanzeigen Crawler");
        replies[2].Should().Contain("Step 1 of 5");
        var subscriber = _persistence.GetSubscribers().Should().ContainSingle().Subject;
        subscriber.Id.Should().Be(Alice);
        subscriber.Channel.Should().Be(MessengerChannel.Signal);
    }

    [Fact]
    public async Task ASenderWhoGuessedTheWrongPasswordTenTimes_ShouldNeverBeAnsweredAgain()
    {
        // Arrange
        using var manager = CreateProtectedManager();
        manager.Start();
        await _client.ReceiveStarted;
        _client.Emit(Alice, "hello");
        for (var i = 0; i < AccessGate.MaxFailedAttempts; i++)
        {
            _client.Emit(Alice, "wrong");
        }

        await RepliesAsync(1 + AccessGate.MaxFailedAttempts);

        // Act
        _client.Emit(Alice, Password);
        _client.Emit(Alice, "/help");
        _client.Emit(Bob, "hello");
        var bobAnswered = await Wait.UntilAsync(() => RepliesTo(Bob).Any());
        await Task.Delay(200);

        // Assert
        bobAnswered.Should().BeTrue();
        var aliceReplies = RepliesTo(Alice).ToList();
        aliceReplies.Should().HaveCount(1 + AccessGate.MaxFailedAttempts);
        aliceReplies.Last().Should().Contain("blocked");
        _persistence.GetSubscribers().Should().BeEmpty();
    }

    [Fact]
    public async Task Messages_ShouldBeHandledInTheOrderTheyArrivedPerUser()
    {
        // Arrange
        await StartAsync();

        // Act
        _client.Emit(Alice, "/add");
        _client.Emit(Alice, SearchUrl);
        _client.Emit(Alice, "/skip");

        // Assert
        (await RepliesAsync(3)).Should().BeTrue();
        var replies = RepliesTo(Alice).ToList();
        replies[0].Should().Contain("Step 1 of 5");
        replies[1].Should().Contain("Step 2 of 5");
        replies[2].Should().Contain("Step 3 of 5");
    }

    [Fact]
    public async Task ASlowConversation_ShouldNotBlockOtherUsers()
    {
        // Arrange
        var siteResponse = new TaskCompletionSource<HtmlDocument>();
        _queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).Returns(siteResponse.Task);
        await StartAsync();
        foreach (var message in new[] { "/add", SearchUrl, "/skip", "/skip", "no", "Slow search" })
        {
            _client.Emit(Alice, message);
        }

        await RepliesAsync(6);

        // Act
        _client.Emit(Alice, "yes");
        _client.Emit(Bob, "/help");
        var bobAnswered = await Wait.UntilAsync(() => RepliesTo(Bob).Any());
        var aliceAnsweredEarly = RepliesTo(Alice).Count() > 6;
        siteResponse.SetResult(null);
        var aliceAnswered = await RepliesAsync(8);

        // Assert
        bobAnswered.Should().BeTrue("Bob must not wait for Alice's request to the site");
        aliceAnsweredEarly.Should().BeFalse();
        aliceAnswered.Should().BeTrue();
        RepliesTo(Alice).Last().Should().Contain("Subscription \"Slow search\" created");
    }

    [Fact]
    public async Task LongReplies_ShouldBeSplitIntoMessagesWithinTheLengthLimit()
    {
        // Arrange
        var subscriptions = Enumerable.Range(1, 40).Select(i => CreateSubscription($"Subscription number {i}")).ToArray();
        AddSignalSubscriber(Alice, subscriptions);
        await StartAsync();

        // Act
        _client.Emit(Alice, "/list");

        // Assert
        var complete = await Wait.UntilAsync(() => string.Join("\n", RepliesTo(Alice)).Contains("Subscription number 40 ("));
        complete.Should().BeTrue();
        var replies = RepliesTo(Alice).ToList();
        replies.Should().HaveCountGreaterThan(1).And.OnlyContain(reply => reply.Length <= 2000);
        string.Join("\n", replies).Should().Contain("Subscription number 1 (");
    }

    [Fact]
    public async Task AFailureWhileHandlingAMessage_ShouldBeAnsweredWithAGenericError()
    {
        // Arrange
        var failingPersistence = new Mock<ISubscriptionPersistence>();
        failingPersistence.Setup(p => p.GetSubscribers()).Throws(new InvalidOperationException("storage unavailable"));
        using var manager = new SignalManager(_client, _logger.Object, failingPersistence.Object, _accessAttempts, _parserProvider.Object, new MessagingConfiguration());
        manager.Start();
        await _client.ReceiveStarted;

        // Act
        _client.Emit(Alice, "/list");

        // Assert
        (await RepliesAsync(1)).Should().BeTrue();
        _client.Sent.Single().Text.Should().Contain("Something went wrong");
    }

    [Fact]
    public async Task AFailedReply_ShouldNotStopTheManagerFromHandlingFurtherMessages()
    {
        // Arrange
        var failures = 0;
        _client.SendBehavior = (_, _) => failures++ == 0 ? Task.FromException(new HttpRequestException("rejected", null, HttpStatusCode.BadRequest)) : Task.CompletedTask;
        await StartAsync();

        // Act
        _client.Emit(Alice, "/help");
        await Wait.UntilAsync(() => _client.SendAttempts.ContainsKey(Alice));
        _client.Emit(Alice, "/help");

        // Assert
        (await RepliesAsync(1)).Should().BeTrue();
    }

    [Fact]
    public async Task NotifyAsync_ShouldMessageOnlySignalRecipientsOwningTheSubscription()
    {
        // Arrange
        var subscription = CreateSubscription("Berlin flats");
        AddSignalSubscriber(Alice, subscription);
        AddSignalSubscriber(Bob);
        _persistence.AddSubscriber(new Subscriber { Id = "3001", Channel = MessengerChannel.Telegram, Subscriptions = new List<Subscription> { subscription } });
        var result = new Result { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1"), CreationDate = "Today", Price = "900 EUR" };

        // Act
        var report = await _manager.NotifyAsync(subscription, result);

        // Assert
        var (recipient, text) = _client.Sent.Should().ContainSingle().Subject;
        recipient.Should().Be(Alice);
        text.Should().Be("New result in \"Berlin flats\":\nToday | 900 EUR\nhttps://www.kleinanzeigen.de/s-anzeige/flat/1");
        report.Delivered.Should().Be(1);
        report.TransientFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_ShouldNotMessageSubscribersWhoDisabledTheSubscription()
    {
        // Arrange
        var subscription = CreateSubscription("Paused", enabled: false);
        AddSignalSubscriber(Alice, subscription);

        // Act
        var report = await _manager.NotifyAsync(subscription, new Result { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1") });

        // Assert
        _client.Sent.Should().BeEmpty();
        report.Should().BeSameAs(DeliveryReport.NoRecipients);
    }

    [Fact]
    public async Task NotifyAsync_ShouldKeepNotifyingOtherRecipientsAfterARejectedMessageWithoutReportingItAsTransient()
    {
        // Arrange
        var subscription = CreateSubscription("Berlin flats");
        AddSignalSubscriber(Alice, subscription);
        AddSignalSubscriber(Bob, subscription);
        _client.SendBehavior = (recipient, _) => recipient == Alice
            ? Task.FromException(new HttpRequestException("Unregistered user", null, HttpStatusCode.BadRequest))
            : Task.CompletedTask;

        // Act
        var report = await _manager.NotifyAsync(subscription, new Result { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1") });

        // Assert
        _client.Sent.Select(s => s.Recipient).Should().Equal(Bob);
        _client.SendAttempts[Alice].Should().Be(1, "a rejected message is not retried");
        report.Delivered.Should().Be(1);
        report.TransientFailures.Should().BeEmpty("trying again cannot fix a rejected message");
    }

    [Fact]
    public async Task NotifyAsync_ShouldRetryAMessageThatFailedBecauseOfAnUnreachableApi()
    {
        // Arrange
        var subscription = CreateSubscription("Berlin flats");
        AddSignalSubscriber(Alice, subscription);
        var failures = 0;
        _client.SendBehavior = (_, _) => failures++ == 0 ? Task.FromException(new HttpRequestException("Connection refused")) : Task.CompletedTask;

        // Act
        var report = await _manager.NotifyAsync(subscription, new Result { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1") });

        // Assert
        _client.SendAttempts[Alice].Should().Be(2);
        _client.Sent.Should().ContainSingle();
        report.Delivered.Should().Be(1);
        report.TransientFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_ShouldReportAnUnreachableApiAfterAllAttemptsAndStillNotifyOtherRecipients()
    {
        // Arrange
        var subscription = CreateSubscription("Berlin flats");
        AddSignalSubscriber(Alice, subscription);
        AddSignalSubscriber(Bob, subscription);
        _client.SendBehavior = (recipient, _) => recipient == Alice
            ? Task.FromException(new HttpRequestException("Connection refused"))
            : Task.CompletedTask;

        // Act
        var report = await _manager.NotifyAsync(subscription, new Result { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1") });

        // Assert
        _client.SendAttempts[Alice].Should().Be(3);
        _client.Sent.Select(s => s.Recipient).Should().Equal(Bob);
        report.Delivered.Should().Be(1);
        report.TransientFailures.Should().ContainSingle().Which.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Dispose_ShouldStopReceiving()
    {
        // Arrange
        await StartAsync();

        // Act
        _manager.Dispose();

        // Assert
        (await Wait.UntilAsync(() => _client.ReceiveCompleted.IsCompleted)).Should().BeTrue();
    }
}
