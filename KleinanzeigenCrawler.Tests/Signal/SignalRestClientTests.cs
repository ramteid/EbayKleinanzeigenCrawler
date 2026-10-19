using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Signal;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Signal;

public class SignalRestClientTests : IAsyncLifetime
{
    private const string BotNumber = "+491701234567";
    private const string SenderUuid = "11111111-2222-3333-4444-555555555555";

    private readonly Mock<ILogger> _logger = new();
    private readonly ConcurrentQueue<SignalMessage> _received = new();
    private readonly CancellationTokenSource _cancellation = new();
    private FakeSignalApi _api;
    private SignalRestClient _client;
    private Task _receiving = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        _api = await FakeSignalApi.StartAsync(BotNumber);
        _client = new SignalRestClient(
            new SignalConfiguration { ApiUrl = _api.BaseUri.ToString(), Number = BotNumber },
            _logger.Object,
            initialReconnectDelay: TimeSpan.FromMilliseconds(20));
    }

    public async Task DisposeAsync()
    {
        _cancellation.Cancel();
        await _receiving;
        _client.Dispose();
        await _api.DisposeAsync();
    }

    private void StartReceiving() => _receiving = _client.ReceiveAsync(_received.Enqueue, _cancellation.Token);

    private static string TextEnvelope(string text, string sourceUuid = SenderUuid) => $$$"""
        {"envelope":{"source":"+4915112345678","sourceNumber":"+4915112345678","sourceUuid":"{{{sourceUuid}}}","sourceName":"Alice","sourceDevice":1,"timestamp":1700000000000,"dataMessage":{"timestamp":1700000000000,"message":{{{JsonSerializer.Serialize(text)}}},"expiresInSeconds":0,"viewOnce":false}},"account":"{{{BotNumber}}}"}
        """;

    [Fact]
    public async Task SendAsync_ShouldPostTheMessageToTheSendEndpoint()
    {
        // Act
        await _client.SendAsync(SenderUuid, "Hello\nWorld");

        // Assert
        using var request = JsonDocument.Parse(_api.SentRequests.Should().ContainSingle().Subject);
        request.RootElement.GetProperty("message").GetString().Should().Be("Hello\nWorld");
        request.RootElement.GetProperty("number").GetString().Should().Be(BotNumber);
        request.RootElement.GetProperty("recipients").EnumerateArray().Select(r => r.GetString()).Should().Equal(SenderUuid);
    }

    [Fact]
    public async Task SendAsync_ShouldThrowWithStatusCodeAndErrorWhenTheApiRejectsTheMessage()
    {
        // Arrange
        _api.SendStatusCode = HttpStatusCode.BadRequest;
        _api.SendResponseBody = "{\"error\":\"Unregistered user\"}";

        // Act
        var act = () => _client.SendAsync(SenderUuid, "Hello");

        // Assert
        var exception = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        exception.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        exception.Message.Should().Contain("Unregistered user");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldPassTextMessagesToTheHandler()
    {
        // Arrange
        StartReceiving();
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();

        // Act
        await _api.PushAsync(TextEnvelope("/add"));

        // Assert
        (await Wait.UntilAsync(() => !_received.IsEmpty)).Should().BeTrue();
        _received.Should().ContainSingle().Which.Should().Be(new SignalMessage(SenderUuid, "/add"));
        _api.ReceivePaths.Should().ContainSingle().Which.Should().Be($"/v1/receive/{BotNumber}");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldSkipEverythingThatIsNotATextMessageOfAOneToOneChat()
    {
        // Arrange
        StartReceiving();
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();

        // Act
        await _api.PushAsync("{\"envelope\":{\"sourceUuid\":\"x\",\"typingMessage\":{\"action\":\"STARTED\"}},\"account\":\"+1\"}");
        await _api.PushAsync("this is not json");
        await _api.PushAsync(TextEnvelope("after the noise"));

        // Assert
        (await Wait.UntilAsync(() => !_received.IsEmpty)).Should().BeTrue();
        _received.Should().ContainSingle().Which.Text.Should().Be("after the noise");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldReconnectAfterTheApiClosesTheConnection()
    {
        // Arrange
        StartReceiving();
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();

        // Act
        await _api.CloseConnectionsAsync();
        var reconnected = await _api.WaitForConnectionsAsync(2);
        await _api.PushAsync(TextEnvelope("after reconnect"));

        // Assert
        reconnected.Should().BeTrue();
        (await Wait.UntilAsync(() => !_received.IsEmpty)).Should().BeTrue();
        _received.Single().Text.Should().Be("after reconnect");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldReconnectAfterTheConnectionBreaks()
    {
        // Arrange
        StartReceiving();
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();

        // Act
        _api.AbortConnections();
        var reconnected = await _api.WaitForConnectionsAsync(2);
        await _api.PushAsync(TextEnvelope("after break"));

        // Assert
        reconnected.Should().BeTrue();
        (await Wait.UntilAsync(() => !_received.IsEmpty)).Should().BeTrue();
        _received.Single().Text.Should().Be("after break");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldNotConnectWhileTheApiIsNotInJsonRpcMode()
    {
        // Arrange
        _api.Mode = "normal";

        // Act
        StartReceiving();
        var retried = await Wait.UntilAsync(() => _api.AboutRequestCount >= 3);

        // Assert
        retried.Should().BeTrue("the mode is checked again after every failure");
        _api.ConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task ReceiveAsync_ShouldConnectOnceTheApiSwitchesToJsonRpcMode()
    {
        // Arrange
        _api.Mode = "native";
        StartReceiving();
        (await Wait.UntilAsync(() => _api.AboutRequestCount >= 2)).Should().BeTrue();

        // Act
        _api.Mode = "json-rpc";

        // Assert
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_ShouldNotConnectWhileTheNumberIsNotRegistered()
    {
        // Arrange
        _api.Accounts = new[] { "+4915198765432" };

        // Act
        StartReceiving();
        var retried = await Wait.UntilAsync(() => _api.AccountsRequestCount >= 3);

        // Assert
        retried.Should().BeTrue("the registration is checked again after every failure");
        _api.ConnectionCount.Should().Be(0);
    }

    [Fact]
    public async Task ReceiveAsync_ShouldConnectOnceTheNumberIsRegistered()
    {
        // Arrange
        _api.Accounts = Array.Empty<string>();
        StartReceiving();
        (await Wait.UntilAsync(() => _api.AccountsRequestCount >= 2)).Should().BeTrue();

        // Act
        _api.Accounts = new[] { BotNumber };

        // Assert
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveAsync_ShouldCompleteWhenCancelled()
    {
        // Arrange
        StartReceiving();
        (await _api.WaitForConnectionsAsync(1)).Should().BeTrue();

        // Act
        _cancellation.Cancel();

        // Assert
        (await Wait.UntilAsync(() => _receiving.IsCompletedSuccessfully)).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"envelope":{"source":"+4915112345678","sourceUuid":"abc","dataMessage":{"message":"/list"}},"account":"+1"}""", "abc", "/list")]
    [InlineData("""{"envelope":{"source":"+4915112345678","dataMessage":{"message":"/list"}},"account":"+1"}""", "+4915112345678", "/list")]
    [InlineData("""{"envelope":{"source":"+4915112345678","sourceUuid":null,"dataMessage":{"message":"hi","groupInfo":null}}}""", "+4915112345678", "hi")]
    public void ParseMessage_ShouldReturnSenderAndTextOfDirectMessages(string json, string expectedSender, string expectedText)
    {
        // Act
        var message = SignalRestClient.ParseMessage(json);

        // Assert
        message.Should().Be(new SignalMessage(expectedSender, expectedText));
    }

    [Theory]
    [InlineData("""{"envelope":{"sourceUuid":"abc","syncMessage":{"sentMessage":{"message":"sent from my phone"}}}}""")]
    [InlineData("""{"envelope":{"sourceUuid":"abc","typingMessage":{"action":"STARTED"}}}""")]
    [InlineData("""{"envelope":{"sourceUuid":"abc","receiptMessage":{"isDelivery":true}}}""")]
    [InlineData("""{"envelope":{"sourceUuid":"abc","dataMessage":{"message":"hi","groupInfo":{"groupId":"g"}}}}""")]
    [InlineData("""{"envelope":{"sourceUuid":"abc","dataMessage":{"message":null,"attachments":[{}]}}}""")]
    [InlineData("""{"envelope":{"sourceUuid":"abc","dataMessage":{"message":"   "}}}""")]
    [InlineData("""{"envelope":{"sourceUuid":"abc","dataMessage":null}}""")]
    [InlineData("""{"envelope":{"dataMessage":{"message":"no sender"}}}""")]
    [InlineData("""{"account":"+1"}""")]
    [InlineData("[]")]
    [InlineData("42")]
    public void ParseMessage_ShouldIgnoreEverythingThatIsNotATextMessageOfAOneToOneChat(string json)
    {
        // Act
        var message = SignalRestClient.ParseMessage(json);

        // Assert
        message.Should().BeNull();
    }

    [Fact]
    public void ParseMessage_ShouldRejectMalformedJson()
    {
        // Act
        var act = () => SignalRestClient.ParseMessage("{not json");

        // Assert
        act.Should().Throw<JsonException>();
    }
}
