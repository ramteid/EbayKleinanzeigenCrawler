using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using Moq;
using System;
using System.IO;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

public class CompositeNotifierTests
{
    private readonly Subscription _subscription = new() { Title = "Test", QueryUrl = new Uri("https://example.com") };
    private readonly Result _result = new() { Link = new Uri("https://example.com/ad/1") };

    private static Mock<IMessenger> CreateMessenger(MessengerChannel channel, DeliveryReport report = null)
    {
        var messenger = new Mock<IMessenger>();
        messenger.SetupGet(m => m.Channel).Returns(channel);
        messenger.Setup(m => m.NotifyAsync(It.IsAny<Subscription>(), It.IsAny<Result>()))
            .ReturnsAsync(report ?? DeliveryReport.NoRecipients);
        return messenger;
    }

    private static DeliveryReport DeliveredTo(int recipients) => new(recipients, Array.Empty<Exception>());

    private static DeliveryReport Unreachable() => new(0, new Exception[] { new IOException("Connection refused") });

    [Fact]
    public async Task NotifySubscribers_ShouldAskAllMessengersToNotify()
    {
        // Arrange
        var telegram = CreateMessenger(MessengerChannel.Telegram);
        var signal = CreateMessenger(MessengerChannel.Signal);
        var notifier = new CompositeNotifier(new[] { telegram.Object, signal.Object });

        // Act
        await notifier.NotifySubscribers(_subscription, _result);

        // Assert
        telegram.Verify(m => m.NotifyAsync(_subscription, _result), Times.Once);
        signal.Verify(m => m.NotifyAsync(_subscription, _result), Times.Once);
    }

    [Fact]
    public async Task NotifySubscribers_ShouldCompleteWithoutMessengers()
    {
        // Arrange
        var notifier = new CompositeNotifier(Array.Empty<IMessenger>());

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySubscribers_ShouldCompleteWhenNobodyHadToBeNotified()
    {
        // Arrange
        var notifier = new CompositeNotifier(new[]
        {
            CreateMessenger(MessengerChannel.Telegram).Object,
            CreateMessenger(MessengerChannel.Signal).Object
        });

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySubscribers_ShouldCompleteWhenTheRecipientsWereReached()
    {
        // Arrange
        var notifier = new CompositeNotifier(new[]
        {
            CreateMessenger(MessengerChannel.Telegram, DeliveredTo(1)).Object,
            CreateMessenger(MessengerChannel.Signal).Object
        });

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySubscribers_ShouldThrowWhenNobodyWasReachedBecauseOfTransientFailures()
    {
        // Arrange
        var notifier = new CompositeNotifier(new[]
        {
            CreateMessenger(MessengerChannel.Telegram).Object,
            CreateMessenger(MessengerChannel.Signal, Unreachable()).Object
        });

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        var exception = (await act.Should().ThrowAsync<AggregateException>()).Which;
        exception.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<IOException>();
    }

    [Fact]
    public async Task NotifySubscribers_ShouldNotThrowAfterAPartialDeliveryBecauseARetryWouldNotifyTwice()
    {
        // Arrange
        var notifier = new CompositeNotifier(new[]
        {
            CreateMessenger(MessengerChannel.Telegram, DeliveredTo(1)).Object,
            CreateMessenger(MessengerChannel.Signal, Unreachable()).Object
        });

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySubscribers_ShouldNotifyTheOtherMessengersWhenOneThrows()
    {
        // Arrange
        var failing = CreateMessenger(MessengerChannel.Signal);
        failing.Setup(m => m.NotifyAsync(It.IsAny<Subscription>(), It.IsAny<Result>()))
            .ThrowsAsync(new InvalidOperationException("bug"));
        var working = CreateMessenger(MessengerChannel.Telegram, DeliveredTo(1));
        var notifier = new CompositeNotifier(new[] { failing.Object, working.Object });

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        await act.Should().NotThrowAsync();
        working.Verify(m => m.NotifyAsync(_subscription, _result), Times.Once);
    }

    [Fact]
    public async Task NotifySubscribers_ShouldTreatAnUnexpectedExceptionAsAFailureWhenNobodyWasReached()
    {
        // Arrange
        var failing = CreateMessenger(MessengerChannel.Signal);
        failing.Setup(m => m.NotifyAsync(It.IsAny<Subscription>(), It.IsAny<Result>()))
            .Throws(new InvalidOperationException("thrown before the first await"));
        var notifier = new CompositeNotifier(new[] { failing.Object, CreateMessenger(MessengerChannel.Telegram).Object });

        // Act
        var act = () => notifier.NotifySubscribers(_subscription, _result);

        // Assert
        var exception = (await act.Should().ThrowAsync<AggregateException>()).Which;
        exception.InnerExceptions.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();
    }
}
