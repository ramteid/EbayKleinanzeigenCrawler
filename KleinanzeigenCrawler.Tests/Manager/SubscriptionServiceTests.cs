using Xunit;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Query;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

/// <summary>
/// Tests for SubscriptionService covering critical business logic
/// </summary>
public class SubscriptionServiceTests
{
    private readonly Mock<ISubscriptionPersistence> _mockPersistence;
    private readonly Mock<IParserProvider> _mockParserProvider;
    private readonly Mock<ILogger> _mockLogger;
    private readonly SubscriptionService _service;
    
    public SubscriptionServiceTests()
    {
        _mockPersistence = new Mock<ISubscriptionPersistence>();
        _mockParserProvider = new Mock<IParserProvider>();
        _mockLogger = new Mock<ILogger>();
        _service = CreateService(MessengerChannel.Telegram);
    }
    
    private SubscriptionService CreateService(MessengerChannel channel) =>
        new(_mockPersistence.Object, _mockParserProvider.Object, _mockLogger.Object, channel);
    
    [Fact]
    public async Task CreateSubscriptionAsync_ShouldFailWithInvalidUrl()
    {
        // Act
        var result = await _service.CreateSubscriptionAsync(
            "user1", 
            "not-a-url", 
            null, 
            null, 
            false, 
            "Test");
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid URL");
    }
    
    [Fact]
    public async Task CreateSubscriptionAsync_ShouldFailWithHttpUrl()
    {
        // Act
        var result = await _service.CreateSubscriptionAsync(
            "user1", 
            "http://example.com", 
            null, 
            null, 
            false, 
            "Test");
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid URL");
    }
    
    [Fact]
    public async Task CreateSubscriptionAsync_ShouldFailWhenSubscriberNotFound()
    {
        // Arrange
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(Array.Empty<Subscriber>());
        var mockParser = new Mock<IParser>();
        _mockParserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(mockParser.Object);
        
        // Act
        var result = await _service.CreateSubscriptionAsync(
            "user1", 
            "https://example.com", 
            null, 
            null, 
            false, 
            "Test");
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Subscriber not found");
    }
    
    [Fact]
    public async Task CreateSubscriptionAsync_ShouldFailWhenDuplicateTitleExists()
    {
        // Arrange
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription>
            {
                new Subscription { Title = "Existing", QueryUrl = new Uri("https://example.com") }
            }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        var mockParser = new Mock<IParser>();
        _mockParserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(mockParser.Object);
        
        // Act
        var result = await _service.CreateSubscriptionAsync(
            "user1", 
            "https://example.com/new", 
            null, 
            null, 
            false, 
            "Existing"); // Same title
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
    }
    
