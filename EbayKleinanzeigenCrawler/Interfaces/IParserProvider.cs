using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IParserProvider
    {
        IParser GetInstance(Subscription subscription);
    }
}