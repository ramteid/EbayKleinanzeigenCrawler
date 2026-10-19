using Xunit;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Subscriptions;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

/// <summary>
/// The crawler, the composite notifier and a real messenger together: when is a result notified again?
/// </summary>
public class NotificationRetryTests : IDisposable
{
    private const string Owner = "11111111-aaaa-bbbb-cccc-000000000001";
    private static readonly Uri SearchUrl = new("https://www.kleinanzeigen.de/s-test/k0");
    private static readonly Uri Ad = new("https://www.kleinanzeigen.de/s-anzeige/flat/1");

    private readonly InMemorySubscriptionPersistence _persistence = new();
    private readonly FakeSignalClient _signal = new();
    private readonly ConcurrentDictionary<Uri, bool> _processed = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly Mock<IParser> _parser = new();
    private readonly Subscription _subscription;
    private readonly SignalManager _signalManager;
    private readonly SubscriptionHandler _handler;

    public NotificationRetryTests()
    {
        _subscription = new Subscription { Title = "Flats", QueryUrl = SearchUrl, Enabled = true, FirstRunCompleted = true };
        _persistence.AddSubscriber(new Subscriber
        {
            Id = Owner,
            Channel = MessengerChannel.Signal,
            Subscriptions = new List<Subscription> { _subscription }
        });

        var searchPage = new HtmlDocument();
        var adPage = new HtmlDocument();
        var queryExecutor = new Mock<IQueryExecutor>();
        queryExecutor.Setup(q => q.GetHtml(SearchUrl)).ReturnsAsync(searchPage);
        queryExecutor.Setup(q => q.GetHtml(Ad)).ReturnsAsync(adPage);
        _parser.SetupGet(p => p.QueryExecutor).Returns(queryExecutor.Object);
        _parser.Setup(p => p.GetAdditionalPages(searchPage)).Returns(new List<Uri>());
        _parser.Setup(p => p.ParseLinks(searchPage)).Returns(new[] { new Result { Link = Ad } });
        _parser.Setup(p => p.IsMatch(adPage, It.IsAny<Subscription>())).Returns(true);

        var urlTracker = new Mock<IUrlTracker>();
        urlTracker.Setup(t => t.RefreshIfProcessedAsync(It.IsAny<Guid>(), It.IsAny<Uri>()))
            .ReturnsAsync((Guid _, Uri url) => _processed.ContainsKey(url));
        urlTracker.Setup(t => t.MarkProcessedAsync(It.IsAny<Guid>(), It.IsAny<Uri>()))
            .Returns((Guid _, Uri url) =>
            {
                _processed[url] = true;
                return Task.CompletedTask;
            });

        _signalManager = new SignalManager(_signal, _logger.Object, _persistence, new InMemoryAccessAttemptPersistence(), Mock.Of<IParserProvider>(), new MessagingConfiguration());
        var telegramStandIn = new Mock<IMessenger>();
        telegramStandIn.SetupGet(m => m.Channel).Returns(MessengerChannel.Telegram);
        telegramStandIn.Setup(m => m.NotifyAsync(It.IsAny<Subscription>(), It.IsAny<Result>())).ReturnsAsync(DeliveryReport.NoRecipients);

        _handler = new SubscriptionHandler(
            new CompositeNotifier(new IMessenger[] { telegramStandIn.Object, _signalManager }),
            Mock.Of<IParserProvider>(),
            _logger.Object,
            _persistence,
            urlTracker.Object,
            new AppConfiguration());
    }

    public void Dispose()
    {
        _signalManager.Dispose();
    }

    private Task RunCrawlerPassAsync() => _handler.ProcessSubscriptionAsync(_subscription, _parser.Object);

    [Fact]
    public async Task AResultIsNotifiedOnceAndNotAgain()
    {
        // Act
        await RunCrawlerPassAsync();
        await RunCrawlerPassAsync();

        // Assert
        _signal.Sent.Should().ContainSingle().Which.Recipient.Should().Be(Owner);
        _processed.Keys.Should().Equal(Ad);
    }

    [Fact]
    public async Task AResultIsRetriedInTheNextPassWhenTheSignalApiWasUnreachableAndThenDeliveredExactlyOnce()
    {
        // Arrange
        var failures = 0;
        _signal.SendBehavior = (_, _) => ++failures <= 3
            ? Task.FromException(new HttpRequestException("Connection refused"))
            : Task.CompletedTask;

        // Act
        await RunCrawlerPassAsync();
        var sentAfterOutage = _signal.Sent.Count;
        var processedAfterOutage = _processed.Count;
        await RunCrawlerPassAsync();
        await RunCrawlerPassAsync();

        // Assert
        sentAfterOutage.Should().Be(0);
        processedAfterOutage.Should().Be(0, "the result must stay unprocessed until somebody received the notification");
        _signal.Sent.Should().ContainSingle().Which.Recipient.Should().Be(Owner);
        _processed.Keys.Should().Equal(Ad);
    }

    [Fact]
    public async Task AResultIsNotRetriedWhenSignalRejectedTheMessage()
    {
        // Arrange
        _signal.SendBehavior = (_, _) => Task.FromException(new HttpRequestException("Unregistered user", null, HttpStatusCode.BadRequest));

        // Act
        await RunCrawlerPassAsync();
        await RunCrawlerPassAsync();

        // Assert
        _signal.SendAttempts[Owner].Should().Be(1, "a rejected message is rejected again");
        _processed.Keys.Should().Equal(Ad);
    }
}
