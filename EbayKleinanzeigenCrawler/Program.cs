using System;
using System.IO;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Jobs;
using EbayKleinanzeigenCrawler.Manager;
using EbayKleinanzeigenCrawler.Parser;
using EbayKleinanzeigenCrawler.Query;
using EbayKleinanzeigenCrawler.Scheduler;
using EbayKleinanzeigenCrawler.Storage;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace EbayKleinanzeigenCrawler
{
    public class Program
    {
        private static void Main(string[] args)
        {
            var serviceCollection = ConfigureServices();

            var logger = serviceCollection.GetService<ILogger>();
            var scheduler = serviceCollection.GetService<JobScheduler>();

            try
            {
                scheduler.Run();
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
            serviceCollection.AddSingleton<IOutgoingNotifications>(s => s.GetService<TelegramManager>());
            serviceCollection.AddSingleton<ISubscriptionManager>(s => s.GetService<TelegramManager>());
            serviceCollection.AddSingleton<IParserProvider, ParserProvider>();
            serviceCollection.AddSingleton<QueryCounter>();
            serviceCollection.AddSingleton<QueryExecutor>();
            serviceCollection.AddTransient<JobScheduler>();
            serviceCollection.AddTransient<CrawlJob>();
            serviceCollection.AddTransient<IJobFactory, JobFactory>();
            serviceCollection.AddTransient<IDataStorage, JsonStorage>();
            serviceCollection.AddTransient<IParser, EbayKleinanzeigenParser>();

            serviceCollection.AddSingleton<ILogger>((_) => new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{Message:lj} {Exception}{NewLine}")
                .WriteTo.File(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level}] {SourceContext}{Message:lj} {Exception}{NewLine}", path: Path.Join("data", "logfile.txt"), rollOnFileSizeLimit: true, fileSizeLimitBytes: 1 * 1024 * 1024)
                .CreateLogger());

            return serviceCollection.BuildServiceProvider();
        }
    }
}