using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EbayKleinanzeigenCrawler.Manager
{
    public class TelegramManager : StatefulManagerBase<long>
    {
        private readonly string _telegramBotToken;

        private readonly ITelegramBotClient _botClient;
        private readonly ILogger _logger;

        public TelegramManager(IDataStorage dataStorage, ILogger logger) : base(dataStorage, logger)
        {
            _logger = logger;
            _telegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

            if (string.IsNullOrWhiteSpace(_telegramBotToken))
            {
                throw new InvalidOperationException("Telegram Bot token was not specified. Please first create a bot and specify its token.");
            }

            _botClient = new TelegramBotClient(_telegramBotToken);
            using var cts = new CancellationTokenSource();

            // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>() // receive all update types
            };
            _botClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );
        }

        async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Only process Message updates: https://core.telegram.org/bots/api#message
            if (update.Message is not { } message)
                return;
            // Only process text messages
            if (message.Text is not { } messageText)
                return;

            var clientId = message.Chat.Id;

            ProcessCommand(clientId, messageText);
        }

        Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}", _ => exception.ToString()
            };

            _logger.Error(ErrorMessage);
            return Task.CompletedTask;
        }

        protected override void SendMessage(Subscriber<long> subscriber, string message, bool enablePreview = true)
        {
            SendMessageTelegram(subscriber, message, enablePreview, ParseMode.Html);
        }

        private void SendMessageTelegram(Subscriber<long> subscriber, string message, bool enablePreview, ParseMode parseMode)
        {
            Message result = _botClient.SendTextMessageAsync(
                chatId: subscriber.Id,
                text: message,
                parseMode: parseMode,
                disableWebPagePreview: !enablePreview
            ).Result;

            if (result?.MessageId is null)
            {
                Logger.Error($"Error when sending message to Recipient: { subscriber.Id}, Message: \"{message}\"");
            }
            else
            {
                Logger.Information($"Recipient: {subscriber.Id}, Message: \"{message}\"");  // TODO: Add Admin-Notifications, e.g. for accumulated parsing failures
            }
        }

        protected override void DisplaySubscriptionList(Subscriber<long> subscriber)
        {
            var message = "Your subscriptions:";
            foreach (Subscription subscription in subscriber.Subscriptions)
            {
                message += "\n\n" +
                           $"*Title*: {EscapeMarkdownV2Characters(subscription.Title)} \n" +
                           $"*URL*: {EscapeMarkdownV2Characters(subscription.QueryUrl.ToString())} \n" +
                           $"*Included keywords*: {EscapeMarkdownV2Characters(string.Join(", ", subscription.IncludeKeywords))} \n" +
                           $"*Excluded keywords*: {EscapeMarkdownV2Characters(string.Join(", ", subscription.ExcludeKeywords))} \n" +
                           $"*Enabled*: {subscription.Enabled}";
            }

            SendMessageTelegram(subscriber, message, enablePreview: false, parseMode: ParseMode.MarkdownV2);
        }

        private string EscapeMarkdownV2Characters(string text)
        {
            const string markdownEscapeCharacters = @"([_\*\[\]\(\)~`>#\+\-=\|\{\}\.!])";
            return Regex.Replace(text, markdownEscapeCharacters, @"\$1");
        }
    }
}
