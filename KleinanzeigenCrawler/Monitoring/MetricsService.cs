using Prometheus;
using System;

namespace KleinanzeigenCrawler.Monitoring;

/// <summary>
/// Provides Prometheus metrics for monitoring application health and performance
/// </summary>
public class MetricsService
{
    // Counters
    private static readonly Counter HttpRequestsTotal = Metrics.CreateCounter(
        "crawler_http_requests_total",
        "Total number of HTTP requests made",
        new CounterConfiguration
        {
            LabelNames = ["provider", "status_code", "success"]
        });
    
    private static readonly Counter NotificationsSentTotal = Metrics.CreateCounter(
        "crawler_notifications_sent_total",
        "Total number of notifications sent to subscribers",
        new CounterConfiguration
        {
            LabelNames = ["success"]
        });
    
    private static readonly Counter ParseErrorsTotal = Metrics.CreateCounter(
        "crawler_parse_errors_total",
        "Total number of parsing errors",
        new CounterConfiguration
        {
            LabelNames = ["provider", "error_type"]
        });
    
    private static readonly Counter ExceptionsTotal = Metrics.CreateCounter(
        "crawler_exceptions_total",
        "Total number of unhandled exceptions",
        new CounterConfiguration
        {
            LabelNames = ["component", "exception_type"]
        });
    
    private static readonly Counter RedisOperationsTotal = Metrics.CreateCounter(
        "crawler_redis_operations_total",
        "Total number of Redis operations",
        new CounterConfiguration
        {
            LabelNames = ["operation", "success"]
        });
    
    private static readonly Counter SubscriptionsProcessedTotal = Metrics.CreateCounter(
        "crawler_subscriptions_processed_total",
        "Total number of subscriptions processed",
        new CounterConfiguration
        {
            LabelNames = ["provider", "success"]
        });
    
    // Gauges
    private static readonly Gauge ActiveSubscriptions = Metrics.CreateGauge(
        "crawler_active_subscriptions",
        "Number of currently active (enabled) subscriptions");
    
    private static readonly Gauge ActiveSubscribers = Metrics.CreateGauge(
        "crawler_active_subscribers",
        "Number of currently active subscribers");
    
    // Histograms
    private static readonly Histogram RequestDuration = Metrics.CreateHistogram(
        "crawler_request_duration_seconds",
        "HTTP request duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = ["provider"],
            Buckets = [0.1, 0.5, 1, 2, 5, 10, 30]
        });
    
    // Summaries
    private static readonly Summary ResultsPerSubscription = Metrics.CreateSummary(
        "crawler_results_per_subscription",
        "Number of results found per subscription check",
        new SummaryConfiguration
        {
            Objectives =
            [
                new QuantileEpsilonPair(0.5, 0.05),
                new QuantileEpsilonPair(0.9, 0.01),
                new QuantileEpsilonPair(0.99, 0.001)
            ]
        });
    
    // Counter methods
    public void RecordHttpRequest(string provider, int statusCode, bool success)
    {
        HttpRequestsTotal.WithLabels(provider, statusCode.ToString(), success.ToString()).Inc();
    }
    
    public void RecordParseError(string provider, string errorType)
    {
        ParseErrorsTotal.WithLabels(provider, errorType).Inc();
    }
    
    public void RecordException(string component, string exceptionType)
    {
        ExceptionsTotal.WithLabels(component, exceptionType).Inc();
    }
    
    public void RecordRedisOperation(string operation, bool success)
    {
        RedisOperationsTotal.WithLabels(operation, success.ToString()).Inc();
    }
    
    public void RecordSubscriptionProcessed(string provider, bool success)
    {
        SubscriptionsProcessedTotal.WithLabels(provider, success.ToString()).Inc();
    }
    
    // Gauge methods
    public void UpdateActiveSubscriptions(int count)
    {
        ActiveSubscriptions.Set(count);
    }
    
    public void UpdateActiveSubscribers(int count)
    {
        ActiveSubscribers.Set(count);
    }
    
    // Histogram methods
    public IDisposable MeasureRequestDuration(string provider)
    {
        return RequestDuration.WithLabels(provider).NewTimer();
    }
    
    // Summary methods
    public void ObserveResultsPerSubscription(double count)
    {
        ResultsPerSubscription.Observe(count);
    }
}
