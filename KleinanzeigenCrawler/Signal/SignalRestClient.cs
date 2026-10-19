using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;

namespace KleinanzeigenCrawler.Signal;

/// <summary>
/// Talks to a signal-cli-rest-api instance running in json-rpc mode, which pushes incoming messages through a WebSocket
/// </summary>
public sealed class SignalRestClient : ISignalClient, IDisposable
{
    private const string RequiredMode = "json-rpc";
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ILogger _logger;
    private readonly string _number;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly Uri _aboutUri;
    private readonly Uri _accountsUri;
    private readonly Uri _sendUri;
    private readonly Uri _receiveUri;

    public SignalRestClient(SignalConfiguration config, ILogger logger, TimeSpan? initialReconnectDelay = null)
    {
        _logger = logger;
        _number = config.Number;
        _initialReconnectDelay = initialReconnectDelay ?? TimeSpan.FromSeconds(1);

        // The trailing slash keeps a path prefix of the base URL when relative URIs are resolved
        var baseUri = new Uri(config.ApiUrl.TrimEnd('/') + "/");
        _aboutUri = new Uri(baseUri, "v1/about");
        _accountsUri = new Uri(baseUri, "v1/accounts");
        _sendUri = new Uri(baseUri, "v2/send");
        _receiveUri = new UriBuilder(new Uri(baseUri, $"v1/receive/{_number}"))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        }.Uri;
    }

    public async Task SendAsync(string recipientId, string text, CancellationToken cancellationToken = default)
    {
        var request = new { message = text, number = _number, recipients = new[] { recipientId } };
        using var response = await _httpClient.PostAsJsonAsync(_sendUri, request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"The Signal API rejected the message ({(int)response.StatusCode}): {body}", null, response.StatusCode);
    }

    public async Task ReceiveAsync(Action<SignalMessage> onMessage, CancellationToken cancellationToken)
    {
        var delay = _initialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveFromConnectionAsync(onMessage, () => delay = _initialReconnectDelay, cancellationToken);
                _logger.Warning("The Signal API closed the connection. Reconnecting in {Delay}", delay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("Receiving Signal messages failed ({ErrorType}): {Reason} (reconnecting in {Delay})", ex.GetType().Name, ex.Message, delay);
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
        }
    }

    /// <summary>
    /// Extracts the text message of a one-to-one chat from a message pushed by the Signal API.
    /// Returns null for everything else, such as group messages, receipts, typing notifications and messages without text.
    /// </summary>
    public static SignalMessage ParseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("envelope", out var envelope)
            || envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("dataMessage", out var dataMessage)
            || dataMessage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (dataMessage.TryGetProperty("groupInfo", out var groupInfo) && groupInfo.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        // The account ID is stable and also available for senders who hide their phone number
        var sender = GetString(envelope, "sourceUuid") ?? GetString(envelope, "source");
        var text = GetString(dataMessage, "message");
        return string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(text)
            ? null
            : new SignalMessage(sender, text);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task ReceiveFromConnectionAsync(Action<SignalMessage> onMessage, Action onConnected, CancellationToken cancellationToken)
    {
        await EnsureApiIsReadyAsync(cancellationToken);

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(_receiveUri, cancellationToken);
        _logger.Information("Connected to the Signal API");
        onConnected();

        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();
        while (true)
        {
            payload.SetLength(0);
            ValueWebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                payload.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);

            Dispatch(Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length), onMessage);
        }
    }

    private void Dispatch(string json, Action<SignalMessage> onMessage)
    {
        SignalMessage message;
        try
        {
            message = ParseMessage(json);
        }
        catch (JsonException ex)
        {
            _logger.Warning("Ignoring a malformed message of the Signal API: {Reason}", ex.Message);
            return;
        }

        if (message is not null)
        {
            onMessage(message);
        }
    }

    /// <summary>
    /// Fails with an explanation of the misconfiguration, because the API accepts a WebSocket for any number
    /// and then silently never delivers a message
    /// </summary>
    private async Task EnsureApiIsReadyAsync(CancellationToken cancellationToken)
    {
        var about = await _httpClient.GetFromJsonAsync<JsonElement>(_aboutUri, cancellationToken);
        var mode = about.ValueKind == JsonValueKind.Object ? GetString(about, "mode") : null;
        if (mode != RequiredMode)
        {
            throw new InvalidOperationException(
                $"The Signal API runs in mode '{mode ?? "unknown"}', but receiving messages requires mode '{RequiredMode}'. Set MODE={RequiredMode} on the signal-cli-rest-api container.");
        }

        var accounts = await _httpClient.GetFromJsonAsync<string[]>(_accountsUri, cancellationToken);
        if (accounts is null || !accounts.Contains(_number))
        {
            throw new InvalidOperationException(
                $"The number {_number} is not registered with the Signal API. Register or link it first, see the Signal section of the README.");
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
