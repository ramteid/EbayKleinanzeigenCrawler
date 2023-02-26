using System;
using System.IO;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.ErrorHandling;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Manager;
using EbayKleinanzeigenCrawler.Parser;
using EbayKleinanzeigenCrawler.Parser.Implementations;
using EbayKleinanzeigenCrawler.Persistence;
using EbayKleinanzeigenCrawler.Query;
using EbayKleinanzeigenCrawler.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EbayKleinanzeigenCrawler;

public class Program
{
    private static async Task Main(string[] _)
    {
        DotNetEnv.Env.Load();
        var serviceCollection = ConfigureServices();

        var logger = serviceCollection.GetService<ILogger>();
        var handler = serviceCollection.GetService<SubscriptionHandler>();

        try
        {
            await handler!.ProcessAllSubscriptionsAsync();
        }
        catch (Exception e)
        {
            logger!.Error(e, "Exception terminated application");
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var serviceCollection = new ServiceCollection();

        serviceCollection.AddTransient<QueryCounter>();
        serviceCollection.AddTransient<IQueryExecutor, QueryExecutor>();
        serviceCollection.AddTransient<ZypresseParser>();
        serviceCollection.AddTransient<EbayKleinanzeigenParser>();
        serviceCollection.AddTransient<SubscriptionHandler>();
        serviceCollection.AddTransient<IDataStorage, JsonStorage>();
        serviceCollection.AddTransient<IParser, ZypresseParser>();
        serviceCollection.AddTransient<IUserAgentProvider, UserAgentProvider>();

        serviceCollection.AddSingleton<IParserProvider, ParserProvider>();
        serviceCollection.AddSingleton<TelegramManager>();
        serviceCollection.AddSingleton<ConsoleManager>();
        serviceCollection.AddSingleton(SelectNotificationManager());
        serviceCollection.AddSingleton<ISubscriptionPersistence, SubscriptionPersistence>();
        serviceCollection.AddSingleton<IAlreadyProcessedUrlsPersistence, AlreadyProcessedUrlsPersistence>();
        serviceCollection.AddSingleton<IErrorStatistics, ErrorStatistics>();

        serviceCollection.AddSingleton<ILogger>(_ => new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{Message:lj} {Exception}{NewLine}")
            .WriteTo.File(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}{Message:lj} {Exception}{NewLine}", 
                path: Path.Join("data", "logfile.txt"), 
                rollOnFileSizeLimit: true, 
                fileSizeLimitBytes: 1 * 1024 * 1024, 
                retainedFileTimeLimit: TimeSpan.FromDays(90)
            )
            .CreateLogger());

        return serviceCollection.BuildServiceProvider();
    }

    private static Func<IServiceProvider, IOutgoingNotifications> SelectNotificationManager()
    {
        var manager = Environment.GetEnvironmentVariable("NOTIFICATION_MANAGER");
        switch (manager)
        {
            case "CONSOLE":
                Console.WriteLine($"Notification manager: {manager}");
                return s => s.GetService<ConsoleManager>();
            default:
                Console.WriteLine("Notification manager: Telegram");
                return s => s.GetService<TelegramManager>();
        }
    }
}