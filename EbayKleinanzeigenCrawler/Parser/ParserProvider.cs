using System;
using System.Collections.Concurrent;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using EbayKleinanzeigenCrawler.Parser.Implementations;

namespace EbayKleinanzeigenCrawler.Parser;

public class ParserProvider : IParserProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, IParser> _parsers = new ();

    public ParserProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IParser GetParser(Subscription subscription)
    {
        var subscriptionQueryUrl = subscription.QueryUrl.ToString();

        const string identifierEbayKleinanzeigen = "ebay-kleinanzeigen.de";
        if (subscriptionQueryUrl.Contains(identifierEbayKleinanzeigen))
        {
            return GetOrAddParser(identifierEbayKleinanzeigen, typeof(EbayKleinanzeigenParser));
        }

        const string identifierZypresse = "zypresse.com";
        if (subscriptionQueryUrl.Contains(identifierZypresse))
        {
            return GetOrAddParser(identifierZypresse, typeof(ZypresseParser));
        }

        throw new NotSupportedException($"No parser exists for '{subscriptionQueryUrl}'");
    }

    private IParser GetOrAddParser(string identifier, Type parserType)
    {
        if (parserType.BaseType == null || parserType.BaseType.GetInterfaces().All(interfaceType => interfaceType != typeof(IParser)))
        {
            throw new NotSupportedException($"Unsupported parser type: {parserType.Name}");
        }

        return _parsers.GetOrAdd(identifier, _ => (IParser) _serviceProvider.GetService(parserType));
    }
}