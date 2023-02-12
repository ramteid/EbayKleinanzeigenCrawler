using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EbayKleinanzeigenCrawler.Manager;

public class TelegramManager : StatefulManagerBase
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger _logger;

    public TelegramManager(ILogger logger, ISubscriptionPersistence subscriptionManager) : base(logger, subscriptionManager)
    {
        var telegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(telegramBotToken))
        {
            throw new InvalidOperationException("Telegram Bot token was not specified. Please first create a bot and specify its token.");
        }
        _botClient = new TelegramBotClient(telegramBotToken);

        _logger = logger;

        // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>() // receive all update types
            },
            cancellationToken: new CancellationTokenSource().Token
        );
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // Only process Message updates: https://core.telegram.org/bots/api#message
        if (update.Message is not { } message)
        {
            return;
        }

        // Only process text messages
        if (message.Text is not { } messageText)
        {
            return;
        }

        var clientId = message.Chat.Id.ToString();

        await ProcessCommand(clientId, messageText);
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken __)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}", 
            _ => exception.ToString()
        };

        _logger.Error(errorMessage);
        return Task.CompletedTask;
    }

    protected override async Task SendMessage(Subscriber subscriber, string message, bool enablePreview = true)
    {
        await SendMessageTelegram(subscriber, message, enablePreview, ParseMode.Html);
    }

    private async Task SendMessageTelegram(Subscriber subscriber, string message, bool enablePreview, ParseMode parseMode)
    {
        var result = await _botClient.SendTextMessageAsync(
            chatId: subscriber.Id,
            text: message,
            parseMode: parseMode,
            disableWebPagePreview: !enablePreview
        );

        Logger.Information($"Recipient: {subscriber.Id}, Message: \"{message}\"");
    }

    protected override async Task DisplaySubscriptionList(Subscriber subscriber)
    {
        var message = "Your subscriptions:";
        foreach (var subscription in subscriber.Subscriptions)
        {
            message += "\n\n" +
                       $"*Title*: {EscapeMarkdownV2Characters(subscription.Title)} \n" +
                       $"*URL*: {EscapeMarkdownV2Characters(subscription.QueryUrl.ToString())} \n" +
                       $"*Included keywords*: {EscapeMarkdownV2Characters(string.Join(", ", subscription.IncludeKeywords))} \n" +
                       $"*Excluded keywords*: {EscapeMarkdownV2Characters(string.Join(", ", subscription.ExcludeKeywords))} \n" +
                       $"*Enabled*: {subscription.Enabled}";
        }

        await SendMessageTelegram(subscriber, message, enablePreview: false, parseMode: ParseMode.MarkdownV2);
    }

    private string EscapeMarkdownV2Characters(string text)
    {
        const string markdownEscapeCharacters = @"([_\*\[\]\(\)~`>#\+\-=\|\{\}\.!])";
        return Regex.Replace(text, markdownEscapeCharacters, @"\$1");
    }
}