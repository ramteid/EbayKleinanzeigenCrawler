using System;
using System.Threading;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Manager;

public static class DeliveryRetry
{
    /// <summary>
    /// Runs the action and repeats it while it fails with a transient error, up to the given number of attempts.
    /// The delay selector returns how long to wait before the next attempt, or null if the error must not be retried.
    /// The exception of the last attempt is rethrown.
    /// </summary>
    public static async Task ExecuteAsync(
        Func<Task> action,
        Func<Exception, int, TimeSpan?> selectRetryDelay,
        int maxAttempts,
        Action<Exception, TimeSpan, int> onRetry,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested && selectRetryDelay(ex, attempt) is { } delay)
            {
                onRetry(ex, delay, attempt);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
