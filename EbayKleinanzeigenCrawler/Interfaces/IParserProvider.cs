using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface IParserProvider
{
    IParser GetParser(Subscription subscription);
}