using Xunit;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Query;
using KleinanzeigenCrawler.Subscriptions;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Subscriptions;

public class SubscriptionHandlerTests
{
    private static readonly Uri SearchUrl = new("https://www.kleinanzeigen.de/s-test/k0");
    private static readonly Uri NewerAd = new("https://www.kleinanzeigen.de/s-anzeige/newer/2");
    private static readonly Uri OlderAd = new("https://www.kleinanzeigen.de/s-anzeige/older/1");

    private readonly Mock<IQueryExecutor> _queryExecutor = new();
    private readonly Mock<IParser> _parser = new();
    private readonly Mock<IOutgoingNotifications> _notifications = new();
    private readonly InMemoryUrlTracker _urlTracker = new();
    private readonly HtmlDocument _searchPage = new();
    private readonly HtmlDocument _adPage = new();

    public SubscriptionHandlerTests()
    {
        // The search page lists the newest ad first
        _parser.SetupGet(p => p.QueryExecutor).Returns(_queryExecutor.Object);
        _parser.Setup(p => p.GetAdditionalPages(_searchPage)).Returns(new List<Uri>());
        _parser.Setup(p => p.ParseLinks(_searchPage)).Returns(new[] { new Result { Link = NewerAd }, new Result { Link = OlderAd } });
        _parser.Setup(p => p.IsMatch(_adPage, It.IsAny<Subscription>())).Returns(true);
        _queryExecutor.Setup(q => q.GetHtml(SearchUrl)).ReturnsAsync(_searchPage);
        _queryExecutor.Setup(q => q.GetHtml(NewerAd)).ReturnsAsync(_adPage);
        _queryExecutor.Setup(q => q.GetHtml(OlderAd)).ReturnsAsync(_adPage);
        _notifications.Setup(n => n.NotifySubscribers(It.IsAny<Subscription>(), It.IsAny<Result>())).Returns(Task.CompletedTask);
    }

    private SubscriptionHandler CreateHandler(IUrlTracker urlTracker) => new(
        _notifications.Object,
        Mock.Of<IParserProvider>(),
        Mock.Of<ILogger>(),
        Mock.Of<ISubscriptionPersistence>(),
        urlTracker,
        new AppConfiguration());

