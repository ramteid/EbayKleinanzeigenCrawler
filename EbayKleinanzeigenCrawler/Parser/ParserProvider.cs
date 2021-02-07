using Autofac;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Parser
{
    public class ParserProvider : IParserProvider
    {
        private readonly ILifetimeScope _scope;

        public ParserProvider(ILifetimeScope scope)
        {
            _scope = scope;
        }

        public IParser GetInstance(Subscription subscription)
        {
            return _scope.Resolve<IParser>(new NamedParameter("subscription", subscription));
        }
    }
}
