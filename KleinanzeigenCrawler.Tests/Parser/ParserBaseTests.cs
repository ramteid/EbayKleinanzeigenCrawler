using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Parser;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Monitoring;
using KleinanzeigenCrawler.Models;
using Moq;
using Serilog;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KleinanzeigenCrawler.Tests.Parser;

/// <summary>
/// Tests for ParserBase covering critical parsing logic
/// </summary>
public class ParserBaseTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly Mock<IQueryExecutor> _mockQueryExecutor;
    private readonly Mock<MetricsService> _mockMetrics;
    
    public ParserBaseTests()
    {
        _mockLogger = new Mock<ILogger>();
        _mockQueryExecutor = new Mock<IQueryExecutor>();
        _mockMetrics = new Mock<MetricsService>();
    }
    
    [Fact]
    public void ParseLinks_ShouldReturnEmptyWhenHtmlInvalid()
    {
        // Arrange
        var parser = new TestParser(_mockLogger.Object, _mockQueryExecutor.Object, null);
        parser.SetValidHtml(false);
        var document = new HtmlDocument();
        document.LoadHtml("<html><body></body></html>");
        
        // Act
        var results = parser.ParseLinks(document).ToList();
        
        // Assert
        results.Should().BeEmpty();
        // Note: MetricsService.RecordParseError is not virtual, so we can't verify it with Moq
    }
    
    [Fact]
    public void ParseLinks_ShouldSkipResultsMarkedForSkipping()
    {
        // Arrange
        var parser = new TestParser(_mockLogger.Object, _mockQueryExecutor.Object, _mockMetrics.Object);
        parser.SetValidHtml(true);
        parser.SetSkipResult(true);
        
        var document = new HtmlDocument();
        document.LoadHtml(@"
            <html><body>
                <div class='result'>Result 1</div>
                <div class='result'>Result 2</div>
            </body></html>");
        
        // Act
        var results = parser.ParseLinks(document).ToList();
        
        // Assert
        results.Should().BeEmpty();
    }
    
    [Fact]
    public void ParseLinks_ShouldHandleNullResultsGracefully()
    {
        // Arrange
        var parser = new TestParser(_mockLogger.Object, _mockQueryExecutor.Object, _mockMetrics.Object);
        parser.SetValidHtml(true);
        parser.SetResultsNull(true);
        
        var document = new HtmlDocument();
        document.LoadHtml("<html><body></body></html>");
        
        // Act
        var results = parser.ParseLinks(document).ToList();
        
        // Assert
        results.Should().BeEmpty();
    }
    
    [Fact]
    public void ParseLinks_ShouldContinueOnParseResultLinkException()
    {
        // Arrange
        var parser = new TestParser(_mockLogger.Object, _mockQueryExecutor.Object, null);
        parser.SetValidHtml(true);
        parser.ThrowOnParseResultLink(true);
        
        var document = new HtmlDocument();
        document.LoadHtml(@"
            <html><body>
                <div class='result'><a href='/ad1'>Ad 1</a></div>
                <div class='result'><a href='/ad2'>Ad 2</a></div>
            </body></html>");
        
        // Act
        var results = parser.ParseLinks(document).ToList();
        
        // Assert
        results.Should().BeEmpty(); // All results throw exceptions
        // Note: MetricsService.RecordParseError is not virtual, so we can't verify it with Moq
    }
    
    [Fact]
    public void ParseLinks_ShouldReturnValidResults()
    {
        // Arrange
        var parser = new TestParser(_mockLogger.Object, _mockQueryExecutor.Object, _mockMetrics.Object);
        parser.SetValidHtml(true);
        
        var document = new HtmlDocument();
        document.LoadHtml(@"
            <html><body>
                <div class='result'><a href='https://example.com/ad1'>Ad 1</a></div>
                <div class='result'><a href='https://example.com/ad2'>Ad 2</a></div>
            </body></html>");
        
        // Act
        var results = parser.ParseLinks(document).ToList();
        
        // Assert
        results.Should().HaveCount(2);
        results.Select(r => r.Link.ToString()).Should().Contain(new[] 
        { 
            "https://example.com/ad1", 
            "https://example.com/ad2" 
        });
    }
    
    // Test implementation of ParserBase for testing abstract methods
    private class TestParser : ParserBase
    {
        private bool _validHtml = true;
        private bool _skipResult = false;
        private bool _resultsNull = false;
        private bool _throwOnParseResultLink = false;
        
        public TestParser(ILogger logger, IQueryExecutor queryExecutor, MetricsService metricsService) 
            : base(logger, queryExecutor, metricsService) { }
        
        public void SetValidHtml(bool valid) => _validHtml = valid;
        public void SetSkipResult(bool skip) => _skipResult = skip;
        public void SetResultsNull(bool isNull) => _resultsNull = isNull;
        public void ThrowOnParseResultLink(bool throwException) => _throwOnParseResultLink = throwException;
        
        protected override TimeSpan TimeToWaitBetweenMaxAmountOfRequests => TimeSpan.FromMinutes(5);
        protected override uint AllowedRequestsPerTimespan => 40;
        protected override string InvalidHtml => "<invalid>";
        
        protected override bool EnsureValidHtml(HtmlDocument resultPage) => _validHtml;
        
        public override List<Uri> GetAdditionalPages(HtmlDocument document) => new List<Uri>();
        
        protected override List<HtmlNode> ParseResults(HtmlDocument resultPage)
        {
            if (_resultsNull) return null;
            return resultPage.DocumentNode.SelectNodes("//div[@class='result']")?.ToList() 
                   ?? new List<HtmlNode>();
        }
        
        protected override bool ShouldSkipResult(HtmlNode result) => _skipResult;
        
        protected override Uri ParseResultLink(HtmlNode result)
        {
            if (_throwOnParseResultLink)
                throw new Exception("Test exception");
                
            var link = result.SelectSingleNode(".//a")?.GetAttributeValue("href", null);
            return link != null ? new Uri(link) : null;
        }
        
        protected override string ParseResultDate(HtmlNode result) => "2025-11-17";
        protected override string ParseResultPrice(HtmlNode result) => "100 €";
        protected override string ParseTitle(HtmlDocument document) => "Test Title";
        protected override string ParseDescriptionText(HtmlDocument document) => "Test Description";
        
        public override int? GetTotalResultCount(HtmlDocument document) => 100; // Return a test value
    }
}
