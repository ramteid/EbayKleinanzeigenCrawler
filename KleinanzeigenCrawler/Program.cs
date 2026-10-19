using System;
using System.IO;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Monitoring;
using KleinanzeigenCrawler.Parser;
using KleinanzeigenCrawler.Parser.Implementations;
using KleinanzeigenCrawler.Persistence;
using KleinanzeigenCrawler.Query;
using KleinanzeigenCrawler.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Serilog;

namespace KleinanzeigenCrawler;

public class Program
{
    private static async Task Main(string[] _)
    {
        // Load configuration
        var config = ConfigurationLoader.LoadConfiguration();
        
        var serviceCollection = ConfigureServices(config);

        var logger = serviceCollection.GetService<ILogger>();
        var handler = serviceCollection.GetService<SubscriptionHandler>();

        try
        {
            // Start Prometheus metrics server if enabled
            if (config.Monitoring.EnablePrometheus)
            {
                var metricServer = new KestrelMetricServer(port: config.Monitoring.PrometheusPort);
                metricServer.Start();
                logger!.Information($"Prometheus metrics server started on port {config.Monitoring.PrometheusPort}");
            }

            await handler!.ProcessAllSubscriptionsAsync();
        }
        catch (Exception e)
        {
            logger!.Error(e, "Exception terminated application");
        }
    }

    private static ServiceProvider ConfigureServices(AppConfiguration config)
    {
        var serviceCollection = new ServiceCollection();

        // Configuration
        serviceCollection.AddSingleton(config);

        // Logging (register early as it's needed by many services)
        serviceCollection.AddSingleton<ILogger>(_ => new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{Message:lj} {Exception}{NewLine}")
            .WriteTo.File(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}{Message:lj} {Exception}{NewLine}", 
                path: Path.Join(config.Crawler.DataPath, "logfile.txt"), 
                // The retention time only applies to files named after a rolling interval, and the default count limit of 31 files would end it early
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true, 
                fileSizeLimitBytes: config.Crawler.LogFileSizeLimitMB * 1024 * 1024, 
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(config.Crawler.LogRetentionDays)
            )
            .CreateLogger());

        // Metrics (needs to be early for parser injection)
        serviceCollection.AddSingleton<MetricsService>();

        // Infrastructure services
        serviceCollection.AddSingleton<IDataStorage, JsonStorage>();
        serviceCollection.AddSingleton<IUserAgentProvider, UserAgentProvider>();
        
        // Redis
        var redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ?? "redis:6379";
        serviceCollection.AddSingleton<IUrlTracker>(sp =>
            new RedisUrlTracker(
                sp.GetService<ILogger>(),
                redisConnectionString,
                sp.GetService<MetricsService>()));

        // Persistence
        serviceCollection.AddSingleton<ISubscriptionPersistence, SubscriptionPersistence>();

        // Query execution
        serviceCollection.AddTransient<QueryCounter>();
        serviceCollection.AddTransient<IQueryExecutor>(sp => new QueryExecutor(
            sp.GetService<ILogger>(),
            sp.GetService<QueryCounter>(),
            sp.GetService<IUserAgentProvider>(),
            sp.GetService<MetricsService>(),
            config.Crawler.DataPath));

        // Parsers
        serviceCollection.AddTransient<KleinanzeigenParser>(sp => new KleinanzeigenParser(
            sp.GetService<ILogger>(),
            sp.GetService<IQueryExecutor>(),
            sp.GetService<MetricsService>()));
        serviceCollection.AddTransient<WgGesuchtParser>(sp => new WgGesuchtParser(
            sp.GetService<ILogger>(),
            sp.GetService<IQueryExecutor>(),
            sp.GetService<MetricsService>()));
        serviceCollection.AddSingleton<IParserProvider, ParserProvider>();

        // Business services
        serviceCollection.AddSingleton<SubscriptionService>();
        serviceCollection.AddTransient<SubscriptionHandler>();
        
        // Notification manager
        serviceCollection.AddSingleton(SelectNotificationManager());

        return serviceCollection.BuildServiceProvider();
    }

    private static Func<IServiceProvider, IOutgoingNotifications> SelectNotificationManager()
    {
        // Always use Telegram with modernized UI (button-based)
        Console.WriteLine("Notification manager: Telegram (Modernized UI)");
        return s => new TelegramManager(
            s.GetService<ILogger>(),
            s.GetService<ISubscriptionPersistence>(),
            s.GetService<IParserProvider>(),
            s.GetService<AppConfiguration>()
        );
    }
}
