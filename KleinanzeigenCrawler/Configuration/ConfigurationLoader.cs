using System;
using System.Text.RegularExpressions;
using DotNetEnv;

namespace KleinanzeigenCrawler.Configuration;

/// <summary>
/// Loads configuration from environment variables and .env files
/// </summary>
public class ConfigurationLoader
{
    private const int MinimumAccessPasswordLength = 8;
    private static readonly Regex InternationalNumber = new(@"^\+[1-9][0-9]{6,14}$");

    public static AppConfiguration LoadConfiguration()
    {
        // Load .env file if it exists
        Env.Load();
        
        return Build(Environment.GetEnvironmentVariable);
    }

    public static AppConfiguration Build(Func<string, string> getVariable)
    {
        var config = new AppConfiguration();
        
        // Messaging configuration
        config.Messaging.SessionTimeoutMinutes = ReadInt(getVariable, "SESSION_TIMEOUT_MINUTES", config.Messaging.SessionTimeoutMinutes, min: 1, max: 10080);
        config.Messaging.AllowNewUsers = ReadBool(getVariable, "ALLOW_NEW_USERS", config.Messaging.AllowNewUsers);
        config.Messaging.AccessPassword = ReadAccessPassword(getVariable);
        
        // Telegram configuration
        config.Telegram.BotToken = getVariable("TELEGRAM_BOT_TOKEN");
        
        // Signal configuration
        config.Signal.Number = getVariable("SIGNAL_NUMBER");
        var signalApiUrl = getVariable("SIGNAL_API_URL");
        if (!string.IsNullOrWhiteSpace(signalApiUrl))
        {
            config.Signal.ApiUrl = signalApiUrl;
        }
        
        if (!config.Telegram.Enabled && !config.Signal.Enabled)
        {
            throw new InvalidOperationException("No messenger is configured. Set TELEGRAM_BOT_TOKEN and/or SIGNAL_NUMBER.");
        }
        
        if (config.Signal.Enabled)
        {
            ValidateSignal(config.Signal);
        }
        
        // Monitoring configuration
        config.Monitoring.EnablePrometheus = ReadBool(getVariable, "ENABLE_PROMETHEUS", config.Monitoring.EnablePrometheus);
        config.Monitoring.PrometheusPort = ReadInt(getVariable, "PROMETHEUS_PORT", config.Monitoring.PrometheusPort, min: 1, max: 65535);
        
        // Crawler configuration
        config.Crawler.CheckIntervalSeconds = ReadInt(getVariable, "CHECK_INTERVAL_SECONDS", config.Crawler.CheckIntervalSeconds, min: 1, max: 86400);
        config.Crawler.DataPath = getVariable("DATA_PATH") ?? "data";
        
        return config;
    }

    /// <summary>
    /// Reads an integer setting. An unset or empty variable keeps the default, an invalid value is an error.
    /// </summary>
    private static int ReadInt(Func<string, string> getVariable, string name, int defaultValue, int min, int max = int.MaxValue)
    {
        var value = getVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
        {
            var range = max == int.MaxValue ? $"at least {min}" : $"between {min} and {max}";
            throw new InvalidOperationException($"{name} must be a whole number {range}, but was '{value}'");
        }

        return parsed;
    }

    /// <summary>
    /// Reads a boolean setting. An unset or empty variable keeps the default, an invalid value is an error.
    /// A misspelled ALLOW_NEW_USERS must not silently keep the registration open.
    /// </summary>
    private static bool ReadBool(Func<string, string> getVariable, string name, bool defaultValue)
    {
        var value = getVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException($"{name} must be true or false, but was '{value}'");
        }

        return parsed;
    }

    /// <summary>
    /// Reads the access password without surrounding whitespace. An unset or empty variable means that new users do not need one.
    /// A short password is an error, because only the number of failed attempts per user limits guessing it.
    /// The error never contains the value.
    /// </summary>
    private static string ReadAccessPassword(Func<string, string> getVariable)
    {
        var password = getVariable("ACCESS_PASSWORD")?.Trim();
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        if (password.Length < MinimumAccessPasswordLength)
        {
            throw new InvalidOperationException($"ACCESS_PASSWORD must have at least {MinimumAccessPasswordLength} characters");
        }

        return password;
    }

    private static void ValidateSignal(SignalConfiguration signal)
    {
        if (!InternationalNumber.IsMatch(signal.Number))
        {
            throw new InvalidOperationException("SIGNAL_NUMBER must be in international format, e.g. +491701234567");
        }
        
        if (!Uri.TryCreate(signal.ApiUrl, UriKind.Absolute, out var apiUrl)
            || (apiUrl.Scheme != Uri.UriSchemeHttp && apiUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("SIGNAL_API_URL must be an absolute http or https URL, e.g. http://signal-api:8080");
        }
    }
}
