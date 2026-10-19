using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;

namespace KleinanzeigenCrawler.Query;

public class QueryCounter
{
    private static readonly TimeSpan QueryWaitTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SleepTime = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    // UTC times of the recent queries. Local time is not used because it jumps when daylight saving time starts or ends.
    private readonly Queue<DateTime> _queue = new();
    private readonly object _lockObject = new();

    public QueryCounter(ILogger logger)
    {
        _logger = logger;
    }

    public async Task WaitForAcquiringPermissionForQueryAsync(TimeSpan timeToWaitBetweenMaxAmountOfRequests, uint allowedRequestsPerTimespan)
    {
        // Randomize the time a little between the requests to hopefully counteract bot detection
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(500, 3000)));

        var startTime = DateTime.UtcNow;
        _logger.Debug("Awaiting query permission");
        
        var hadToWait = false;
        while (!AcquirePermissionForQuery(allowedRequestsPerTimespan, timeToWaitBetweenMaxAmountOfRequests))
        {
            hadToWait = true;
            await Task.Delay(SleepTime);
            if (DateTime.UtcNow > startTime + QueryWaitTimeout)
            {
                throw new TimeoutException("Permission for query was not granted before the specified timeout");
            }
        }

        if (hadToWait)
        {
            // For regular breaks add more randomized pause to hopefully counteract bot detection
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 60)));
        }
    }

    private bool AcquirePermissionForQuery(uint allowedRequestsPerTimespan, TimeSpan timeToWaitBetweenMaxAmountOfRequests)
    {
        lock (_lockObject)
        {
            // Remove all entries which are older than 5 minutes
            var now = DateTime.UtcNow;
            while (_queue.TryPeek(out var oldest) && oldest < now - timeToWaitBetweenMaxAmountOfRequests)
            {
                _queue.Dequeue();
            }

            // If we have made less than 40 calls within the last 5 minutes, we can make another call
            if (_queue.Count < allowedRequestsPerTimespan)
            {
                _logger.Debug($"Allowed query because only {_queue.Count} queries were made within the last {timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                _queue.Enqueue(now);
                return true;
            }

            _logger.Debug($"Disallowed query because {_queue.Count} queries were made within the last {timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
            return false;
        }
    }
}
