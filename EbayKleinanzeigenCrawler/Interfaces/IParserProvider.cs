using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using System;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IParserProvider
    {
        IParser GetParser(Subscription subscription);
    }
}