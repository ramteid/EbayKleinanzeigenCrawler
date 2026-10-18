using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Parser.Implementations;

namespace KleinanzeigenCrawler.Parser;

public class ParserProvider : IParserProvider
{
    private static readonly IReadOnlyList<ParserRegistration> ParserRegistrations = new[]
    {
        ParserRegistration.ForHostSuffixes("kleinanzeigen", typeof(KleinanzeigenParser), "ebay-kleinanzeigen.de", "kleinanzeigen.de"),
        ParserRegistration.ForHostSuffixes("wg-gesucht", typeof(WgGesuchtParser), "wg-gesucht.de")
    };

    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, IParser> _parsers = new ();

    public ParserProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IParser GetParser(Subscription subscription)
    {
        if (subscription?.QueryUrl == null)
        {
            throw new ArgumentNullException(nameof(subscription), "Subscription requires a QueryUrl.");
        }

        var host = subscription.QueryUrl.Host ?? string.Empty;
        var registration = ParserRegistrations.FirstOrDefault(p => p.Supports(host));
        if (registration == null)
        {
            throw new NotSupportedException($"No parser exists for host '{host}'.");
        }

        return _parsers.GetOrAdd(registration.Identifier, _ => CreateParser(registration.ParserType));
    }

    private IParser CreateParser(Type parserType)
    {
        if (!typeof(IParser).IsAssignableFrom(parserType))
        {
            throw new NotSupportedException($"Unsupported parser type: {parserType.Name}");
        }

        return (IParser) (_serviceProvider.GetService(parserType) ??
                          throw new InvalidOperationException($"Could not resolve parser '{parserType.Name}' from the service provider."));
    }

    private sealed record ParserRegistration(string Identifier, Type ParserType, string[] HostSuffixes)
    {
        public bool Supports(string host)
        {
            return HostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        public static ParserRegistration ForHostSuffixes(string identifier, Type parserType, params string[] hostSuffixes)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                throw new ArgumentException("Identifier is required.", nameof(identifier));
            }

            if (parserType == null)
            {
                throw new ArgumentNullException(nameof(parserType));
            }

            if (hostSuffixes == null || hostSuffixes.Length == 0)
            {
                throw new ArgumentException("At least one host suffix is required.", nameof(hostSuffixes));
            }

            return new ParserRegistration(identifier, parserType, hostSuffixes);
        }
    }
}
