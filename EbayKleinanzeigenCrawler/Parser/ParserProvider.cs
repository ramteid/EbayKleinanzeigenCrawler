using System;
using EbayKleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Parser;
using Microsoft.Extensions.DependencyInjection;

namespace EbayKleinanzeigenCrawler.Parser
{
    public class ParserProvider : IParserProvider
    {
        private IServiceProvider _serviceProvider;

        public ParserProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IParser GetParser(Subscription subscription)
        {
            if (subscription.QueryUrl.ToString().Contains("ebay-kleinanzeigen"))
            {
                return _serviceProvider.GetService<EbayKleinanzeigenParser>();
            }
            else
            {
                throw new NotSupportedException($"No parser exists for '{subscription.QueryUrl}'");
            }
        }
    }
}
