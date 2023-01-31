using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IParserProvider
    {
        IParser GetParser(Subscription subscription);
    }
}