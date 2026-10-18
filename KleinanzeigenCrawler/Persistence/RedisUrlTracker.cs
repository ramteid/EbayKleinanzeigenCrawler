using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Monitoring;
using Serilog;
using StackExchange.Redis;

namespace KleinanzeigenCrawler.Persistence;

/// <summary>
/// Redis-based URL tracking with space-efficient hashing and 90-day TTL
/// </summary>
public class RedisUrlTracker
{
    private readonly ILogger _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly MetricsService _metricsService;
    private const int ExpiryDays = 90;
    private const string KeyPrefix = "url:";
    private const string SubKeyPrefix = "sub:";
    
    public RedisUrlTracker(ILogger logger, string connectionString, MetricsService metricsService = null)
    {
        _logger = logger;
        _metricsService = metricsService;
        
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false; // Resilient connection
        options.ConnectRetry = 5;
        options.ConnectTimeout = 5000;
        options.SyncTimeout = 5000;
        
        _redis = ConnectionMultiplexer.Connect(options);
        _db = _redis.GetDatabase();
        
        _logger.Information("Redis URL tracker initialized");
    }
    
    public async Task AddOrUpdateAsync(Guid subscriptionId, List<AlreadyProcessedUrl> urls)
    {
        try
        {
            var batch = _db.CreateBatch();
            var tasks = new List<Task>();
            
            foreach (var url in urls)
            {
                var hash = ComputeUrlHash(url.Uri.ToString());
                var key = $"{KeyPrefix}{subscriptionId}:{hash}";
                var lastFoundTimestamp = new DateTimeOffset(url.LastFound).ToUnixTimeSeconds();
                
                // Store timestamp with 90-day expiry
                var setTask = batch.StringSetAsync(key, lastFoundTimestamp, TimeSpan.FromDays(ExpiryDays));
                tasks.Add(setTask);
            }
            
            batch.Execute();
            await Task.WhenAll(tasks);
            
            _metricsService?.RecordRedisOperation("AddOrUpdate", true);
            _logger.Verbose($"Updated {urls.Count} URLs for subscription {subscriptionId}");
        }
        catch (RedisException ex)
        {
            _logger.Error(ex, $"Redis error updating URLs for subscription {subscriptionId}");
            _metricsService?.RecordRedisOperation("AddOrUpdate", false);
            _metricsService?.RecordException("RedisUrlTracker", ex.GetType().Name);
            throw;
        }
    }
    
    public void RestoreData()
    {
        try
        {
            // Test connection
            _db.Ping();
            _logger.Information("Redis connection verified - data persists in Redis");
        }
        catch (RedisException ex)
        {
            _logger.Error(ex, "Failed to connect to Redis");
            throw;
        }
    }
    
    public void PersistData()
    {
        // Redis automatically persists with AOF/RDB
        // TTL-based expiry handles cleanup of old URLs (90 days)
        // We rely on Redis configuration for persistence to avoid performance impact of frequent BGSAVE calls
    }
    
    /// <summary>
    /// Check if a URL has been processed for a subscription
    /// </summary>
    public bool IsUrlProcessed(Guid subscriptionId, Uri url)
    {
        try
        {
            var hash = ComputeUrlHash(url.ToString());
            var key = $"{KeyPrefix}{subscriptionId}:{hash}";
            return _db.KeyExists(key);
        }
        catch (RedisException ex)
        {
            _logger.Error(ex, $"Redis error checking URL for subscription {subscriptionId}");
            return false;
        }
    }
    
    /// <summary>
    /// Update LastFound timestamp for an existing URL
    /// </summary>
    public void UpdateLastFound(Guid subscriptionId, Uri url)
    {
        try
        {
            var hash = ComputeUrlHash(url.ToString());
            var key = $"{KeyPrefix}{subscriptionId}:{hash}";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            
            _db.StringSet(key, timestamp, TimeSpan.FromDays(ExpiryDays));
            _logger.Verbose($"Updated LastFound for URL hash {hash}");
        }
        catch (RedisException ex)
        {
            _logger.Error(ex, $"Redis error updating LastFound for subscription {subscriptionId}");
        }
    }
    
    /// <summary>
    /// Get subscription statistics
    /// </summary>
    public int GetProcessedUrlCount(Guid subscriptionId)
    {
        try
        {
            var pattern = $"{KeyPrefix}{subscriptionId}:*";
            var endpoints = _redis.GetEndPoints();
            if (endpoints.Length == 0)
            {
                _logger.Warning("No Redis endpoints available");
                return 0;
            }
            
            var server = _redis.GetServer(endpoints[0]);
            return server.Keys(pattern: pattern).Count();
        }
        catch (RedisException ex)
        {
            _logger.Error(ex, $"Redis error counting URLs for subscription {subscriptionId}");
            return 0;
        }
    }
    
    /// <summary>
    /// Compute SHA256 hash of URL for space-efficient storage
    /// </summary>
    private static string ComputeUrlHash(string url)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
        return Convert.ToBase64String(hashBytes).Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }
}
