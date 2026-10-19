using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Persistence;
using Moq;
using Serilog;
using System;
using System.IO;
using System.Linq;

namespace KleinanzeigenCrawler.Tests.Persistence;

/// <summary>
/// Tests for the persisted subscriber file format using real file storage
/// </summary>
public class SubscriptionPersistenceStorageTests : IDisposable
{
    private const string LegacySubscribersJson = """
        [
          {
            "Id": "123456789",
            "Subscriptions": [
              {
                "Id": "0b0f3c1e-0000-4000-8000-000000000001",
                "Title": "Legacy",
                "QueryUrl": "https://www.kleinanzeigen.de/s-test/k0",
                "IncludeKeywords": [],
                "ExcludeKeywords": [],
                "InitialPull": false,
                "Enabled": true,
                "FirstRunCompleted": true
              }
            ]
          }
        ]
        """;

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"kc-persistence-{Guid.NewGuid():N}");
    private readonly Mock<ILogger> _mockLogger = new();

    public SubscriptionPersistenceStorageTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_dataDirectory, recursive: true);
    }

    private SubscriptionPersistence CreatePersistence() => new(
        _mockLogger.Object,
        new JsonStorage(_mockLogger.Object),
        new AppConfiguration { Crawler = { DataPath = _dataDirectory } });

    [Fact]
    public void RestoreData_ShouldLoadSubscribersWithoutChannelAsTelegramSubscribers()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_dataDirectory, "Subscribers.json"), LegacySubscribersJson);

        // Act
        var subscribers = CreatePersistence().GetSubscribers();

        // Assert
        var subscriber = subscribers.Should().ContainSingle().Subject;
        subscriber.Id.Should().Be("123456789");
        subscriber.Channel.Should().Be(MessengerChannel.Telegram);
        subscriber.Subscriptions.Should().ContainSingle(s => s.Title == "Legacy");
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[{\"Id\": \"1\", \"Subscriptions\": [{\"Id\": \"not-a-guid\"}]}]")]
    public void RestoreData_ShouldFailAndLeaveAnUnreadableFileUntouched(string content)
    {
        // Arrange
        var path = Path.Combine(_dataDirectory, "Subscribers.json");
        File.WriteAllText(path, content);

        // Act
        var act = CreatePersistence;

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Subscribers.json*cannot be loaded*");
        File.ReadAllText(path).Should().Be(content);
    }

    [Fact]
    public void Persistence_ShouldRoundTripTheChannelAsText()
    {
        // Arrange
        var persistence = CreatePersistence();
        persistence.AddSubscriber(new Subscriber { Id = "signal-account", Channel = MessengerChannel.Signal });
        persistence.AddSubscriber(new Subscriber { Id = "123456789", Channel = MessengerChannel.Telegram });

        // Act
        var restored = CreatePersistence().GetSubscribers();

        // Assert
        File.ReadAllText(Path.Combine(_dataDirectory, "Subscribers.json")).Should().Contain("\"Channel\": \"Signal\"");
        restored.Select(s => (s.Channel, s.Id)).Should().BeEquivalentTo(new[]
        {
            (MessengerChannel.Signal, "signal-account"),
            (MessengerChannel.Telegram, "123456789")
        });
    }
}
