using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EbayKleinanzeigenCrawler.Manager
{
    public abstract class StatefulManagerBase<TId> : IOutgoingNotifications, ISubscriptionManager
    {
        protected readonly IDataStorage DataStorage;
        protected readonly ILogger Logger;
        protected ConcurrentBag<Subscriber<TId>> SubscriberList { get; set; }

        protected StatefulManagerBase(IDataStorage dataStorage, ILogger logger)
        {
            Directory.CreateDirectory("data");
            
            DataStorage = dataStorage;
            Logger = logger;
            SubscriberList = new ConcurrentBag<Subscriber<TId>>();
            RestoreData();
        }

        protected abstract void SendMessage(Subscriber<TId> subscriber, string message);

        protected abstract void DisplaySubscriptionList(Subscriber<TId> subscriber);

        protected void ProcessCommand(TId clientId, string message)
        {
            Logger.Information($"Sender: {clientId}, Message: \"{message}\"");

            Subscriber<TId> subscriber = null;
            try
            {
                subscriber = SubscriberList.SingleOrDefault(s => s.Id.Equals(clientId));
                if (subscriber is null)
                {
                    subscriber = new Subscriber<TId> { Id = clientId };
                    SubscriberList.Add(subscriber);
                    SaveData();
                }

                HandleState(subscriber, message);
            }
            catch (Exception exception)
            {
                if (subscriber is not null)
                {
                    SendMessage(subscriber, $"Error: {exception.Message}"); // TODO: Handle Exceptions from SendMessage()?
                }
                else
                {
                    Logger.Error(exception, $"Error: { exception.Message}");
                }
            }
        }

        public void NotifySubscribers(Subscription subscription, Result newResult)
        {
            List<Subscriber<TId>> subscribers = SubscriberList.Where(s => s.Subscriptions.Contains(subscription)).ToList();

            if (subscribers.Count == 0)
            {
                Logger.Error($"Attempted to notify subscribers but found none for subscription {subscription.Id}");
            }

            foreach (Subscriber<TId> subscriber in subscribers)
            {
                // As it is possible that multiple subscribers have the same subscription, this subscription could be an equal one from another subscriber
                Subscription exactSubscription = subscriber.Subscriptions.Single(s => s.Equals(subscription) && s.Enabled);

                string message = $"New result: {newResult.Link} \n" +                       // TODO: display price
                                 $"{exactSubscription.Title} - {newResult.CreationDate} - {newResult.Price}";   // TODO: Amend Title of already sent notification to avoid duplicate notifications for two subscriptions
                SendMessage(subscriber, message);
            }
        }

        public List<Subscription> GetDistinctEnabledSubscriptions()
        {
            return SubscriberList
                .SelectMany(s => s.Subscriptions)
                .Where(s => s.Enabled)
                .Distinct()
                .ToList();
        }

        private void HandleState(Subscriber<TId> subscriber, string messageText)
        {
            switch (subscriber.State)
            {
                case InputState.Idle when messageText == "/start":
                    {
                        DisplayHelloMessage(subscriber);
                        break;
                    }
                case InputState.Idle when messageText == "/add":
                    {
                        StartAddingSubscription(subscriber);
                        return;
                    }
                case InputState.Idle when messageText == "/delete":  // TODO: add edit mode
                    {
                        DeleteAllSubscriptions(subscriber);
                        return;
                    }
                case InputState.Idle when messageText == "/list":
                    {
                        DisplaySubscriptionList(subscriber);
                        return;
                    }
                case InputState.Idle when messageText == "/help":
                    {
                        DisplayHelp(subscriber);
                        return;
                    }
                case InputState.Idle when messageText == "/reload":
                    {
                        ReloadSubscriberFile(subscriber); // TODO: restrict to admins only
                        return;
                    }
                case not InputState.Idle when messageText == "/cancel":
                    {
                        CancelOperation(subscriber);
                        return;
                    }
                case InputState.WaitingForUrl:
                    {
                        AnalyzeInputUrl(messageText, subscriber);
                        return;
                    }
                case InputState.WaitingForIncludeKeywords:
                    {
                        AnalyzeInputIncludeKeywords(messageText, subscriber);
                        return;
                    }
                case InputState.WaitingForExcludeKeywords:
                    {
                        AnalyzeInputExcludeKeywords(messageText, subscriber);
                        return;
                    }
                case InputState.WaitingForInitialPull: // TODO: Add answer buttons
                    {
                        AnalyzeInputInitialPull(messageText, subscriber);
                        return;
                    }
                case InputState.WaitingForTitle:
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

        private void DisplayHelloMessage(Subscriber<TId> subscriber)
        {
            SendMessage(subscriber, "Welcome :-) Write /help for available commands");
            subscriber.State = InputState.Idle;
        }

        private void DeleteAllSubscriptions(Subscriber<TId> subscriber)
        {
            subscriber.Subscriptions.Clear();
            SaveData();
            SendMessage(subscriber, "All your subscriptions were deleted");
        }

        private void DisplayHelp(Subscriber<TId> subscriber)
        {
            const string message = "Write /add to start the process of defining a subscription. \n" +
                                   "Write /delete to delete all your subscriptions. \n" +
                                   "Write /list to view your current subscriptions \n" +
                                   "While you add a new subscription, you can write /cancel";
            SendMessage(subscriber, message);
        }

        private void ReloadSubscriberFile(Subscriber<TId> subscriber)
        {
            bool restored = RestoreData();
            SendMessage(subscriber, restored ? "Subscriber list restored" : "Failed to restore subscriber list");
        }

        private void StartAddingSubscription(Subscriber<TId> subscriber)
        {
            SendMessage(subscriber, "Paste the URL of a Ebay Kleinanzeigen search page. Use most exact search filters and avoid too many results.");
            SendMessage(subscriber, "Currently, no URLs of mobile devices are supported. The URL must begin with 'https://www.ebay-kleinanzeigen.de/....'"); // TODO: Verify URL instead
            subscriber.State = InputState.WaitingForUrl;
        }

        private void AnalyzeInputUrl(string messageText, Subscriber<TId> subscriber)
        {
            bool validUrl = Uri.TryCreate(messageText, UriKind.Absolute, out Uri url);
            if (!validUrl)  // TODO: Create proper URL validation in Parser class and call it from here
            {
                throw new InvalidOperationException("This doesn't seem to be a valid URL. Please try again.");
            }

            subscriber.IncompleteSubscription = new Subscription { QueryUrl = url };
            subscriber.State = InputState.WaitingForIncludeKeywords;
            SendMessage(subscriber, "Ok. Enter keywords which must all be included in title or description. Separate by \",\" and define alternatives by \"|\". Or write '/skip'");
            SendMessage(subscriber, "Example: 'one, two|three' will find results where 'one' and 'two' are words in the description; but it will also find results with 'one' and 'three'.");
        }

        private void AnalyzeInputIncludeKeywords(string messageText, Subscriber<TId> subscriber)
        {
            List<string> includeKeywords = messageText
                .Split(",")
                .Select(keyword => keyword.Trim())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Where(keyword => keyword != "/skip")
                .ToList();

            subscriber.IncompleteSubscription.IncludeKeywords = includeKeywords;
            SendMessage(subscriber, $"Ok. I got {includeKeywords.Count} keywords to include.");
            subscriber.State = InputState.WaitingForExcludeKeywords;
            SendMessage(subscriber, "Enter keywords which must not appear in title or description, separated by \",\" or write '/skip'");
            SendMessage(subscriber, "Hint: If only one of these keywords is found in title or description, there will be no notification.");
        }

        private void AnalyzeInputExcludeKeywords(string messageText, Subscriber<TId> subscriber)
        {
            List<string> excludeKeywords = messageText
                .Split(",")
                .Select(keyword => keyword.Trim())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Where(keyword => keyword != "/skip")
                .ToList();

            subscriber.IncompleteSubscription.ExcludeKeywords = excludeKeywords;
            SendMessage(subscriber, $"Ok. I got {excludeKeywords.Count} keywords to exclude.");
            subscriber.State = InputState.WaitingForInitialPull;
            SendMessage(subscriber, "Do you want to receive all existing matches initially? (yes/no)");
        }

        private void AnalyzeInputInitialPull(string messageText, Subscriber<TId> subscriber)
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
            subscriber.State = InputState.WaitingForTitle;
            SendMessage(subscriber, "Choose a unique title for your subscription.");
        }

        private void AnalyzeInputTitle(string messageText, Subscriber<TId> subscriber)
        {
            subscriber.IncompleteSubscription.Title = messageText.Trim();
        }

        private void FinalizeSubscription(Subscriber<TId> subscriber)
        {
            subscriber.IncompleteSubscription.Enabled = true;
            subscriber.Subscriptions.Add(subscriber.IncompleteSubscription);
            Logger.Information($"Added subscription {subscriber.IncompleteSubscription.Id}");
            subscriber.IncompleteSubscription = null;
            subscriber.State = InputState.Idle;
            SaveData();
            SendMessage(subscriber, $"That's it. Added a new subscription for you. You now have {subscriber.Subscriptions.Count} subscriptions.");
            SendMessage(subscriber, "Initially it can take a while until all matches are found. I can only do 40 queries every 5 minutes :-)");
        }

        private void CancelOperation(Subscriber<TId> subscriber)
        {
            subscriber.State = InputState.Idle;
            subscriber.IncompleteSubscription = null;
            SendMessage(subscriber, "Cancelled the operation");
        }

        private void DisplayDontUnderstand(Subscriber<TId> subscriber)
        {
            SendMessage(subscriber, "I don't understand. Write /help for instructions.");
        }

        private bool RestoreData()
        {
            try
            {
                DataStorage.Load(Path.Join("data", "Subscribers.json"), out ConcurrentBag<Subscriber<TId>> data);
                SubscriberList = data;
                Logger.Information($"Restored data: {data.Count} Subscribers");
            }
            catch (Exception e)
            {
                SubscriberList ??= new ConcurrentBag<Subscriber<TId>>();

                if (e is FileNotFoundException)
                {
                    Logger.Warning($"Could not restore subscribers: {e.Message}");
                }
                else
                {
                    Logger.Error(e, $"Error when restoring subscribers: {e.Message}");
                }

                return false;
            }

            return true;
        }

        private void SaveData()
        {
            DataStorage.Save(SubscriberList, Path.Join("data", "Subscribers.json"));
        }
    }
}