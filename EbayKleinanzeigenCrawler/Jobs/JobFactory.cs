using Autofac;
using EbayKleinanzeigenCrawler.Interfaces;

namespace EbayKleinanzeigenCrawler.Jobs
{
    public class JobFactory : IJobFactory
    {
        private readonly ILifetimeScope _scope;

        public JobFactory(ILifetimeScope scope)
        {
            _scope = scope;
        }

        public CrawlJob CreateInstance()
        {
            return _scope.Resolve<CrawlJob>();
        }
    }
}
