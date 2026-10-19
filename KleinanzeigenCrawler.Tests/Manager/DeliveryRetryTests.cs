using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Manager;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

public class DeliveryRetryTests
{
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    [Fact]
    public async Task ExecuteAsync_ShouldRunTheActionOnceWhenItSucceeds()
    {
        // Arrange
        var calls = 0;

        // Act
        await DeliveryRetry.ExecuteAsync(() => { calls++; return Task.CompletedTask; }, (_, _) => NoDelay, 3, (_, _, _) => { });

        // Assert
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRetryTransientFailuresUntilTheActionSucceeds()
    {
        // Arrange
        var calls = 0;
        var retries = new List<(string Message, int Attempt)>();

        // Act
        await DeliveryRetry.ExecuteAsync(
            () => ++calls < 3 ? Task.FromException(new InvalidOperationException($"failure {calls}")) : Task.CompletedTask,
            (_, _) => NoDelay,
            3,
            (ex, _, attempt) => retries.Add((ex.Message, attempt)));

        // Assert
        calls.Should().Be(3);
        retries.Should().Equal(("failure 1", 1), ("failure 2", 2));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRethrowTheLastFailureWhenAllAttemptsFail()
    {
        // Arrange
        var calls = 0;

        // Act
        var act = () => DeliveryRetry.ExecuteAsync(
            () => Task.FromException(new InvalidOperationException($"failure {++calls}")),
            (_, _) => NoDelay,
            3,
            (_, _, _) => { });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("failure 3");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRetryWhenTheSelectorReturnsNoDelay()
    {
        // Arrange
        var calls = 0;

        // Act
        var act = () => DeliveryRetry.ExecuteAsync(
            () => { calls++; return Task.FromException(new InvalidOperationException("permanent")); },
            (_, _) => null,
            3,
            (_, _, _) => { });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("permanent");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassTheAttemptNumberAndWaitForTheSelectedDelay()
    {
        // Arrange
        var attempts = new List<int>();
        var calls = 0;

        // Act
        var started = DateTime.UtcNow;
        await DeliveryRetry.ExecuteAsync(
            () => ++calls < 2 ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask,
            (_, attempt) => { attempts.Add(attempt); return TimeSpan.FromMilliseconds(150); },
            3,
            (_, _, _) => { });

        // Assert
        attempts.Should().Equal(1);
        (DateTime.UtcNow - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRetryOnceCancellationWasRequested()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        // Act
        var act = () => DeliveryRetry.ExecuteAsync(
            () => { calls++; cancellation.Cancel(); return Task.FromException(new InvalidOperationException("stopped")); },
            (_, _) => NoDelay,
            3,
            (_, _, _) => { },
            cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stopped");
        calls.Should().Be(1);
    }
}
