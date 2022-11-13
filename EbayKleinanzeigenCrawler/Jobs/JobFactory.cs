using EbayKleinanzeigenCrawler.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace EbayKleinanzeigenCrawler.Jobs
{
    public class JobFactory : IJobFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public JobFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public CrawlJob CreateInstance()
        {
            return _serviceProvider.GetService<CrawlJob>();
        }
    }
}
