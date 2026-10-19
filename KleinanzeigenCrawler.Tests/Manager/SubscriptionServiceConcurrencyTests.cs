using Xunit;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Persistence;
using Moq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

/// <summary>
/// The crawler and the persistence read the subscriptions of a subscriber while conversations add and delete them
/// </summary>
public class SubscriptionServiceConcurrencyTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"kc-concurrency-{Guid.NewGuid():N}");

    public SubscriptionServiceConcurrencyTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public async Task AddingAndDeletingSubscriptions_ShouldNotBreakConcurrentReadersAndSaves()
    {
        // Arrange
        var logger = Mock.Of<ILogger>();
        var persistence = new SubscriptionPersistence(
            logger,
            new JsonStorage(logger),
            new AppConfiguration { Crawler = { DataPath = _dataDirectory } });
        persistence.AddSubscriber(new Subscriber { Id = "1", Channel = MessengerChannel.Telegram });

        var queryExecutor = new Mock<IQueryExecutor>();
        queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).ReturnsAsync((HtmlDocument)null);
        var parser = new Mock<IParser>();
        parser.SetupGet(p => p.QueryExecutor).Returns(queryExecutor.Object);
        var parserProvider = new Mock<IParserProvider>();
        parserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(parser.Object);
        var service = new SubscriptionService(persistence, parserProvider.Object, logger, MessengerChannel.Telegram, allowNewUsers: true);

        using var stop = new CancellationTokenSource();
        var failures = new ConcurrentBag<Exception>();
        var crawler = Task.Run(() => Repeat(stop.Token, failures, () =>
            persistence.GetEnabledSubscriptions().Count(subscription => subscription.Enabled)));
        var saver = Task.Run(() => Repeat(stop.Token, failures, () =>
        {
            foreach (var subscriber in persistence.GetSubscribers())
            {
                persistence.UpdateSubscriber(subscriber);
            }
        }));

        // Act
        for (var i = 0; i < 150; i++)
        {
            var created = await service.CreateSubscriptionAsync("1", "https://example.com/search", null, null, false, $"Search {i}");
            created.IsSuccess.Should().BeTrue(created.ErrorMessage);
            service.DeleteSubscription("1", created.Data.Id).IsSuccess.Should().BeTrue();
        }

        stop.Cancel();
        await Task.WhenAll(crawler, saver);

        // Assert
        failures.Should().BeEmpty();
        service.GetSubscriptions("1").Should().BeEmpty();
    }

    [Fact]
    public async Task GetSubscriptions_ShouldReturnAStableSnapshotWhileSubscriptionsChange()
    {
        // Arrange
        var logger = Mock.Of<ILogger>();
        var persistence = new SubscriptionPersistence(
            logger,
            new JsonStorage(logger),
            new AppConfiguration { Crawler = { DataPath = _dataDirectory } });
        persistence.AddSubscriber(new Subscriber { Id = "1" });
        var queryExecutor = new Mock<IQueryExecutor>();
        queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).ReturnsAsync((HtmlDocument)null);
        var parser = new Mock<IParser>();
        parser.SetupGet(p => p.QueryExecutor).Returns(queryExecutor.Object);
        var parserProvider = new Mock<IParserProvider>();
        parserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(parser.Object);
        var service = new SubscriptionService(persistence, parserProvider.Object, logger, MessengerChannel.Telegram, allowNewUsers: true);
        var created = await service.CreateSubscriptionAsync("1", "https://example.com/search", null, null, false, "First");
        var snapshot = service.GetSubscriptions("1");

        // Act
        service.DeleteSubscription("1", created.Data.Id);
        await service.CreateSubscriptionAsync("1", "https://example.com/search", null, null, false, "Second");

        // Assert
        snapshot.Select(s => s.Title).Should().Equal("First");
    }

    private static void Repeat(CancellationToken stop, ConcurrentBag<Exception> failures, Action action)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }
}
