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

        const string identifierEbayKleinanzeigenOld = "ebay-kleinanzeigen.de";
        const string identifierEbayKleinanzeigenNew = "www.kleinanzeigen.de";
        if (subscriptionQueryUrl.Contains(identifierEbayKleinanzeigenOld) || subscriptionQueryUrl.Contains(identifierEbayKleinanzeigenNew))
        {
            return GetOrAddParser(identifierEbayKleinanzeigenNew, typeof(EbayKleinanzeigenParser));
        }

        const string identifierZypresse = "zypresse.com";
        if (subscriptionQueryUrl.Contains(identifierZypresse))
        {
            return GetOrAddParser(identifierZypresse, typeof(ZypresseParser));
        }

        const string identifierWgGesucht = "wg-gesucht.de";
        if (subscriptionQueryUrl.Contains(identifierWgGesucht))
        {
            return GetOrAddParser(identifierWgGesucht, typeof(WgGesuchtParser));
        }

        throw new NotSupportedException($"No parser exists for '{subscriptionQueryUrl}'");
    }

    private IParser GetOrAddParser(string identifier, Type parserType)
    {
        if (parserType.BaseType == null || parserType.BaseType.GetInterfaces().All(interfaceType => interfaceType != typeof(IParser)))
        {
            throw new NotSupportedException($"Unsupported parser type: {parserType.Name}");
        }

        return _parsers.GetOrAdd(identifier, _ => (IParser) _serviceProvider.GetService(parserType) ?? throw new InvalidOperationException("Could not get Parser from service provider"));
    }
}