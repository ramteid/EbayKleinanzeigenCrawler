namespace KleinanzeigenCrawler.Configuration;

/// <summary>
/// Application configuration settings
/// </summary>
public class AppConfiguration
{
    public TelegramConfiguration Telegram { get; set; } = new();
    public MonitoringConfiguration Monitoring { get; set; } = new();
    public CrawlerConfiguration Crawler { get; set; } = new();
}

public class TelegramConfiguration
{
    public string BotToken { get; set; }
    public int SessionTimeoutMinutes { get; set; } = 15;
    public bool AllowNewUsers { get; set; } = true;
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
