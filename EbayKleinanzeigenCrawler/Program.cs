using System;
using System.Threading.Tasks;
using Autofac;
using EbayKleinanzeigenCrawler.Infrastructure;
using EbayKleinanzeigenCrawler.Scheduler;
using Serilog;

namespace EbayKleinanzeigenCrawler
{
    public class Program
    {
        private static async Task Main(string[] args)
        {
            AutofacConfig.IoCConfiguration();

            await using ILifetimeScope scope = AutofacConfig.Container.BeginLifetimeScope();
            var logger = scope.Resolve<ILogger>();
            var scheduler = scope.Resolve<JobScheduler>();

            try
            {
                scheduler.Run();
            }
            catch (Exception e)
            {
                logger.Error(e, "Exception terminated application");
            }
        }
    }
}