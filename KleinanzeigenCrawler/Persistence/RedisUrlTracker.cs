using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Monitoring;
using Serilog;
using StackExchange.Redis;

namespace KleinanzeigenCrawler.Persistence;

/// <summary>
/// Redis-based URL tracking with space-efficient hashing and 90-day TTL
/// </summary>
public class RedisUrlTracker : IUrlTracker
{
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(90);
    private const string KeyPrefix = "url:";

    private readonly ILogger _logger;
    private readonly IDatabase _db;
    private readonly MetricsService _metricsService;
    
    public RedisUrlTracker(ILogger logger, string connectionString, MetricsService metricsService = null)
    {
        _logger = logger;
        _metricsService = metricsService;
        
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false; // Resilient connection
        options.ConnectRetry = 5;
        options.ConnectTimeout = 5000;
        options.SyncTimeout = 5000;
        
        _db = ConnectionMultiplexer.Connect(options).GetDatabase();
        
        _logger.Information("Redis URL tracker initialized");
    }
    
    public void VerifyConnection()
    {
        _db.Ping();
        _logger.Information("Redis connection verified - data persists in Redis");
    }
    
    public Task<bool> RefreshIfProcessedAsync(Guid subscriptionId, Uri url)
    {
        // Redis only sets an expiry on existing keys, so this checks and refreshes in one round trip
        return ExecuteAsync("RefreshIfProcessed", subscriptionId, () => _db.KeyExpireAsync(GetKey(subscriptionId, url), Expiry));
    }
    
    public Task MarkProcessedAsync(Guid subscriptionId, Uri url)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return ExecuteAsync("MarkProcessed", subscriptionId, () => _db.StringSetAsync(GetKey(subscriptionId, url), timestamp, Expiry));
    }
    
    private async Task<T> ExecuteAsync<T>(string operation, Guid subscriptionId, Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            _metricsService?.RecordRedisOperation(operation, true);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Redis operation {Operation} failed for subscription {SubscriptionId}", operation, subscriptionId);
            _metricsService?.RecordRedisOperation(operation, false);
            _metricsService?.RecordException(nameof(RedisUrlTracker), ex.GetType().Name);
            throw;
        }
    }
    
    private static string GetKey(Guid subscriptionId, Uri url) => $"{KeyPrefix}{subscriptionId}:{ComputeUrlHash(url.ToString())}";
    
    /// <summary>
    /// Compute SHA256 hash of URL for space-efficient storage
    /// </summary>
    private static string ComputeUrlHash(string url)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToBase64String(hashBytes).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }
}
