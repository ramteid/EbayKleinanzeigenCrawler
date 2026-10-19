using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace KleinanzeigenCrawler.Tests.TestDoubles;

/// <summary>
/// An in-process imitation of the endpoints of signal-cli-rest-api that the crawler uses
/// </summary>
public sealed class FakeSignalApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<int, WebSocket> _sockets = new();
    private int _nextSocketId;
    private int _connectionCount;
    private int _aboutRequestCount;
    private int _accountsRequestCount;

    private FakeSignalApi(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseUri { get; private set; }
    public string Mode { get; set; } = "json-rpc";
    public string[] Accounts { get; set; } = Array.Empty<string>();
    public HttpStatusCode SendStatusCode { get; set; } = HttpStatusCode.Created;
    public string SendResponseBody { get; set; } = "{\"timestamp\":\"1\"}";
    public ConcurrentQueue<string> SentRequests { get; } = new();
    public ConcurrentQueue<string> ReceivePaths { get; } = new();
    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public int AboutRequestCount => Volatile.Read(ref _aboutRequestCount);
    public int AccountsRequestCount => Volatile.Read(ref _accountsRequestCount);

    public static async Task<FakeSignalApi> StartAsync(params string[] registeredNumbers)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var api = new FakeSignalApi(app) { Accounts = registeredNumbers };

        app.UseWebSockets();
        app.MapGet("/v1/about", api.HandleAboutAsync);
        app.MapGet("/v1/accounts", api.HandleAccountsAsync);
        app.MapPost("/v2/send", api.HandleSendAsync);
        app.Map("/v1/receive/{number}", api.HandleReceiveAsync);

        await app.StartAsync();
        api.BaseUri = new Uri(app.Urls.First());
        return api;
    }

    public async Task<bool> WaitForConnectionsAsync(int count)
    {
        return await Wait.UntilAsync(() => ConnectionCount >= count);
    }

    public async Task PushAsync(string json)
    {
        foreach (var socket in _sockets.Values)
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    public async Task CloseConnectionsAsync()
    {
        foreach (var socket in _sockets.Values)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
    }

    public void AbortConnections()
    {
        foreach (var socket in _sockets.Values)
        {
            socket.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAboutAsync(HttpContext context)
    {
        Interlocked.Increment(ref _aboutRequestCount);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"build\":1,\"mode\":\"{Mode}\",\"version\":\"0.0.0\",\"versions\":[\"v1\",\"v2\"],\"capabilities\":{{}}}}");
    }

    private async Task HandleAccountsAsync(HttpContext context)
    {
        Interlocked.Increment(ref _accountsRequestCount);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(Accounts));
    }

    private async Task HandleSendAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        SentRequests.Enqueue(await reader.ReadToEndAsync());
        context.Response.StatusCode = (int)SendStatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(SendResponseBody);
    }

    private async Task HandleReceiveAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        ReceivePaths.Enqueue(context.Request.Path.Value);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Interlocked.Increment(ref _nextSocketId);
        _sockets[id] = socket;
        Interlocked.Increment(ref _connectionCount);
        try
        {
            // Reading keeps the connection open and processes the control frames of the client
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // The client went away or the connection was aborted
        }
        finally
        {
            _sockets.TryRemove(id, out _);
        }
    }
}
