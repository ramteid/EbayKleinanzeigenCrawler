using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace KleinanzeigenCrawler.Query;

public class QueryCounter : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<DateTime> _queue = new();
    private static readonly object _lockObject = new();
    private readonly ThreadLocal<Random> _random = new(() => new Random(Guid.NewGuid().GetHashCode()));
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);

    public QueryCounter(ILogger logger)
    {
        _logger = logger;
        
        // Start periodic cleanup timer to prevent memory accumulation
        _cleanupTimer = new Timer(
            callback: _ => PeriodicCleanup(),
            state: null,
            dueTime: _cleanupInterval,
            period: _cleanupInterval
        );
    }
    
    /// <summary>
    /// Periodically removes old entries from the queue to prevent unbounded growth
    /// </summary>
    private void PeriodicCleanup()
    {
        lock (_lockObject)
        {
            var sizeBefore = _queue.Count;
            
            // Remove entries older than 1 hour (well beyond the typical 5-minute window)
            var cutoffTime = DateTime.Now.Subtract(TimeSpan.FromHours(1));
            
            while (_queue.TryPeek(out var oldest) && oldest < cutoffTime)
            {
                _queue.TryDequeue(out _);
            }
            
            var sizeAfter = _queue.Count;
            if (sizeBefore != sizeAfter)
            {
                _logger.Debug($"Periodic cleanup: removed {sizeBefore - sizeAfter} old query entries");
            }
        }
    }

    public async Task<bool> WaitForAcquiringPermissionForQueryAsync(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan, bool acquire)
    {
        // Randomize the time a little between the requests to hopefully counteract bot detection
        await Task.Delay(TimeSpan.FromMilliseconds(_random.Value.Next(500, 3000)));

        var queryWaitTimeout = TimeSpan.FromMinutes(10);
        var sleepTime = TimeSpan.FromSeconds(10);
        var startTime = DateTime.Now;
        _logger.Debug("Awaiting query permission");
        
        var hadToWait = false;
        while (!AcquirePermissionForQuery(allowedRequestsPerTimespan, timeToWaitBetweenMaxAmountOfRequests, acquire))
        {
            hadToWait = true;
            await Task.Delay(sleepTime);
            if (DateTime.Now > startTime + queryWaitTimeout)
            {
                throw new Exception("Permission for query was not granted before the specified timeout");
            }
        }

        if (hadToWait)
        {
            // For regular breaks add more randomized pause to hopefully counteract bot detection
            await Task.Delay(TimeSpan.FromSeconds(_random.Value.Next(1, 60)));
        }

        return true;
    }

    private bool AcquirePermissionForQuery(uint allowedRequestsPerTimespan, TimeSpan timeToWaitBetweenMaxAmountOfRequests, bool acquire)
    {
        lock (_lockObject)
        {
            // Remove all entries which are older than 5 minutes
            while (IsOldestTooOld(timeToWaitBetweenMaxAmountOfRequests))
            {
                _queue.TryDequeue(out _);
            }

            // Now the queue only consists of entries made less than 5 min ago
            var entriesCount = _queue.Count;

            // If we have made less than 40 calls within the last 5 minutes, we can make another call
            if (entriesCount < allowedRequestsPerTimespan)
            {
                // Only add to queue if actually acquiring, not just peeking
                if (acquire)
                {
                    _logger.Debug($"Allowed query because only {_queue.Count} queries were made within the last {timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                    _queue.Enqueue(DateTime.Now);
                }
                else
                {
                    _logger.Debug($"Query would be allowed because only {_queue.Count} queries were made within the last {timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                }
                return true;
            }

            if (acquire)
            {
                _logger.Debug($"Disallowed query because {_queue.Count} queries were made within the last {timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
            }
            return false;
        }
    }

    private bool IsOldestTooOld(TimeSpan timeToWaitBetweenMaxAmountOfRequests)
    {
        var queueHasElements = _queue.TryPeek(out var oldest);
        var tooOld = oldest < DateTime.Now.Subtract(timeToWaitBetweenMaxAmountOfRequests);
        return queueHasElements && tooOld;
    }
    
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _random?.Dispose();
    }
}