    private static Subscription CreateSubscription(bool firstRunCompleted = true, bool initialPull = false) => new()
    {
        Title = "Test",
        QueryUrl = SearchUrl,
        Enabled = true,
        FirstRunCompleted = firstRunCompleted,
        InitialPull = initialPull
    };

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldNotifyNewMatchesOnlyOnce()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);

        // Act
        var completed = await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        completed.Should().BeTrue();
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)), Times.Once);
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == NewerAd)), Times.Once);
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { OlderAd, NewerAd });
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldNotTreatResultsAsNewWhenTrackerFails()
    {
        // Arrange
        var failingTracker = new Mock<IUrlTracker>();
        failingTracker.Setup(t => t.RefreshIfProcessedAsync(It.IsAny<Guid>(), It.IsAny<Uri>()))
            .ThrowsAsync(new TimeoutException("Redis is unavailable"));
        var handler = CreateHandler(failingTracker.Object);

        // Act
        var act = () => handler.ProcessSubscriptionAsync(CreateSubscription(), _parser.Object);

        // Assert
        await act.Should().ThrowAsync<TimeoutException>();
        _queryExecutor.Verify(q => q.GetHtml(It.Is<Uri>(url => url != SearchUrl)), Times.Never);
        _notifications.Verify(n => n.NotifySubscribers(It.IsAny<Subscription>(), It.IsAny<Result>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldRetryResultWhosePageCouldNotBeLoaded()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);
        _queryExecutor.SetupSequence(q => q.GetHtml(OlderAd))
            .ReturnsAsync((HtmlDocument)null)
            .ReturnsAsync(_adPage);

        // Act
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        var processedAfterFailedLoad = _urlTracker.ProcessedUrls.ToList();
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        processedAfterFailedLoad.Should().BeEquivalentTo(new[] { NewerAd });
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)), Times.Once);
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { OlderAd, NewerAd });
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldGiveUpOnResultAfterMaxLoadAttempts()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);
        _queryExecutor.Setup(q => q.GetHtml(OlderAd)).ReturnsAsync((HtmlDocument)null);

        // Act
        for (var attempt = 1; attempt < SubscriptionHandler.MaxLoadAttempts; attempt++)
        {
            await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        }
        var processedBeforeLastAttempt = _urlTracker.ProcessedUrls.ToList();
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        processedBeforeLastAttempt.Should().NotContain(OlderAd);
        _urlTracker.ProcessedUrls.Should().Contain(OlderAd);
        _queryExecutor.Verify(q => q.GetHtml(OlderAd), Times.Exactly(SubscriptionHandler.MaxLoadAttempts));
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)), Times.Never);
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldNotLetFailingNotificationBlockOtherResults()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);
        _notifications.Setup(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)))
            .ThrowsAsync(new InvalidOperationException("Messenger is unavailable"));

        // Act
        var completed = await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        completed.Should().BeTrue();
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == NewerAd)), Times.Once);
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { NewerAd });
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldGiveUpOnResultAfterMaxFailedNotifications()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);
        _notifications.Setup(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)))
            .ThrowsAsync(new InvalidOperationException("Messenger is unavailable"));

        // Act
        for (var attempt = 1; attempt <= SubscriptionHandler.MaxLoadAttempts; attempt++)
        {
            await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        }

        // Assert
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { OlderAd, NewerAd });
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)), Times.Exactly(SubscriptionHandler.MaxLoadAttempts));
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == NewerAd)), Times.Once);
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldNotRetryDeletedAd()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);
        _queryExecutor.Setup(q => q.GetHtml(OlderAd)).ThrowsAsync(new PageNotFoundException(OlderAd, HttpStatusCode.NotFound));

        // Act
        var completed = await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        completed.Should().BeTrue();
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { OlderAd, NewerAd });
        _notifications.Verify(n => n.NotifySubscribers(subscription, It.Is<Result>(r => r.Link == OlderAd)), Times.Never);
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldReportIncompleteRunWhenAdditionalPageDoesNotExist()
    {
        // Arrange
        var secondPageUrl = new Uri("https://www.kleinanzeigen.de/s-test/seite:2/k0");
        _parser.Setup(p => p.GetAdditionalPages(_searchPage)).Returns(new List<Uri> { secondPageUrl });
        _queryExecutor.Setup(q => q.GetHtml(secondPageUrl)).ThrowsAsync(new PageNotFoundException(secondPageUrl, HttpStatusCode.NotFound));
        var handler = CreateHandler(_urlTracker);

        // Act
        var completed = await handler.ProcessSubscriptionAsync(CreateSubscription(), _parser.Object);

        // Assert
        completed.Should().BeFalse();
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { OlderAd, NewerAd });
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldForgetFailedAttemptsOfResultThatIsNoLongerListed()
    {
        // Arrange
        var subscription = CreateSubscription();
        var handler = CreateHandler(_urlTracker);
        _queryExecutor.Setup(q => q.GetHtml(OlderAd)).ReturnsAsync((HtmlDocument)null);
        for (var attempt = 1; attempt < SubscriptionHandler.MaxLoadAttempts; attempt++)
        {
            await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        }
        _parser.Setup(p => p.ParseLinks(_searchPage)).Returns(new[] { new Result { Link = NewerAd } });
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);
        _parser.Setup(p => p.ParseLinks(_searchPage)).Returns(new[] { new Result { Link = NewerAd }, new Result { Link = OlderAd } });
        _urlTracker.ProcessedUrls.Remove(NewerAd);

        // Act
        await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        _urlTracker.ProcessedUrls.Should().NotContain(OlderAd);
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldOnlyRememberResultsOfFirstRunWithoutInitialPull()
    {
        // Arrange
        var subscription = CreateSubscription(firstRunCompleted: false, initialPull: false);
        var handler = CreateHandler(_urlTracker);

        // Act
        var completed = await handler.ProcessSubscriptionAsync(subscription, _parser.Object);

        // Assert
        completed.Should().BeTrue();
        _urlTracker.ProcessedUrls.Should().BeEquivalentTo(new[] { OlderAd, NewerAd });
        _queryExecutor.Verify(q => q.GetHtml(It.Is<Uri>(url => url != SearchUrl)), Times.Never);
        _notifications.Verify(n => n.NotifySubscribers(It.IsAny<Subscription>(), It.IsAny<Result>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSubscriptionAsync_ShouldReportIncompleteRunWhenSearchPageCannotBeLoaded()
    {
        // Arrange
        _queryExecutor.Setup(q => q.GetHtml(SearchUrl)).ReturnsAsync((HtmlDocument)null);
        var handler = CreateHandler(_urlTracker);

        // Act
        var completed = await handler.ProcessSubscriptionAsync(CreateSubscription(), _parser.Object);

        // Assert
        completed.Should().BeFalse();
        _urlTracker.ProcessedUrls.Should().BeEmpty();
    }

    private sealed class InMemoryUrlTracker : IUrlTracker
    {
        public HashSet<Uri> ProcessedUrls { get; } = new();

        public void VerifyConnection()
        {
        }

        public Task<bool> RefreshIfProcessedAsync(Guid subscriptionId, Uri url) => Task.FromResult(ProcessedUrls.Contains(url));

        public Task MarkProcessedAsync(Guid subscriptionId, Uri url)
        {
            ProcessedUrls.Add(url);
            return Task.CompletedTask;
        }
    }
}
