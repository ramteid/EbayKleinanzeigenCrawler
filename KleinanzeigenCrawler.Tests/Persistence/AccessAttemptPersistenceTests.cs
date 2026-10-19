using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Persistence;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using System;
using System.IO;

namespace KleinanzeigenCrawler.Tests.Persistence;

/// <summary>
/// Tests for the persisted access attempts using real file storage
/// </summary>
public class AccessAttemptPersistenceTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"kc-access-{Guid.NewGuid():N}");
    private readonly Mock<ILogger> _mockLogger = new();

    public AccessAttemptPersistenceTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_dataDirectory, recursive: true);
    }

    private string FilePath => Path.Combine(_dataDirectory, "AccessAttempts.json");

    private AccessAttemptPersistence CreatePersistence(IDataStorage storage = null) => new(
        _mockLogger.Object,
        storage ?? new JsonStorage(_mockLogger.Object),
        new AppConfiguration { Crawler = { DataPath = _dataDirectory } });

    [Fact]
    public void RestoreData_ShouldStartEmptyWhenTheFileDoesNotExist()
    {
        // Act
        var persistence = CreatePersistence();

        // Assert
        persistence.GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().BeNull();
        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void SetFailedAttempts_ShouldSurviveARestartPerChannelAndUser()
    {
        // Arrange
        var persistence = CreatePersistence();
        persistence.SetFailedAttempts(MessengerChannel.Signal, "user-1", 10);
        persistence.SetFailedAttempts(MessengerChannel.Telegram, "user-1", 2);
        persistence.SetFailedAttempts(MessengerChannel.Signal, "user-2", 0);

        // Act
        var restored = CreatePersistence();

        // Assert
        restored.GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().Be(10);
        restored.GetFailedAttempts(MessengerChannel.Telegram, "user-1").Should().Be(2);
        restored.GetFailedAttempts(MessengerChannel.Signal, "user-2").Should().Be(0);
        restored.GetFailedAttempts(MessengerChannel.Telegram, "user-2").Should().BeNull();
        File.ReadAllText(FilePath).Should().Contain("\"Channel\": \"Signal\"").And.Contain("\"FailedAttempts\": 10");
    }

    [Fact]
    public void SetFailedAttempts_ShouldOverwriteTheCountOfAUser()
    {
        // Arrange
        var persistence = CreatePersistence();
        persistence.SetFailedAttempts(MessengerChannel.Signal, "user-1", 1);

        // Act
        persistence.SetFailedAttempts(MessengerChannel.Signal, "user-1", 2);

        // Assert
        CreatePersistence().GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().Be(2);
    }

    [Fact]
    public void Remove_ShouldForgetTheUserAlsoAfterARestart()
    {
        // Arrange
        var persistence = CreatePersistence();
        persistence.SetFailedAttempts(MessengerChannel.Signal, "user-1", 3);
        persistence.SetFailedAttempts(MessengerChannel.Signal, "user-2", 4);

        // Act
        persistence.Remove(MessengerChannel.Signal, "user-1");

        // Assert
        persistence.GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().BeNull();
        var restored = CreatePersistence();
        restored.GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().BeNull();
        restored.GetFailedAttempts(MessengerChannel.Signal, "user-2").Should().Be(4);
    }

    [Fact]
    public void Remove_ShouldNotCreateAFileForAnUnknownUser()
    {
        // Arrange
        var persistence = CreatePersistence();

        // Act
        persistence.Remove(MessengerChannel.Signal, "unknown");

        // Assert
        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void RestoreData_ShouldIgnoreEntriesWithoutId()
    {
        // Arrange
        File.WriteAllText(FilePath, """[{"Channel": "Signal", "FailedAttempts": 3}, {"Channel": "Signal", "Id": "user-1", "FailedAttempts": 5}]""");

        // Act
        var persistence = CreatePersistence();

        // Assert
        persistence.GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().Be(5);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""[{"Channel": "Fax", "Id": "user-1", "FailedAttempts": 10}]""")]
    [InlineData("""[{"Channel": "Signal", "Id": "user-1", "FailedAttempts": "many"}]""")]
    public void RestoreData_ShouldFailAndLeaveAnUnreadableFileUntouched(string content)
    {
        // Arrange
        File.WriteAllText(FilePath, content);

        // Act
        var act = () => CreatePersistence();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*AccessAttempts.json*cannot be loaded*");
        File.ReadAllText(FilePath).Should().Be(content);
    }

    [Fact]
    public void SetFailedAttempts_ShouldKeepTheCountInMemoryWhenTheFileCannotBeWritten()
    {
        // Arrange
        var persistence = CreatePersistence(new UnwritableStorage());

        // Act
        var act = () => persistence.SetFailedAttempts(MessengerChannel.Signal, "user-1", 4);

        // Assert
        act.Should().Throw<IOException>();
        persistence.GetFailedAttempts(MessengerChannel.Signal, "user-1").Should().Be(4);
    }

    [Fact]
    public void AccessGate_ShouldKeepABlockAcrossARestartOfTheApplication()
    {
        // Arrange
        var subscribers = new InMemorySubscriptionPersistence();
        AccessGate CreateGate() => new(
            new SubscriptionService(subscribers, Mock.Of<IParserProvider>(), _mockLogger.Object, MessengerChannel.Signal),
            CreatePersistence(),
            new MessagingConfiguration { AccessPassword = "correct-horse" },
            _mockLogger.Object);
        var gate = CreateGate();
        for (var i = 0; i <= AccessGate.MaxFailedAttempts; i++)
        {
            gate.Admit("user-1", "wrong");
        }

        // Act
        var restartedGate = CreateGate();
        var admission = restartedGate.Admit("user-1", "correct-horse");

        // Assert
        admission.Status.Should().Be(AdmissionStatus.Ignored);
        subscribers.GetSubscribers().Should().BeEmpty();
    }

    private sealed class UnwritableStorage : IDataStorage
    {
        public void Save<T>(T data, string fileName) => throw new IOException("disk full");

        public void Load<T>(string fileName, out T data) => throw new FileNotFoundException(fileName);
    }
}
