namespace KleinanzeigenCrawler.Configuration;

/// <summary>
/// Application configuration settings
/// </summary>
public class AppConfiguration
{
    public MessagingConfiguration Messaging { get; set; } = new();
    public TelegramConfiguration Telegram { get; set; } = new();
    public SignalConfiguration Signal { get; set; } = new();
    public MonitoringConfiguration Monitoring { get; set; } = new();
    public CrawlerConfiguration Crawler { get; set; } = new();
}

/// <summary>
/// Settings shared by all messengers
/// </summary>
public class MessagingConfiguration
{
    public int SessionTimeoutMinutes { get; set; } = 15;
    public bool AllowNewUsers { get; set; } = true;
    
    /// <summary>
    /// Password that a new user has to send once before the bot answers. Null if new users do not need a password.
    /// </summary>
    public string AccessPassword { get; set; }
}

public class TelegramConfiguration
{
    public string BotToken { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken);
}

public class SignalConfiguration
{
    /// <summary>
    /// Base URL of a signal-cli-rest-api instance running in json-rpc mode
    /// </summary>
    public string ApiUrl { get; set; } = "http://signal-api:8080";
    
    /// <summary>
    /// Signal number of the bot in international format
    /// </summary>
    public string Number { get; set; }
    
    public bool Enabled => !string.IsNullOrWhiteSpace(Number);
}

public class MonitoringConfiguration
{
    public bool EnablePrometheus { get; set; } = true;
    public int PrometheusPort { get; set; } = 9090;
}

public class CrawlerConfiguration
{
    public int CheckIntervalSeconds { get; set; } = 60;
    public string DataPath { get; set; } = "data";
    public int LogRetentionDays { get; set; } = 90;
    public int LogFileSizeLimitMB { get; set; } = 1;
}
