using EbayKleinanzeigenCrawler.Jobs;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IJobFactory
    {
        CrawlJob CreateInstance();
    }
}