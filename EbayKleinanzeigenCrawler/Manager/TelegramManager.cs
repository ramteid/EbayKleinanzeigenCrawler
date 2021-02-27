using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Manager.Telegram;
using EbayKleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;

namespace EbayKleinanzeigenCrawler.Manager
{
    public class TelegramManager : IOutgoingNotifications, ISubscriptionManager, IDisposable
    {
        private const string TelegramBotToken = ""; // TODO: move to config file

        private readonly IDataStorage _dataStorage;
        private readonly ILogger _logger;
        private readonly ITelegramBotClient _botClient;
        private ConcurrentBag<TelegramSubscriber> _subscriberList;

        public TelegramManager(IDataStorage dataStorage, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(TelegramBotToken))
            {
                throw new InvalidOperationException("Telegram Bot token was not specified. Please first create a bot and specify its token.");
            }

            _dataStorage = dataStorage;
            _logger = logger;
            RestoreData();
            _botClient = new TelegramBotClient(TelegramBotToken);
            _botClient.OnMessage += (_, e) => Bot_OnMessage(e);
            _botClient.StartReceiving();
        }

        private void Bot_OnMessage(MessageEventArgs e)
        {
            string messageText = e.Message.Text;

            if (e.Message.Text is null)
            {
                return;
            }

            long subscriberId = e.Message.Chat.Id;
            _logger.Information($"Sender: {subscriberId}, Message: \"{messageText}\"");

            TelegramSubscriber subscriber = _subscriberList.SingleOrDefault(s => s.Id == subscriberId);
            if (subscriber is null)
            {
                subscriber = new TelegramSubscriber { Id = subscriberId };
                _subscriberList.Add(subscriber);
                SaveData();
            }

            try
            {
                HandleState(subscriber, messageText);
            }
            catch (Exception exception)
            {
                SendMessage(subscriber, $"Error: {exception.Message}"); // TODO: Handle Exceptions from SendMessage()?
            }
        }

        public void NotifySubscribers(Subscription subscription, Result newResult)
        {
            List<TelegramSubscriber> subscribers = _subscriberList.Where(s => s.Subscriptions.Contains(subscription)).ToList();

            if (subscribers.Count == 0)
            {
                _logger.Error($"Attempted to notify subscribers but found none for subscription {subscription.Id}");
            }

            foreach (TelegramSubscriber subscriber in subscribers)
            {
                // As it is possible that multiple subscribers have the same subscription, this subscription could be an equal one from another subscriber
                Subscription exactSubscription = subscriber.Subscriptions.Single(s => s.Equals(subscription) && s.Enabled);

                string message = $"New result: {newResult.Link} \n" +                       // TODO: display price
                                 $"{exactSubscription.Title} - {newResult.CreationDate}";   // TODO: Amend Title of already sent notification to avoid duplicate notifications for two subscriptions
                SendMessage(subscriber, message);
            }
        }

        public List<Subscription> GetDistinctEnabledSubscriptions()
        {
            return _subscriberList
                .SelectMany(s => s.Subscriptions)
                .Where(s => s.Enabled)
                .Distinct()
                .ToList();
        }

        private void SendMessage(TelegramSubscriber subscriber, string message, bool disableWebPagePreview = false, ParseMode parseMode = ParseMode.Default)
        {
            _botClient.SendTextMessageAsync(
                chatId: subscriber.Id,
                text: message,
                parseMode: parseMode,
                disableWebPagePreview: disableWebPagePreview
            ).Wait();
            _logger.Information($"Recipient: {subscriber.Id}, Message: \"{message}\"");  // TODO: Add Admin-Notifications, e.g. for accumulated parsing failures
        }

