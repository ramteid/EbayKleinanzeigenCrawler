using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Parser.Implementations;
using KleinanzeigenCrawler.Interfaces;
using Moq;
using Serilog;
using HtmlAgilityPack;
using System.Linq;

namespace KleinanzeigenCrawler.Tests.Parser;

public class WgGesuchtParserTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<IQueryExecutor> _mockQueryExecutor;
    private readonly WgGesuchtParser _parser;

    public WgGesuchtParserTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockQueryExecutor = new Mock<IQueryExecutor>();
        _parser = new WgGesuchtParser(_mockLogger.Object, _mockQueryExecutor.Object, null);
    }

    [Fact]
    public void ParseLinks_ShouldParseCorrectly()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <div class='wgg_card offer_list_item'>
                    <h3><a href='/offer/123'>Title 1</a></h3>
                    <div><span>Online: 10.10.2023</span></div>
                    <div><b>500 &euro;</b></div>
                </div>
                <div class='wgg_card offer_list_item'>
                    <h3><a href='https://www.wg-gesucht.de/offer/456'>Title 2</a></h3>
                    <div><span>Online: 11.10.2023</span></div>
                    <div><b>600 €</b></div>
                </div>
            </body>
            </html>";
        var document = new HtmlDocument();
        document.LoadHtml(html);

        // Act
        var results = _parser.ParseLinks(document).ToList();

        // Assert
        results.Should().HaveCount(2);
        results[0].Link.ToString().Should().Be("https://www.wg-gesucht.de/offer/123");
        results[0].CreationDate.Should().Be("Online: 10.10.2023");
        results[0].Price.Should().Be("500 €");

        results[1].Link.ToString().Should().Be("https://www.wg-gesucht.de/offer/456");
        results[1].CreationDate.Should().Be("Online: 11.10.2023");
        results[1].Price.Should().Be("600 €");
    }

    [Fact]
    public void GetTotalResultCount_ShouldParseCorrectly()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <h1 class='list-view-title'>WG-Zimmer in Berlin: 123 Angebote</h1>
            </body>
            </html>";
        var document = new HtmlDocument();
        document.LoadHtml(html);

        // Act
        var count = _parser.GetTotalResultCount(document);

        // Assert
        count.Should().Be(123);
    }

    [Fact]
    public void GetTotalResultCount_ShouldParseCorrectly_WithSeparators()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <h1 class='list-view-title'>WG-Zimmer in Berlin: 1.234 Angebote</h1>
            </body>
            </html>";
        var document = new HtmlDocument();
        document.LoadHtml(html);

        // Act
        var count = _parser.GetTotalResultCount(document);

        // Assert
        count.Should().Be(1234);
    }
}
