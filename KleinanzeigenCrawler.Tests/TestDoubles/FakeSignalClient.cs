using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Tests.TestDoubles;

/// <summary>
/// Lets tests emit incoming messages and observe or influence outgoing ones without a network
/// </summary>
public sealed class FakeSignalClient : ISignalClient
{
    private readonly TaskCompletionSource _receiveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<SignalMessage> _onMessage;

    public ConcurrentQueue<(string Recipient, string Text)> Sent { get; } = new();
    public ConcurrentDictionary<string, int> SendAttempts { get; } = new();
    public Func<string, string, Task> SendBehavior { get; set; } = (_, _) => Task.CompletedTask;
    public Task ReceiveStarted => _receiveStarted.Task;
    public Task ReceiveCompleted { get; private set; } = Task.CompletedTask;

    public async Task SendAsync(string recipientId, string text, CancellationToken cancellationToken = default)
    {
        SendAttempts.AddOrUpdate(recipientId, 1, (_, attempts) => attempts + 1);
        await SendBehavior(recipientId, text);
        Sent.Enqueue((recipientId, text));
    }

    public Task ReceiveAsync(Action<SignalMessage> onMessage, CancellationToken cancellationToken)
    {
        _onMessage = onMessage;
        _receiveStarted.TrySetResult();
        ReceiveCompleted = WaitUntilCancelledAsync(cancellationToken);
        return ReceiveCompleted;
    }

    public void Emit(string senderId, string text) => _onMessage(new SignalMessage(senderId, text));

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
