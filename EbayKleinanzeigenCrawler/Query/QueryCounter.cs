using System;
using System.Collections.Concurrent;
using System.Threading;
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
        private readonly TimeSpan _timeToWaitBetweenMaxAmountOfRequests = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(10);
        private const uint _allowedRequestsPerTimespan = 40;

        private readonly ILogger _logger;
        private readonly ConcurrentQueue<DateTime> _queue = new();
        private readonly object _lockObject = new();

        public QueryCounter(ILogger logger)
        {
            _logger = logger;
        }

        public bool WaitForPermissionForQuery(LogEventLevel logEventLevel = LogEventLevel.Debug)
        {
            var queryWaitTimeout = TimeSpan.FromMinutes(10);
            var sleepTime = TimeSpan.FromSeconds(10);
            var startTime = DateTime.Now;
            _logger.Debug($"Awaiting query permission");
            while (!AcquirePermissionForQuery(logEventLevel))
            {
                Thread.Sleep(sleepTime);
                if (DateTime.Now > startTime + queryWaitTimeout)
                {
                    throw new Exception("Permission for query was not granted before the specified timeout");
                }
            }
            return true;
        }

        private bool AcquirePermissionForQuery(LogEventLevel logEventLevel = LogEventLevel.Debug)
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
                if (entriesCount < _allowedRequestsPerTimespan)
                {
                    _logger.Write(logEventLevel, $"Allowed query because only {_queue.Count} queries were made within the last {_timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                    _queue.Enqueue(DateTime.Now);
                    return true;
                }

                _logger.Write(logEventLevel, $"Disallowed query because {_queue.Count} queries were made within the last {_timeToWaitBetweenMaxAmountOfRequests.TotalMinutes} minutes");
                return false;
            }
        }

        private bool IsOldestTooOld()
        {
            bool queueHasElements = _queue.TryPeek(out DateTime oldest);
            bool tooOld = oldest < DateTime.Now.Subtract(_timeToWaitBetweenMaxAmountOfRequests);
            return queueHasElements && tooOld;
        }
    }
}
