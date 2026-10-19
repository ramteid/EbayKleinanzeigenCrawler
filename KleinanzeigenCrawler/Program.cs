using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Monitoring;
using KleinanzeigenCrawler.Parser;
using KleinanzeigenCrawler.Parser.Implementations;
using KleinanzeigenCrawler.Persistence;
using KleinanzeigenCrawler.Query;
using KleinanzeigenCrawler.Signal;
using KleinanzeigenCrawler.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Serilog;
using Telegram.Bot;

namespace KleinanzeigenCrawler;

public class Program
{
    private static async Task<int> Main(string[] _)
    {
        // Load configuration
        AppConfiguration config;
        try
        {
            config = ConfigurationLoader.LoadConfiguration();
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"Invalid configuration: {e.Message}");
            return 1;
        }
        
        using var serviceProvider = ConfigureServices(config);
        var logger = serviceProvider.GetRequiredService<ILogger>();

        try
        {
            var handler = serviceProvider.GetRequiredService<SubscriptionHandler>();
            var messengers = serviceProvider.GetServices<IMessenger>().ToList();

            // Start Prometheus metrics server if enabled
            if (config.Monitoring.EnablePrometheus)
            {
                var metricServer = new KestrelMetricServer(port: config.Monitoring.PrometheusPort);
                metricServer.Start();
                logger.Information($"Prometheus metrics server started on port {config.Monitoring.PrometheusPort}");
            }

            foreach (var messenger in messengers)
            {
                messenger.Start();
            }

            logger.Information("Running messengers: {Channels}", string.Join(", ", messengers.Select(m => m.Channel)));

            await handler.ProcessAllSubscriptionsAsync();
            return 0;
        }
        catch (Exception e)
        {
            logger.Error(e, "Exception terminated application");
            return 1;
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
        serviceCollection.AddSingleton<IAccessAttemptPersistence, AccessAttemptPersistence>();

        // Query execution
        serviceCollection.AddTransient<QueryCounter>();
        serviceCollection.AddTransient<IQueryExecutor>(sp => new QueryExecutor(
            sp.GetService<ILogger>(),
            sp.GetService<QueryCounter>(),
            sp.GetService<IUserAgentProvider>(),
            sp.GetService<MetricsService>()));

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
        serviceCollection.AddTransient<SubscriptionHandler>();
        
        // Messengers: all configured messengers run simultaneously and notify their own subscribers
        if (config.Telegram.Enabled)
        {
            serviceCollection.AddSingleton<IMessenger>(sp => new TelegramManager(
                new TelegramBotClient(config.Telegram.BotToken),
                sp.GetRequiredService<ILogger>(),
                sp.GetRequiredService<ISubscriptionPersistence>(),
                sp.GetRequiredService<IAccessAttemptPersistence>(),
                sp.GetRequiredService<IParserProvider>(),
                config.Messaging,
                sp.GetRequiredService<MetricsService>()));
        }
        
        if (config.Signal.Enabled)
        {
            serviceCollection.AddSingleton<ISignalClient>(sp => new SignalRestClient(
                config.Signal,
                sp.GetRequiredService<ILogger>()));
            serviceCollection.AddSingleton<IMessenger>(sp => new SignalManager(
                sp.GetRequiredService<ISignalClient>(),
                sp.GetRequiredService<ILogger>(),
                sp.GetRequiredService<ISubscriptionPersistence>(),
                sp.GetRequiredService<IAccessAttemptPersistence>(),
                sp.GetRequiredService<IParserProvider>(),
                config.Messaging,
                sp.GetRequiredService<MetricsService>()));
        }
        
        serviceCollection.AddSingleton<IOutgoingNotifications, CompositeNotifier>();

        return serviceCollection.BuildServiceProvider();
    }
}