        private void HandleState(TelegramSubscriber subscriber, string messageText)
        {
            switch (subscriber.State)
            {
                case TelegramInputState.Idle when messageText == "/start":
                    {
                        DisplayHelloMessage(subscriber);
                        break;
                    }
                case TelegramInputState.Idle when messageText == "/add":
                    {
                        StartAddingSubscription(subscriber);
                        return;
                    }
                case TelegramInputState.Idle when messageText == "/delete":  // TODO: add edit mode
                    {
                        DeleteAllSubscriptions(subscriber);
                        return;
                    }
                case TelegramInputState.Idle when messageText == "/list":
                    {
                        DisplaySubscriptionList(subscriber);
                        return;
                    }
                case TelegramInputState.Idle when messageText == "/help":
                    {
                        DisplayHelp(subscriber);
                        return;
                    }
                case TelegramInputState.Idle when messageText == "/reload":
                    {
                        ReloadSubscriberFile(subscriber); // TODO: restrict to admins only
                        return;
                    }
                case not TelegramInputState.Idle when messageText == "/cancel":
                    {
                        CancelOperation(subscriber);
                        return;
                    }
                case TelegramInputState.WaitingForUrl:
                    {
                        AnalyzeInputUrl(messageText, subscriber);
                        return;
                    }
                case TelegramInputState.WaitingForIncludeKeywords:
                    {
                        AnalyzeInputIncludeKeywords(messageText, subscriber);
                        return;
                    }
                case TelegramInputState.WaitingForExcludeKeywords:
                    {
                        AnalyzeInputExcludeKeywords(messageText, subscriber);
                        return;
                    }
                case TelegramInputState.WaitingForInitialPull: // TODO: Add answer buttons
                    {
                        AnalyzeInputInitialPull(messageText, subscriber);
                        return;
                    }
                case TelegramInputState.WaitingForTitle:
                    {
                        AnalyzeInputTitle(messageText, subscriber);
                        FinalizeSubscription(subscriber);
                        return;
                    }
                default:
                    {
                        DisplayDontUnderstand(subscriber);
                        return;
                    }
            }
        }

        private void DisplayHelloMessage(TelegramSubscriber subscriber)
        {
            SendMessage(subscriber, "Welcome :-) Write /help for available commands");
            subscriber.State = TelegramInputState.Idle;
        }

        private void DeleteAllSubscriptions(TelegramSubscriber subscriber)
        {
            subscriber.Subscriptions.Clear();
            SaveData();
            SendMessage(subscriber, "All your subscriptions were deleted");
        }

        private void DisplaySubscriptionList(TelegramSubscriber subscriber)
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

