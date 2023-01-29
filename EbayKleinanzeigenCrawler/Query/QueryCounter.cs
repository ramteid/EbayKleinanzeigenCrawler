using System;
using System.Collections.Concurrent;
using Serilog;

namespace KleinanzeigenCrawler.Query
{
    public class QueryCounter
    {
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<DateTime> _queue = new ConcurrentQueue<DateTime>();
        private readonly object _lockObject = new object();
        public readonly TimeSpan TimeSpanMinutes = TimeSpan.FromMinutes(5.1);
        private const uint AllowedRequestsPerTimespan = 40;

        public QueryCounter(ILogger logger)
        {
            _logger = logger;
        }

        public bool AcquirePermissionForQuery()
        {
            lock (_lockObject)
            {
                // Remove all entries which are older than 5 minutes
                while (IsOldestTooOld())
                {
                    _queue.TryDequeue(out DateTime _);
                }

                // Now the queue only consists of entries made less than 5 min ago
                int entriesCount = _queue.Count;

                // If we have made less than 40 calls within the last 5 minutes, we can make another call
                if (entriesCount < AllowedRequestsPerTimespan)
                {
                    _logger.Debug($"Allowed query because only {_queue.Count} queries were made within the last {TimeSpanMinutes.TotalMinutes} minutes");
                    _queue.Enqueue(DateTime.Now);
                    return true;
                }

                _logger.Debug($"Disallowed query because {_queue.Count} queries were made within the last {TimeSpanMinutes.TotalMinutes} minutes");
                return false;
            }
        }

        private bool IsOldestTooOld()
        {
            bool queueHasElements = _queue.TryPeek(out DateTime oldest);
            bool tooOld = oldest < DateTime.Now.Subtract(TimeSpanMinutes);
            return queueHasElements && tooOld;
        }
    }
}
