using System;
using DotNetEnv;

namespace KleinanzeigenCrawler.Configuration;

/// <summary>
/// Loads configuration from environment variables and .env files
/// </summary>
public class ConfigurationLoader
{
    public static AppConfiguration LoadConfiguration()
    {
        // Load .env file if it exists
        Env.Load();
        
        var config = new AppConfiguration();
        
        // Telegram configuration
        config.Telegram.BotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
            ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN environment variable is required");
            
        if (int.TryParse(Environment.GetEnvironmentVariable("SESSION_TIMEOUT_MINUTES"), out var sessionTimeout))
        {
            config.Telegram.SessionTimeoutMinutes = sessionTimeout;
        }
        
        if (bool.TryParse(Environment.GetEnvironmentVariable("ALLOW_NEW_USERS"), out var allowNewUsers))
        {
            config.Telegram.AllowNewUsers = allowNewUsers;
        }
        
        // Monitoring configuration
        if (bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_PROMETHEUS"), out var enablePrometheus))
        {
            config.Monitoring.EnablePrometheus = enablePrometheus;
        }
        
        if (int.TryParse(Environment.GetEnvironmentVariable("PROMETHEUS_PORT"), out var prometheusPort))
        {
            config.Monitoring.PrometheusPort = prometheusPort;
        }
        
        // Crawler configuration
        if (int.TryParse(Environment.GetEnvironmentVariable("CHECK_INTERVAL_SECONDS"), out var checkInterval))
        {
            config.Crawler.CheckIntervalSeconds = checkInterval;
        }
        
        config.Crawler.DataPath = Environment.GetEnvironmentVariable("DATA_PATH") ?? "data";
        
        return config;
    }
}
