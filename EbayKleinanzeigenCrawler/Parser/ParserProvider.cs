using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;

namespace EbayKleinanzeigenCrawler.Parser
{
    public class ParserProvider : IParserProvider
    {
        private readonly IServiceProvider _serviceProvider;

        public ParserProvider(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IParser GetInstance(Subscription subscription)
        {
            return new EbayKleinanzeigenParser(_serviceProvider.GetService<ILogger>(), subscription);
        }
    }
}