            SendMessage(subscriber, message, disableWebPagePreview: true, parseMode: ParseMode.MarkdownV2);
        }

        private string EscapeMarkdownV2Characters(string text)
        {
            const string markdownEscapeCharacters = @"([_\*\[\]\(\)~`>#\+\-=\|\{\}\.!])";
            return Regex.Replace(text, markdownEscapeCharacters, @"\$1");
        }

        private void DisplayHelp(TelegramSubscriber subscriber)
        {
            const string message = "Write /add to start the process of defining a subscription. \n" +
                                   "Write /delete to delete all your subscriptions. \n" +
                                   "Write /list to view your current subscriptions \n" +
                                   "While you add a new subscription, you can write /cancel";
            SendMessage(subscriber, message);
        }

        private void ReloadSubscriberFile(TelegramSubscriber subscriber)
        {
            bool restored = RestoreData();
            SendMessage(subscriber, restored ? "Subscriber list restored" : "Failed to restore subscriber list");
        }

        private void StartAddingSubscription(TelegramSubscriber subscriber)
        {
            SendMessage(subscriber, "Paste the URL of a Ebay Kleinanzeigen search page. Use most exact search filters and avoid too many results.");
            SendMessage(subscriber, "Currently, no URLs of mobile devices are supported. The URL must begin with 'https://www.ebay-kleinanzeigen.de/....'"); // TODO: Verify URL instead
            subscriber.State = TelegramInputState.WaitingForUrl;
        }

        private void AnalyzeInputUrl(string messageText, TelegramSubscriber subscriber)
        {
            bool validUrl = Uri.TryCreate(messageText, UriKind.Absolute, out Uri url);
            if (!validUrl)  // TODO: Create proper URL validation in Parser class and call it from here
            {
                throw new InvalidOperationException("This doesn't seem to be a valid URL. Please try again.");
            }

            subscriber.IncompleteSubscription = new Subscription { QueryUrl = url };
            subscriber.State = TelegramInputState.WaitingForIncludeKeywords;
            SendMessage(subscriber, "Ok. Enter keywords which must all be included in title or description. Separate by \",\" and define alternatives by \"|\". Or write '/skip'");
            SendMessage(subscriber, "Example: 'one, two|three' will find results where 'one' and 'two' are words in the description; but it will also find results with 'one' and 'three'.");
        }

        private void AnalyzeInputIncludeKeywords(string messageText, TelegramSubscriber subscriber)
        {
            List<string> includeKeywords = messageText
                .Split(",")
                .Select(keyword => keyword.Trim())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Where(keyword => keyword != "/skip")
                .ToList();

            subscriber.IncompleteSubscription.IncludeKeywords = includeKeywords;
            SendMessage(subscriber, $"Ok. I got {includeKeywords.Count} keywords to include.");
            subscriber.State = TelegramInputState.WaitingForExcludeKeywords;
            SendMessage(subscriber, "Enter keywords which must not appear in title or description, separated by \",\" or write '/skip'");
            SendMessage(subscriber, "Hint: If only one of these keywords is found in title or description, there will be no notification.");
        }

        private void AnalyzeInputExcludeKeywords(string messageText, TelegramSubscriber subscriber)
        {
            List<string> excludeKeywords = messageText
                .Split(",")
                .Select(keyword => keyword.Trim())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Where(keyword => keyword != "/skip")
                .ToList();

            subscriber.IncompleteSubscription.ExcludeKeywords = excludeKeywords;
            SendMessage(subscriber, $"Ok. I got {excludeKeywords.Count} keywords to exclude.");
            subscriber.State = TelegramInputState.WaitingForInitialPull;
            SendMessage(subscriber, "Do you want to receive all existing matches initially? (yes/no)");
        }

        private void AnalyzeInputInitialPull(string messageText, TelegramSubscriber subscriber)
        {
            bool initialPull;
            if (string.Equals(messageText.Trim(), "yes", StringComparison.InvariantCultureIgnoreCase))
            {
                initialPull = true;
                SendMessage(subscriber, "Ok. You will get all currently available results.");
            }
            else if (string.Equals(messageText.Trim(), "no", StringComparison.InvariantCultureIgnoreCase))
            {
                initialPull = false;
            }
            else
            {
                DisplayDontUnderstand(subscriber);
                return;
            }

            subscriber.IncompleteSubscription.InitialPull = initialPull;
            subscriber.State = TelegramInputState.WaitingForTitle;
            SendMessage(subscriber, "Choose a unique title for your subscription.");
        }

        private void AnalyzeInputTitle(string messageText, TelegramSubscriber subscriber)
        {
            subscriber.IncompleteSubscription.Title = messageText.Trim();
        }

        private void FinalizeSubscription(TelegramSubscriber subscriber)
        {
            subscriber.IncompleteSubscription.Enabled = true;
            subscriber.Subscriptions.Add(subscriber.IncompleteSubscription);
            subscriber.IncompleteSubscription = null;
            subscriber.State = TelegramInputState.Idle;
            SaveData();
            SendMessage(subscriber, $"That's it. Added a new subscription for you. You now have {subscriber.Subscriptions.Count} subscriptions.");
            SendMessage(subscriber, "Initially it can take a while until all matches are found. I can only do 40 queries every 5 minutes :-)");
        }

        private void CancelOperation(TelegramSubscriber subscriber)
        {
            subscriber.State = TelegramInputState.Idle;
            subscriber.IncompleteSubscription = null;
            SendMessage(subscriber, "Cancelled the operation");
        }

        private void DisplayDontUnderstand(TelegramSubscriber subscriber)
        {
            SendMessage(subscriber, "I don't understand. Write /help for instructions.");
        }

        private bool RestoreData()
        {
            try
            {
                _dataStorage.Load("Subscribers.json", out ConcurrentBag<TelegramSubscriber> data);
                _subscriberList = data;
                _logger.Information($"Restored data: {data.Count} Subscribers");
            }
            catch (Exception e)
            {
                if (_subscriberList is null)
                {
                    _subscriberList = new ConcurrentBag<TelegramSubscriber>();
                }

                if (e is FileNotFoundException)
                {
                    _logger.Warning($"Could not restore subscribers: {e.Message}");
                }
                else
                {
                    _logger.Error(e, $"Error when restoring subscribers: {e.Message}");
                }

                return false;
            }

            return true;
        }

        private void SaveData()
        {
            _dataStorage.Save(_subscriberList, "Subscribers.json");
        }

        public void Dispose()
        {
            _botClient.StopReceiving();
        }
    }
}
