using EbayKleinanzeigenCrawler.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EbayKleinanzeigenCrawler.ErrorHandling
{
    public class ErrorStatistics : IErrorStatistics
    {
        private readonly TimeSpan _maxAge = TimeSpan.FromHours(1);
        private readonly uint _notificationThreshold = 2;

        private static readonly object _lock = new();
        private List<ErrorEntry> _errors = new();
        private readonly ILogger _logger;
        private readonly IOutgoingNotifications _notificationService;

        public ErrorStatistics(ILogger logger, IOutgoingNotifications notificationService)
        {
            _logger = logger;
            _notificationService = notificationService;
        }

        public void AmendErrorStatistic(ErrorType errorType)
        {
            lock (_lock)
            {
                _errors.Add(new ErrorEntry
                {
                    Timestamp = DateTime.Now,
                    ErrorType = errorType
                });
            }
        }

        public void NotifyOnThreshold()
        {
            lock (_lock)
            {
                _errors = _errors
                    .Where(e => e.Timestamp > DateTime.Now - _maxAge)
                    .ToList();

                if (_errors.Count >= _notificationThreshold)
                {
                    var lines = new List<string>
                        { $"There were {_errors.Count} errors in the last {_maxAge.TotalMinutes} minutes:" }
                        .Concat(
                            Enum.GetValues(typeof(ErrorType)).Cast<ErrorType>()
                            .Select(enumValue => $"{enumValue}: {_errors.Count(e => e.ErrorType == enumValue)}")
                        );

                    var message = string.Join("\n", lines);
                    _logger.Information("Sending Admin notification: " + message);
                    _notificationService.NotifyAdmins(message);
                    _errors.Clear();
                }
            }
        }
    }
}
