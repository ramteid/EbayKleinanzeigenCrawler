using Autofac;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Jobs;
using EbayKleinanzeigenCrawler.Manager;
using EbayKleinanzeigenCrawler.Parser;
using EbayKleinanzeigenCrawler.Query;
using EbayKleinanzeigenCrawler.Scheduler;
using EbayKleinanzeigenCrawler.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Sinks.SystemConsole.Themes;

namespace EbayKleinanzeigenCrawler.Infrastructure
{
    public class AutofacConfig
    {
        public static IContainer Container { get; private set; }

        public static void IoCConfiguration()
        {
            var builder = new ContainerBuilder();
            builder.RegisterType<TelegramManager>().As<IOutgoingNotifications>().As<ISubscriptionManager>().SingleInstance();
            builder.RegisterType<ParserProvider>().As<IParserProvider>().SingleInstance();
            builder.RegisterType<EbayKleinanzeigenParser>().As<IParser>();
            builder.RegisterType<QueryCounter>().SingleInstance();
            builder.RegisterType<QueryExecutor>().SingleInstance();
            builder.RegisterType<JobScheduler>();
            builder.RegisterType<CrawlJob>();
            builder.RegisterType<JobFactory>().As<IJobFactory>();
            builder.RegisterType<JsonStorage>().As<IDataStorage>();

            Logger logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{Message:lj} {Exception}{NewLine}", theme: AnsiConsoleTheme.Code)
                .WriteTo.File(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level}] {SourceContext}{Message:lj} {Exception}{NewLine}", path: "logfile.txt", rollOnFileSizeLimit: true, fileSizeLimitBytes: 1 * 1024 * 1024)
                .CreateLogger();

            builder.Register<ILogger>((c, p) => logger).SingleInstance();

            Container = builder.Build();
        }
    }
}
