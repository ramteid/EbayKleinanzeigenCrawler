using System;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Parser.Implementations;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
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
            var subscriptionQueryUrl = subscription.QueryUrl.ToString();

            if (subscriptionQueryUrl.Contains("ebay-kleinanzeigen.de"))
            {
                return _serviceProvider.GetService<EbayKleinanzeigenParser>();
            }
            else if (subscriptionQueryUrl.Contains("zypresse.com"))
            {
                return _serviceProvider.GetService<ZypresseParser>();
            }
            else
            {
                throw new NotSupportedException($"No parser exists for '{subscriptionQueryUrl}'");
            }
        }
    }
}
