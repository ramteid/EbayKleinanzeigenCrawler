using System;
using System.IO;
using EbayKleinanzeigenCrawler.Storage;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Query;
using KleinanzeigenCrawler.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using EbayKleinanzeigenCrawler.Subscriptions;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Persistence;
using EbayKleinanzeigenCrawler.Parser;
using EbayKleinanzeigenCrawler.Parser.Implementations;

namespace KleinanzeigenCrawler
{
    public class Program
    {
        private static void Main(string[] _)
        {
            DotNetEnv.Env.Load();
            var serviceCollection = ConfigureServices();

            var logger = serviceCollection.GetService<ILogger>();
            var handler = serviceCollection.GetService<SubscriptionHandler>();

            try
            {
                handler.Run();
            }
            catch (Exception e)
            {
                logger.Error(e, "Exception terminated application");
            }
        }

        private static ServiceProvider ConfigureServices()
        {
            var serviceCollection = new ServiceCollection();

            serviceCollection.AddSingleton<TelegramManager>();
            serviceCollection.AddSingleton<ConsoleManager>();
            serviceCollection.AddSingleton(SelectNotificationManager());
            serviceCollection.AddSingleton<ISubscriptionPersistence, SubscriptionPersistence>();
            serviceCollection.AddSingleton<IAlreadyProcessedUrlsPersistence, AlreadyProcessedUrlsPersistence>();
            serviceCollection.AddSingleton<QueryCounter>();
            serviceCollection.AddTransient<IQueryExecutor, QueryExecutor>();
            serviceCollection.AddTransient<ZypresseParser>();
            serviceCollection.AddTransient<EbayKleinanzeigenParser>();
            serviceCollection.AddTransient<SubscriptionHandler>();
            serviceCollection.AddTransient<IParserProvider, ParserProvider>();
            serviceCollection.AddTransient<IDataStorage, JsonStorage>();
            serviceCollection.AddTransient<IParser, ZypresseParser>();

            serviceCollection.AddSingleton<ILogger>((_) => new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{Message:lj} {Exception}{NewLine}")
                .WriteTo.File(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level}] {SourceContext}{Message:lj} {Exception}{NewLine}", path: Path.Join("data", "logfile.txt"), rollOnFileSizeLimit: true, fileSizeLimitBytes: 1 * 1024 * 1024)
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
                    Console.WriteLine($"Notification manager: Telegram");
                    return s => s.GetService<TelegramManager>();
            }
        }
    }
}
