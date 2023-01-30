using System;
using System.Collections.Concurrent;
using Serilog;
using Serilog.Events;

namespace KleinanzeigenCrawler.Query
{
    public class QueryCounter
    {
        /// <summary>
        /// This interval is used by EbayKleinanzeigen. They only allow 40 queries every 5 minutes. Above that, they obfuscate their HTML.
        /// To make sure not to exceed this limit, it is hard-coded here.
        /// </summary>
        public readonly TimeSpan TimeToWaitBetweenMaxAmountOfRequests = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10);
        private const uint AllowedRequestsPerTimespan = 40;

        private readonly ILogger _logger;
        private readonly ConcurrentQueue<DateTime> _queue = new();
        private readonly object _lockObject = new();

        public QueryCounter(ILogger logger)
        {
            _logger = logger;
        }

        public bool AcquirePermissionForQuery(LogEventLevel logEventLevel = LogEventLevel.Debug)
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
                    _logger.Write(logEventLevel, $"Allowed query because only {_queue.Count} queries were made within the last {TimeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                    _queue.Enqueue(DateTime.Now);
                    return true;
                }

                _logger.Write(logEventLevel, $"Disallowed query because {_queue.Count} queries were made within the last {TimeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                return false;
            }
        }

        private bool IsOldestTooOld()
        {
            bool queueHasElements = _queue.TryPeek(out DateTime oldest);
            bool tooOld = oldest < DateTime.Now.Subtract(TimeToWaitBetweenMaxAmountOfRequests);
            return queueHasElements && tooOld;
        }
    }
}
