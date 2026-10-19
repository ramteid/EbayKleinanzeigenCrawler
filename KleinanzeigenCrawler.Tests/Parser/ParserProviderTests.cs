using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Parser;
using KleinanzeigenCrawler.Parser.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;
using System;

namespace KleinanzeigenCrawler.Tests.Parser;

public class ParserProviderTests
{
    private readonly ParserProvider _parserProvider;

    public ParserProviderTests()
    {
        var services = new ServiceCollection();
        services.AddTransient(_ => new KleinanzeigenParser(Mock.Of<ILogger>(), Mock.Of<IQueryExecutor>()));
        services.AddTransient(_ => new WgGesuchtParser(Mock.Of<ILogger>(), Mock.Of<IQueryExecutor>()));
        _parserProvider = new ParserProvider(services.BuildServiceProvider());
    }

    [Theory]
    [InlineData("https://www.kleinanzeigen.de/s-test/k0", typeof(KleinanzeigenParser))]
    [InlineData("https://kleinanzeigen.de/s-test/k0", typeof(KleinanzeigenParser))]
    [InlineData("https://www.wg-gesucht.de/wg-zimmer-in-Freiburg.html", typeof(WgGesuchtParser))]
    public void GetParser_ShouldReturnParserForSupportedDomainAndItsSubdomains(string url, Type expectedParserType)
    {
        // Act
        var parser = _parserProvider.GetParser(new Subscription { QueryUrl = new Uri(url) });

        // Assert
        parser.Should().BeOfType(expectedParserType);
    }

    [Theory]
    [InlineData("https://www.evilkleinanzeigen.de/s-test/k0")]
    [InlineData("https://www.kleinanzeigen.de.example.com/s-test/k0")]
    [InlineData("https://www.not-wg-gesucht.de/wg-zimmer.html")]
    [InlineData("https://www.ebay-kleinanzeigen.de/s-test/k0")]
    public void GetParser_ShouldRejectOtherDomains(string url)
    {
        // Act
        var act = () => _parserProvider.GetParser(new Subscription { QueryUrl = new Uri(url) });

        // Assert
        act.Should().Throw<NotSupportedException>();
    }
}
