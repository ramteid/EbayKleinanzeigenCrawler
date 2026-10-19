using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Persistence;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Interfaces;
using Moq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KleinanzeigenCrawler.Tests.Persistence;

/// <summary>
/// Tests for SubscriptionPersistence focusing on critical business logic
/// Note: Full integration tests with actual file I/O are better suited for integration test suite
/// </summary>
public class SubscriptionPersistenceTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly FakeDataStorage _fakeStorage;
    private readonly AppConfiguration _config;
    
    public SubscriptionPersistenceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _fakeStorage = new FakeDataStorage();
        _config = new AppConfiguration();
    }
    
    private SubscriptionPersistence CreatePersistence() =>
        new SubscriptionPersistence(_mockLogger.Object, _fakeStorage, _config);
    
    [Fact]
    public void AddSubscriber_ShouldAddSubscriber()
    {
        // Arrange
        var persistence = CreatePersistence();
        var subscriber = new Subscriber { Id = "user1", Subscriptions = new List<Subscription>() };
        
        // Act
        persistence.AddSubscriber(subscriber);
        
        // Assert
        var subscribers = persistence.GetSubscribers();
        subscribers.Should().ContainSingle();
        subscribers[0].Id.Should().Be("user1");
    }
    
    [Fact]
    public void GetSubscribers_ShouldReturnAllSubscribers()
    {
        // Arrange
        var persistence = CreatePersistence();
        persistence.AddSubscriber(new Subscriber { Id = "user1", Subscriptions = new List<Subscription>() });
        persistence.AddSubscriber(new Subscriber { Id = "user2", Subscriptions = new List<Subscription>() });
        
        // Act
        var result = persistence.GetSubscribers();
        
        // Assert
        result.Should().HaveCount(2);
        result.Select(s => s.Id).Should().Contain(new[] { "user1", "user2" });
    }
    
    [Fact]
    public void GetEnabledSubscriptions_ShouldReturnOnlyEnabledOnes()
    {
        // Arrange
        var persistence = CreatePersistence();
        var subscriber1 = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription>
            {
                new Subscription { Title = "Sub1", Enabled = true, QueryUrl = new Uri("https://example.com") },
                new Subscription { Title = "Sub2", Enabled = false, QueryUrl = new Uri("https://example.com") }
            }
        };
        var subscriber2 = new Subscriber 
        { 
            Id = "user2", 
            Subscriptions = new List<Subscription>
            {
                new Subscription { Title = "Sub3", Enabled = true, QueryUrl = new Uri("https://example.com") }
            }
        };
        persistence.AddSubscriber(subscriber1);
        persistence.AddSubscriber(subscriber2);
        
        // Act
        var enabled = persistence.GetEnabledSubscriptions();
        
        // Assert
        enabled.Should().HaveCount(2);
        enabled.Should().OnlyContain(s => s.Enabled);
        enabled.Select(s => s.Title).Should().Contain(new[] { "Sub1", "Sub3" });
    }
    
    [Fact]
    public void EnsureFirstRunCompletedAndSave_ShouldUpdateSubscriptionWhenNotCompleted()
    {
        // Arrange
        var persistence = CreatePersistence();
        var subscription = new Subscription 
        { 
            Id = Guid.NewGuid(),
            Title = "Test",
            Enabled = true,
            FirstRunCompleted = false,
            QueryUrl = new Uri("https://example.com")
        };
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription> { subscription }
        };
        persistence.AddSubscriber(subscriber);
        
        // Act
        persistence.EnsureFirstRunCompletedAndSave(subscription);
        
        // Assert
        var updatedSubscribers = persistence.GetSubscribers();
        updatedSubscribers[0].Subscriptions[0].FirstRunCompleted.Should().BeTrue();
    }
    
    [Fact]
    public void EnsureFirstRunCompletedAndSave_ShouldNotUpdateWhenAlreadyCompleted()
    {
        // Arrange
        var persistence = CreatePersistence();
        var subscription = new Subscription 
        { 
            Id = Guid.NewGuid(),
            Title = "Test",
            Enabled = true,
            FirstRunCompleted = true,
            QueryUrl = new Uri("https://example.com")
        };
        var subscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription> { subscription }
        };
        persistence.AddSubscriber(subscriber);
        
        // Act
        persistence.EnsureFirstRunCompletedAndSave(subscription);
        
        // Assert - should still be true
        var updatedSubscribers = persistence.GetSubscribers();
        updatedSubscribers[0].Subscriptions[0].FirstRunCompleted.Should().BeTrue();
    }
    
    [Fact]
    public void UpdateSubscriber_ShouldReplaceExistingSubscriber()
    {
        // Arrange
        var persistence = CreatePersistence();
        var subscriber = new Subscriber { Id = "user1", Subscriptions = new List<Subscription>() };
        persistence.AddSubscriber(subscriber);
        
        var updatedSubscriber = new Subscriber 
        { 
            Id = "user1", 
            Subscriptions = new List<Subscription> 
            { 
                new Subscription { Title = "New", QueryUrl = new Uri("https://example.com") } 
            }
        };
        
        // Act
        persistence.UpdateSubscriber(updatedSubscriber);
        
        // Assert
        var result = persistence.GetSubscribers();
        result.Should().ContainSingle();
        result[0].Subscriptions.Should().HaveCount(1);
        result[0].Subscriptions[0].Title.Should().Be("New");
    }
    
    [Fact]
    public void RestoreData_ShouldHandleNullPayloadGracefully()
    {
        // Arrange
        _fakeStorage.SubscribersToLoad = null;

        // Act
        var persistence = CreatePersistence();

        // Assert
        persistence.GetSubscribers().Should().BeEmpty();
    }
    
    [Fact]
    public void RestoreData_ShouldDeduplicateAndIgnoreInvalidEntries()
    {
        // Arrange
        _fakeStorage.SubscribersToLoad = new ConcurrentBag<Subscriber>
        {
            new Subscriber
            {
                Id = "user1",
                Subscriptions = new List<Subscription> { new Subscription { Title = "Old", QueryUrl = new Uri("https://example.com") } }
            },
            null,
            new Subscriber
            {
                Id = string.Empty,
                Subscriptions = new List<Subscription>()
            },
            new Subscriber
            {
                Id = "user1",
                Subscriptions = new List<Subscription> { new Subscription { Title = "Latest", QueryUrl = new Uri("https://example.org") } }
            },
            new Subscriber
            {
                Id = "user2",
                Subscriptions = new List<Subscription> { new Subscription { Title = "Another", QueryUrl = new Uri("https://example.net") } }
            }
        };

        // Act
        var persistence = CreatePersistence();
        var subscribers = persistence.GetSubscribers();

        // Assert
        subscribers.Should().HaveCount(2);
        var user1Subscriptions = subscribers.Single(s => s.Id == "user1").Subscriptions;
        user1Subscriptions.Should().HaveCount(2);
        user1Subscriptions.Select(s => s.Title).Should().Contain(new[] { "Old", "Latest" });
        subscribers.Single(s => s.Id == "user2").Subscriptions.Should().HaveCount(1);
    }
    
    [Fact]
    public void AddSubscriber_ShouldKeepSubscribersWithSameIdOnDifferentChannelsSeparate()
    {
        // Arrange
        var persistence = CreatePersistence();
        
        // Act
        persistence.AddSubscriber(new Subscriber { Id = "42", Channel = MessengerChannel.Telegram });
        persistence.AddSubscriber(new Subscriber { Id = "42", Channel = MessengerChannel.Signal });
        
        // Assert
        persistence.GetSubscribers().Select(s => s.Channel)
            .Should().BeEquivalentTo(new[] { MessengerChannel.Telegram, MessengerChannel.Signal });
    }
    
    [Fact]
    public void UpdateSubscriber_ShouldOnlyReplaceSubscriberOfSameChannel()
    {
        // Arrange
        var persistence = CreatePersistence();
        persistence.AddSubscriber(new Subscriber { Id = "42", Channel = MessengerChannel.Telegram });
        persistence.AddSubscriber(new Subscriber { Id = "42", Channel = MessengerChannel.Signal });
        
        // Act
        persistence.UpdateSubscriber(new Subscriber
        {
            Id = "42",
            Channel = MessengerChannel.Signal,
            Subscriptions = new List<Subscription> { new Subscription { Title = "Signal only", QueryUrl = new Uri("https://example.com") } }
        });
        
        // Assert
        var subscribers = persistence.GetSubscribers();
        subscribers.Single(s => s.Channel == MessengerChannel.Telegram).Subscriptions.Should().BeEmpty();
        subscribers.Single(s => s.Channel == MessengerChannel.Signal).Subscriptions.Should().ContainSingle();
    }
    
    [Fact]
    public void RestoreData_ShouldNotMergeSubscribersOfDifferentChannels()
    {
        // Arrange
        _fakeStorage.SubscribersToLoad = new ConcurrentBag<Subscriber>
        {
            new Subscriber { Id = "42", Channel = MessengerChannel.Telegram },
            new Subscriber { Id = "42", Channel = MessengerChannel.Signal }
        };
        
        // Act
        var persistence = CreatePersistence();
        
        // Assert
        persistence.GetSubscribers().Should().HaveCount(2);
    }
    
    [Fact]
    public void RestoreData_ShouldStartEmptyWhenTheSubscriberFileDoesNotExist()
    {
        // Arrange
        _fakeStorage.LoadException = new FileNotFoundException("Subscribers.json");

        // Act
        var persistence = CreatePersistence();

        // Assert
        persistence.GetSubscribers().Should().BeEmpty();
    }
    
    [Fact]
    public void RestoreData_ShouldFailInsteadOfStartingEmptyWhenTheSubscriberFileCannotBeLoaded()
    {
        // Arrange
        _fakeStorage.LoadException = new InvalidDataException("Unexpected character");

        // Act
        var act = CreatePersistence;

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Subscribers.json*cannot be loaded*")
            .WithInnerException<InvalidDataException>();
    }
    
    // Fake implementation for testing without real I/O
    private class FakeDataStorage : IDataStorage
    {
        public ConcurrentBag<Subscriber> SubscribersToLoad { get; set; }
        public Exception LoadException { get; set; }
        
        public void Save<T>(T data, string fileName)
        {
            if (data is IEnumerable<Subscriber> subscribers)
            {
                SubscribersToLoad = new ConcurrentBag<Subscriber>(subscribers);
            }
        }
        
        public void Load<T>(string fileName, out T data)
        {
            if (LoadException != null)
            {
                throw LoadException;
            }
            
            if (typeof(T) == typeof(ConcurrentBag<Subscriber>))
            {
                data = (T)(object)SubscribersToLoad;
                return;
            }
            
            data = default;
        }
    }
}
