using System;
using System.Collections.Concurrent;
using System.Threading;
using Serilog;

namespace EbayKleinanzeigenCrawler.Query;

public class QueryCounter
{
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<DateTime> _queue = new();
    private static readonly object _lockObject = new();
    private readonly Random _random = new();

    public QueryCounter(ILogger logger)
    {
        _logger = logger;
    }

    public bool WaitForAcquiringPermissionForQuery(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan, bool acquire)
    {
        // Randomize the time a little between the requests to hopefully counteract bot detection
        Thread.Sleep(TimeSpan.FromMilliseconds(_random.Next(0, 1000)));

        var queryWaitTimeout = TimeSpan.FromMinutes(10);
        var sleepTime = TimeSpan.FromSeconds(10);
        var startTime = DateTime.Now;
        _logger.Debug("Awaiting query permission");
        
        var hadToWait = false;
        while (!AcquirePermissionForQuery(allowedRequestsPerTimespan, timeToWaitBetweenMaxAmountOfRequests, acquire))
        {
            hadToWait = true;
            Thread.Sleep(sleepTime);
            if (DateTime.Now > startTime + queryWaitTimeout)
            {
                throw new Exception("Permission for query was not granted before the specified timeout");
            }
        }

        if (hadToWait)
        {
            // For regular breaks add more randomized pause to hopefully counteract bot detection
            Thread.Sleep(TimeSpan.FromSeconds(_random.Next(1, 60)));
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
                if (!acquire)
                {
                    return true;
                }

                _logger.Debug($"Allowed query because only {_queue.Count} queries were made within the last {timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                _queue.Enqueue(DateTime.Now);
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
}