using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using EbayKleinanzeigenCrawler.Infrastructure;
using EbayKleinanzeigenCrawler.Scheduler;

namespace EbayKleinanzeigenCrawler
{
    public class Program
    {
        private static async Task Main(string[] args)
        {
            AutofacConfig.IoCConfiguration();

            await using ILifetimeScope scope = AutofacConfig.Container.BeginLifetimeScope();
            var scheduler = scope.Resolve<JobScheduler>();

            try
            {
                scheduler.Run();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Thread.Sleep(TimeSpan.FromSeconds(15));
            }
        }
    }
}