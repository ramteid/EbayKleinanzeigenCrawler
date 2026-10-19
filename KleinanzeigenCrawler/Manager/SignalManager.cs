using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Monitoring;
using Serilog;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Signal manager with a text-based UI. Signal has no bot API and no buttons, so the conversation consists of step-by-step prompts.
/// </summary>
public sealed class SignalManager : IMessenger
{
    private const int MaxMessageLength = 2000;
    private const int MaxSendAttempts = 3;
    private const string UnexpectedErrorReply = "Something went wrong. Send /cancel and try again.";

    private readonly ISignalClient _client;
    private readonly ILogger _logger;
    private readonly MetricsService _metricsService;
    private readonly SessionManager _sessionManager;
    private readonly SubscriptionService _subscriptionService;
    private readonly TextConversationHandler _conversation;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userGates = new();

    public MessengerChannel Channel => MessengerChannel.Signal;

    public SignalManager(
        ISignalClient client,
        ILogger logger,
        ISubscriptionPersistence subscriptionPersistence,
        IAccessAttemptPersistence accessAttemptPersistence,
        IParserProvider parserProvider,
        MessagingConfiguration messaging,
        MetricsService metricsService = null)
    {
        _client = client;
        _logger = logger;
        _metricsService = metricsService;
        _sessionManager = new SessionManager(logger, TimeSpan.FromMinutes(messaging.SessionTimeoutMinutes));
        _subscriptionService = new SubscriptionService(subscriptionPersistence, parserProvider, logger, Channel);
        var accessGate = new AccessGate(_subscriptionService, accessAttemptPersistence, messaging, logger);
        _conversation = new TextConversationHandler(_subscriptionService, accessGate, _sessionManager);
    }

    public void Start()
    {
        _ = Task.Run(RunReceiveLoopAsync);
        _logger.Information("Signal bot started with text-based UI");
    }

    public async Task<DeliveryReport> NotifyAsync(Subscription subscription, Result newResult)
    {
        var recipients = _subscriptionService.GetRecipients(subscription);
        if (recipients.Count == 0)
        {
            _logger.Debug("No {Channel} recipients found for subscription {SubscriptionId}", Channel, subscription.Id);
            return DeliveryReport.NoRecipients;
        }

        var message = PlainTextFormatter.NewResult(subscription, newResult);
        var delivered = 0;
        var transientFailures = new List<Exception>();
        foreach (var recipient in recipients)
        {
            var failure = await TrySendAsync(recipient.Id, message);
            _metricsService?.RecordNotification(Channel, failure is null);
            if (failure is null)
            {
                delivered++;
            }
            else if (IsTransient(failure) && !_cancellation.IsCancellationRequested)
            {
                transientFailures.Add(failure);
            }
        }

        return new DeliveryReport(delivered, transientFailures);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _sessionManager.Dispose();
    }

    private async Task RunReceiveLoopAsync()
    {
        try
        {
            await _client.ReceiveAsync(message => _ = HandleMessageAsync(message), _cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Receiving Signal messages stopped unexpectedly");
        }
    }

    /// <summary>
    /// Handles the messages of one user one after another, while the conversations of different users do not wait for each other.
    /// Creating a subscription may take minutes when the crawler has used up its request budget.
    /// </summary>
    private async Task HandleMessageAsync(SignalMessage message)
    {
        var gate = _userGates.GetOrAdd(message.SenderId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(_cancellation.Token);
            try
            {
                _logger.Information("Signal message from {Sender}", message.SenderId);
                var reply = await _conversation.HandleMessageAsync(message.SenderId, message.Text);
                if (reply is not null)
                {
                    await TrySendAsync(message.SenderId, reply);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Handling a Signal message from {Sender} failed", message.SenderId);
            await TrySendAsync(message.SenderId, UnexpectedErrorReply);
        }
    }

    /// <returns>The reason why the text could not be delivered, or null if it was delivered</returns>
    private async Task<Exception> TrySendAsync(string recipientId, string text)
    {
        try
        {
            foreach (var chunk in TextChunker.Split(text, MaxMessageLength))
            {
                await DeliveryRetry.ExecuteAsync(
                    () => _client.SendAsync(recipientId, chunk, _cancellation.Token),
                    SelectRetryDelay,
                    MaxSendAttempts,
                    (ex, delay, attempt) => _logger.Warning(
                        "Sending a Signal message to {Recipient} failed: {Reason} (retrying in {Delay}, attempt {Attempt} of {MaxAttempts})",
                        recipientId, ex.Message, delay, attempt + 1, MaxSendAttempts),
                    _cancellation.Token);
            }

            return null;
        }
        catch (OperationCanceledException ex) when (_cancellation.IsCancellationRequested)
        {
            return ex;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error sending a Signal message to {Recipient}", recipientId);
            return ex;
        }
    }

    // An unreachable or overloaded API and timeouts may go away. A message that the API rejected will be rejected again.
    private static bool IsTransient(Exception exception) => exception is
        HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError } or TaskCanceledException;

    private static TimeSpan? SelectRetryDelay(Exception exception, int attempt) =>
        IsTransient(exception) ? TimeSpan.FromSeconds(attempt) : null;
}
