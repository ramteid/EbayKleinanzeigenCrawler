using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Query;
using Moq;
using Serilog;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Query;

public class QueryExecutorTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly Uri _url;
    private readonly QueryExecutor _queryExecutor;
    private HttpStatusCode _statusCode = HttpStatusCode.OK;
    private int _requestCount;

    public QueryExecutorTests()
    {
        var port = GetFreePort();
        _url = new Uri($"http://127.0.0.1:{port}/ad");
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ = ServeAsync();

        var userAgentProvider = new Mock<IUserAgentProvider>();
        userAgentProvider.Setup(p => p.GetRandomUserAgent()).Returns("test");
        var logger = Mock.Of<ILogger>();
        _queryExecutor = new QueryExecutor(logger, new QueryCounter(logger), userAgentProvider.Object);
        _queryExecutor.Initialize(TimeSpan.FromMinutes(5), 40, "<invalid>");
    }

    public void Dispose()
    {
        _server.Close();
        _queryExecutor.Dispose();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _server.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _requestCount++;
            context.Response.StatusCode = (int)_statusCode;
            var body = System.Text.Encoding.UTF8.GetBytes("<html><body>ok</body></html>");
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    [Fact]
    public async Task GetHtml_ShouldReturnDocumentOnSuccess()
    {
        // Act
        var document = await _queryExecutor.GetHtml(_url);

        // Assert
        document.Should().NotBeNull();
        document.DocumentNode.InnerText.Should().Contain("ok");
        _requestCount.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task GetHtml_ShouldThrowWithoutRetryWhenPageDoesNotExist(HttpStatusCode statusCode)
    {
        // Arrange
        _statusCode = statusCode;

        // Act
        var act = () => _queryExecutor.GetHtml(_url);

        // Assert
        var exception = await act.Should().ThrowAsync<PageNotFoundException>();
        exception.Which.StatusCode.Should().Be(statusCode);
        _requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHtml_ShouldRetryOnceAndReturnNullOnServerError()
    {
        // Arrange
        _statusCode = HttpStatusCode.InternalServerError;

        // Act
        var document = await _queryExecutor.GetHtml(_url);

        // Assert
        document.Should().BeNull();
        _requestCount.Should().Be(2);
    }
}