    [Fact]
    public void DeleteSubscription_ShouldRemoveSubscription()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription>
            {
                new Subscription { Id = subscriptionId, Title = "Test", QueryUrl = new Uri("https://example.com") }
            }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.DeleteSubscription("user1", subscriptionId);
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        subscriber.Subscriptions.Should().BeEmpty();
        _mockPersistence.Verify(p => p.UpdateSubscriber(subscriber), Times.Once);
    }
    
    [Fact]
    public void DeleteSubscription_ShouldFailWhenSubscriptionNotFound()
    {
        // Arrange
        var subscriber = new Subscriber { Id = "user1", Subscriptions = new List<Subscription>() };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.DeleteSubscription("user1", Guid.NewGuid());
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Subscription not found");
    }
    
    [Fact]
    public void SetSubscriptionEnabled_ShouldToggleEnabledState()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = new Subscription 
        { 
            Id = subscriptionId, 
            Title = "Test", 
            Enabled = false,
            QueryUrl = new Uri("https://example.com")
        };
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription> { subscription }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.SetSubscriptionEnabled("user1", subscriptionId, true);
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        subscription.Enabled.Should().BeTrue();
        _mockPersistence.Verify(p => p.UpdateSubscriber(subscriber), Times.Once);
    }
    
    [Fact]
    public void UpdateSubscription_ShouldUpdateKeywords()
    {
        // Arrange
        var subscriptionId = Guid.NewGuid();
        var subscription = new Subscription 
        { 
            Id = subscriptionId, 
            Title = "Test",
            IncludeKeywords = new List<string> { "old" },
            ExcludeKeywords = new List<string>(),
            QueryUrl = new Uri("https://example.com")
        };
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription> { subscription }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.UpdateSubscription(
            "user1", 
            subscriptionId, 
            new List<string> { "new1", "new2" }, 
            new List<string> { "exclude" });
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        subscription.IncludeKeywords.Should().Contain(new[] { "new1", "new2" });
        subscription.ExcludeKeywords.Should().Contain("exclude");
        _mockPersistence.Verify(p => p.UpdateSubscriber(subscriber), Times.Once);
    }
    
    [Fact]
    public void GetSubscriptions_ShouldReturnUserSubscriptions()
    {
        // Arrange
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription>
            {
                new Subscription { Title = "Sub1", QueryUrl = new Uri("https://example.com") },
                new Subscription { Title = "Sub2", QueryUrl = new Uri("https://example.com") }
            }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.GetSubscriptions("user1");
        
        // Assert
        result.Should().HaveCount(2);
        result.Select(s => s.Title).Should().Contain(new[] { "Sub1", "Sub2" });
    }
    
    [Fact]
    public void GetSubscriptions_ShouldReturnEmptyListForUnknownUser()
    {
        // Arrange
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(Array.Empty<Subscriber>());
        
        // Act
        var result = _service.GetSubscriptions("unknown");
        
        // Assert
        result.Should().BeEmpty();
    }
    
    [Fact]
    public void DeleteAllSubscriptions_ShouldClearSubscriptionList()
    {
        // Arrange
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription>
            {
                new Subscription { Title = "Sub1", QueryUrl = new Uri("https://example.com") },
                new Subscription { Title = "Sub2", QueryUrl = new Uri("https://example.com") }
            }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.DeleteAllSubscriptions("user1");
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        subscriber.Subscriptions.Should().BeEmpty();
        _mockPersistence.Verify(p => p.UpdateSubscriber(subscriber), Times.Once);
    }
    
    [Fact]
    public void IsSubscriber_ShouldMatchOnlySubscribersOfTheServiceChannel()
    {
        // Arrange
        var telegramSubscriber = new Subscriber { Id = "42", Channel = MessengerChannel.Telegram };
        var signalSubscriber = new Subscriber { Id = "43", Channel = MessengerChannel.Signal };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { telegramSubscriber, signalSubscriber });
        var signalService = CreateService(MessengerChannel.Signal);
        
        // Act & Assert
        _service.IsSubscriber("42").Should().BeTrue();
        _service.IsSubscriber("43").Should().BeFalse();
        signalService.IsSubscriber("43").Should().BeTrue();
        signalService.IsSubscriber("42").Should().BeFalse();
        _service.IsSubscriber("unknown").Should().BeFalse();
    }
    
    [Fact]
    public void RegisterSubscriber_ShouldAddSubscriberOnTheServiceChannel()
    {
        // Arrange
        var signalService = CreateService(MessengerChannel.Signal);
        
        // Act
        signalService.RegisterSubscriber("account-1");
        
        // Assert
        _mockPersistence.Verify(p => p.AddSubscriber(It.Is<Subscriber>(s =>
            s.Id == "account-1" && s.Channel == MessengerChannel.Signal && s.Subscriptions.Count == 0)), Times.Once);
    }
    
    [Fact]
    public void GetRecipients_ShouldReturnOnlySubscribersOfTheServiceChannelOwningTheSubscription()
    {
        // Arrange
        var subscription = new Subscription { Title = "Shared", QueryUrl = new Uri("https://example.com"), Enabled = true };
        var telegramOwner = new Subscriber { Id = "1", Channel = MessengerChannel.Telegram, Subscriptions = new List<Subscription> { subscription } };
        var signalOwner = new Subscriber { Id = "2", Channel = MessengerChannel.Signal, Subscriptions = new List<Subscription> { subscription } };
        var telegramOther = new Subscriber { Id = "3", Channel = MessengerChannel.Telegram };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { telegramOwner, signalOwner, telegramOther });
        
        // Act
        var result = _service.GetRecipients(subscription);
        
        // Assert
        result.Should().ContainSingle().Which.Should().BeSameAs(telegramOwner);
    }
    
    [Fact]
    public void GetRecipients_ShouldExcludeSubscribersWhoDisabledTheSubscription()
    {
        // Arrange
        var subscription = new Subscription { Title = "Paused", QueryUrl = new Uri("https://example.com"), Enabled = false };
        var owner = new Subscriber { Id = "1", Subscriptions = new List<Subscription> { subscription } };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { owner });
        
        // Act
        var result = _service.GetRecipients(subscription);
        
        // Assert
        result.Should().BeEmpty();
    }
    
    [Fact]
    public void GetSubscriptions_ShouldIgnoreSubscribersOfOtherChannels()
    {
        // Arrange
        var signalSubscriber = new Subscriber
        {
            Id = "user1",
            Channel = MessengerChannel.Signal,
            Subscriptions = new List<Subscription> { new Subscription { Title = "Signal sub", QueryUrl = new Uri("https://example.com") } }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { signalSubscriber });
        
        // Act
        var result = _service.GetSubscriptions("user1");
        
        // Assert
        result.Should().BeEmpty();
    }
    
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateSubscriptionAsync_ShouldFailWithoutTitle(string title)
    {
        // Arrange
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { new Subscriber { Id = "user1" } });
        
        // Act
        var result = await _service.CreateSubscriptionAsync("user1", "https://example.com", null, null, false, title);
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("title");
        _mockPersistence.Verify(p => p.UpdateSubscriber(It.IsAny<Subscriber>()), Times.Never);
    }
    
    [Theory]
    [InlineData("Existing")]
    [InlineData("existing")]
    [InlineData("EXISTING")]
    [InlineData("  Existing  ")]
    public async Task CreateSubscriptionAsync_ShouldRejectDuplicateTitleIgnoringCaseAndSurroundingWhitespaceBeforeQueryingTheSite(string title)
    {
        // Arrange
        var subscriber = new Subscriber
        {
            Id = "user1",
            Subscriptions = new List<Subscription> { new Subscription { Title = "Existing", QueryUrl = new Uri("https://example.com") } }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        var queryExecutor = SetupParserWithoutHtml();
        
        // Act
        var result = await _service.CreateSubscriptionAsync("user1", "https://example.com/new", null, null, false, title);
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
        queryExecutor.Verify(q => q.GetHtml(It.IsAny<Uri>()), Times.Never);
        subscriber.Subscriptions.Should().ContainSingle();
    }
    
    [Theory]
    [InlineData("  Fresh title  ", "Fresh title")]
    [InlineData("Existing 2", "Existing 2")]
    public void ValidateTitle_ShouldAcceptAFreeTitleAndReturnItTrimmed(string title, string expected)
    {
        // Arrange
        var subscriber = new Subscriber
        {
            Id = "user1",
            Subscriptions = new List<Subscription> { new Subscription { Title = "Existing", QueryUrl = new Uri("https://example.com") } }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.ValidateTitle("user1", title);
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(expected);
    }
    
    [Theory]
    [InlineData(null, "provide a title")]
    [InlineData("   ", "provide a title")]
    [InlineData("existing", "already exists")]
    [InlineData(" EXISTING ", "already exists")]
    public void ValidateTitle_ShouldRejectBlankAndDuplicateTitles(string title, string expectedError)
    {
        // Arrange
        var subscriber = new Subscriber
        {
            Id = "user1",
            Subscriptions = new List<Subscription> { new Subscription { Title = "Existing", QueryUrl = new Uri("https://example.com") } }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.ValidateTitle("user1", title);
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain(expectedError);
    }
    
    [Fact]
    public void ValidateTitle_ShouldFailForUnknownSubscriber()
    {
        // Arrange
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(Array.Empty<Subscriber>());
        
        // Act
        var result = _service.ValidateTitle("unknown", "Title");
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Subscriber not found");
    }
    
    [Fact]
    public async Task CreateSubscriptionAsync_ShouldAddSubscriptionWithTrimmedTitle()
    {
        // Arrange
        var subscriber = new Subscriber { Id = "user1" };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        SetupParserWithoutHtml();
        
        // Act
        var result = await _service.CreateSubscriptionAsync("user1", "https://example.com/search", null, null, true, "  My search  ");
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Title.Should().Be("My search");
        result.Data.Enabled.Should().BeTrue();
        subscriber.Subscriptions.Should().ContainSingle().Which.Should().BeSameAs(result.Data);
        _mockPersistence.Verify(p => p.UpdateSubscriber(subscriber), Times.Once);
    }
    
    [Fact]
    public void DisableAllSubscriptions_ShouldDisableEnabledSubscriptionsAndReportTheirCount()
    {
        // Arrange
        var subscriber = new Subscriber
        {
            Id = "user1",
            Subscriptions = new List<Subscription>
            {
                new Subscription { Title = "On 1", QueryUrl = new Uri("https://example.com"), Enabled = true },
                new Subscription { Title = "On 2", QueryUrl = new Uri("https://example.com"), Enabled = true },
                new Subscription { Title = "Off", QueryUrl = new Uri("https://example.com"), Enabled = false }
            }
        };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.DisableAllSubscriptions("user1");
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(2);
        subscriber.Subscriptions.Should().OnlyContain(s => !s.Enabled);
        _mockPersistence.Verify(p => p.UpdateSubscriber(subscriber), Times.Once);
    }
    
    [Fact]
    public void DisableAllSubscriptions_ShouldNotPersistWhenNothingIsEnabled()
    {
        // Arrange
        var subscriber = new Subscriber { Id = "user1" };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        
        // Act
        var result = _service.DisableAllSubscriptions("user1");
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(0);
        _mockPersistence.Verify(p => p.UpdateSubscriber(It.IsAny<Subscriber>()), Times.Never);
    }
    
    [Fact]
    public void DisableAllSubscriptions_ShouldFailForUnknownSubscriber()
    {
        // Arrange
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(Array.Empty<Subscriber>());
        
        // Act
        var result = _service.DisableAllSubscriptions("unknown");
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Subscriber not found");
    }
    
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task CreateSubscriptionAsync_ShouldRejectASearchUrlThatTheSiteDoesNotKnow(HttpStatusCode status)
    {
        // Arrange
        var subscriber = new Subscriber { Id = "user1" };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        var queryExecutor = SetupParserWithoutHtml();
        queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>()))
            .ThrowsAsync(new PageNotFoundException(new Uri("https://example.com/search"), status));
        
        // Act
        var result = await _service.CreateSubscriptionAsync("user1", "https://example.com/search", null, null, false, "Gone search");
        
        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found").And.Contain(((int)status).ToString());
        subscriber.Subscriptions.Should().BeEmpty();
        _mockPersistence.Verify(p => p.UpdateSubscriber(It.IsAny<Subscriber>()), Times.Never);
    }
    
    [Fact]
    public async Task CreateSubscriptionAsync_ShouldStillAddTheSubscriptionWhenTheResultCountCannotBeChecked()
    {
        // Arrange
        var subscriber = new Subscriber { Id = "user1" };
        _mockPersistence.Setup(p => p.GetSubscribers()).Returns(new[] { subscriber });
        var queryExecutor = SetupParserWithoutHtml();
        queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).ThrowsAsync(new HttpRequestException("Connection refused"));
        
        // Act
        var result = await _service.CreateSubscriptionAsync("user1", "https://example.com/search", null, null, false, "Flaky site");
        
        // Assert
        result.IsSuccess.Should().BeTrue();
        subscriber.Subscriptions.Should().ContainSingle();
    }
    
    private Mock<IQueryExecutor> SetupParserWithoutHtml()
    {
        var queryExecutor = new Mock<IQueryExecutor>();
        queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).ReturnsAsync((HtmlDocument)null);
        var parser = new Mock<IParser>();
        parser.SetupGet(p => p.QueryExecutor).Returns(queryExecutor.Object);
        _mockParserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(parser.Object);
        return queryExecutor;
    }
}
