using EbayKleinanzeigenCrawler.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EbayKleinanzeigenCrawler.ErrorHandling
{
    public enum ErrorType
    {
        HttpRequest,
        ParseTitle,
        ParseDate,
        ParseDescription,
        ParseLink,
        ParsePrice
    }

    public class ErrorEntry
    {
        public DateTime Timstamp { get; init; }
        public ErrorType ErrorType { get; init; }
    }

    public class ErrorStatistics : IErrorStatistics
    {
        private static readonly object _lock = new();
        private List<ErrorEntry> _errors = new();
        private readonly ILogger _logger;
        private readonly IOutgoingNotifications _notificationService;
        private readonly TimeSpan _maxAge = TimeSpan.FromHours(1);
        private readonly uint _notificationThreshold = 2;

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
                    Timstamp = DateTime.Now,
                    ErrorType = errorType
                });

                var latestErrors = _errors
                    .Where(e => e.Timstamp > DateTime.Now + _maxAge)
                    .ToList();
                _errors = latestErrors;

                NotifyOnThreshold(latestErrors);
            }
        }

        private void NotifyOnThreshold(List<ErrorEntry> latestErrors)
        {
            if (latestErrors.Count >= _notificationThreshold)
            {
                var lines = new List<string>
                    { $"There were {latestErrors.Count} errors in the last {_maxAge.TotalMinutes}:" }
                    .Concat(
                        Enum.GetValues(typeof(ErrorType)).Cast<ErrorType>()
                        .Select(enumValue => $"{enumValue}: {latestErrors.Count(e => e.ErrorType == enumValue)}")
                    );

                var message = string.Join("\n", lines);
                _logger.Information("Sending Admin notification: " + message);
                _notificationService.NotifyAdmins(message);
            }
        }
    }
}
