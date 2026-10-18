using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Interfaces;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
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
        _service = new SubscriptionService(_mockPersistence.Object, _mockParserProvider.Object, _mockLogger.Object);
    }
    
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
}
