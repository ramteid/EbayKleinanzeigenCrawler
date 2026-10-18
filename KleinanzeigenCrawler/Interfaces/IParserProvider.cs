using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Interfaces;

public interface IParserProvider
{
    IParser GetParser(Subscription subscription);
}