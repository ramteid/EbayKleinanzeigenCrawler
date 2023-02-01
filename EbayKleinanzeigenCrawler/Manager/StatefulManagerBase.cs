using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KleinanzeigenCrawler.Manager
{
    public abstract class StatefulManagerBase : IOutgoingNotifications
    {
        protected readonly ILogger Logger;
        private readonly ISubscriptionPersistence _subscriptionPersistence;

        protected StatefulManagerBase(ILogger logger, ISubscriptionPersistence subscriptionManager)
        {
            Logger = logger;
            _subscriptionPersistence = subscriptionManager;
        }

        protected abstract void SendMessage(Subscriber subscriber, string message, bool enablePreview = true);

        protected abstract void DisplaySubscriptionList(Subscriber subscriber);

        protected void ProcessCommand(string clientId, string message)
        {
            Logger.Information($"Sender: {clientId}, Message: \"{message}\"");

            Subscriber subscriber = null;
            try
            {
                var subscribers = _subscriptionPersistence.GetSubscribers();
                subscriber = subscribers.SingleOrDefault(s => s.Id.Equals(clientId));
                if (subscriber is null)
                {
                    subscriber = new Subscriber { Id = clientId };
                    _subscriptionPersistence.AddSubscriber(subscriber);
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
            // TODO: Bug: When there are two equal subscriptions with only one having initial results enabled, also the other subscription with initial=false will get the initial results

            List<Subscriber> subscribers = _subscriptionPersistence.GetSubscribers()
                .Where(s => s.Subscriptions.Contains(subscription)).ToList();

            if (subscribers.Count == 0)
            {
                Logger.Error($"Attempted to notify subscribers but found none for subscription {subscription.Id}");
            }

            foreach (Subscriber subscriber in subscribers)
            {
                // As it is possible that multiple subscribers have the same subscription, this subscription could be an equal one from another subscriber
                Subscription exactSubscription = subscriber.Subscriptions.Single(s => s.Equals(subscription) && s.Enabled);

                string message = $"New result: {newResult.Link} \n" +
                                 $"{exactSubscription.Title} - {newResult.CreationDate} - {newResult.Price}";   // TODO: Amend Title of already sent notification to avoid duplicate notifications for two subscriptions
                SendMessage(subscriber, message);
            }
        }

        private void HandleState(Subscriber subscriber, string messageText)
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
                case InputState.Idle when messageText == "/deleteall":
                    {
                        DeleteAllSubscriptions(subscriber);
                        return;
                    }
                case InputState.Idle when messageText == "/delete":  // TODO: add edit mode
                    {
                        StartDeletingSubscription(subscriber);
                        return;
                    }
                case InputState.Idle when messageText == "/enable" || messageText == "/disable":
                    {
                        EnableOrDisableSubscription(subscriber, messageText);
                        return;
                    }
                case not InputState.Idle when messageText == "/cancel":
                    {
                        CancelOperation(subscriber);
                        return;
                    }
                case InputState.WaitingForTitleToDisable or InputState.WaitingForTitleToEnable:
                    {
                        EnableOrDisableSubscriptionTitle(subscriber, messageText);
                        return;
                    }
                case InputState.WaitingForSubscriptionToDelete:
                    {
                        DeleteSubscription(subscriber, messageText);
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

        private void DisplayHelloMessage(Subscriber subscriber)
        {
            SendMessage(subscriber, "Welcome :-) Write /help for available commands");
            subscriber.State = InputState.Idle;
        }
        
        private void DeleteAllSubscriptions(Subscriber subscriber)
        {
            subscriber.Subscriptions.Clear();
            _subscriptionPersistence.SaveData();
            SendMessage(subscriber, "All your subscriptions were deleted");
        }

        private void StartDeletingSubscription(Subscriber subscriber)
        {
            SendMessage(subscriber, "Enter the title of the subscription to delete.\n" +
                                    "Enter /list to view your current subscriptions or /cancel to cancel");
            subscriber.State = InputState.WaitingForSubscriptionToDelete;
        }

        private void DeleteSubscription(Subscriber subscriber, string messageText)
        {
            var subscriptionToDelete = subscriber.Subscriptions.FirstOrDefault(s => s.Title == messageText);

            if (subscriptionToDelete is null)
            {
                SendMessage(subscriber, "No subscription with that title was found. Please try again or /cancel.");
                return;
            }

            subscriber.Subscriptions.Remove(subscriptionToDelete);
            subscriber.State = InputState.Idle;
            _subscriptionPersistence.SaveData();
            SendMessage(subscriber, $"Deleted subscription {subscriptionToDelete.Title}");
        }

        private void EnableOrDisableSubscription(Subscriber subscriber, string messageText)
        {
            if (messageText == "/disable")
            {
                subscriber.State = InputState.WaitingForTitleToDisable;
            }
            else if (messageText == "/enable")
            {
                subscriber.State = InputState.WaitingForTitleToEnable;
            }
            else
            {
                SendMessage(subscriber, "Unknown command");
                return;
            }

            SendMessage(subscriber, "Enter title of the subscription");
        }

        private void EnableOrDisableSubscriptionTitle(Subscriber subscriber, string messageText)
        {
            var subscription = subscriber.Subscriptions.FirstOrDefault(s => s.Title == messageText);

            if (subscription is null)
            {
                SendMessage(subscriber, "No subscription with that title was found. Please try again or /cancel.");
                return;
            }

            string resultMessage = "";
            if (subscriber.State == InputState.WaitingForTitleToDisable)
            {
                subscription.Enabled = false;
                resultMessage = $"Disabled subscription {subscription.Title}";
            }
            else if (subscriber.State == InputState.WaitingForTitleToEnable)
            {
                subscription.Enabled = true;
                resultMessage = $"Enabled subscription {subscription.Title}";
            }

            subscriber.State = InputState.Idle;
            _subscriptionPersistence.SaveData();
            SendMessage(subscriber, resultMessage);
        }

        private void DisplayHelp(Subscriber subscriber)
        {
            const string message = "Write /add to start the process of defining a subscription. \n" +
                                   "Write /delete to delete a subscription. \n" +
                                   "Write /deleteall to delete all your subscriptions. \n" +
                                   "Write /list to view your current subscriptions \n" +
                                   "Write /disable or /enable to disable or enable a subscription \n" +
                                   "At any time, you can write /cancel";
            SendMessage(subscriber, message);
        }

        private void ReloadSubscriberFile(Subscriber subscriber)
        {
            bool restored = _subscriptionPersistence.RestoreData();
            SendMessage(subscriber, restored ? "Subscriber list restored" : "Failed to restore subscriber list");
        }

        private void StartAddingSubscription(Subscriber subscriber)
        {
            SendMessage(subscriber, "Paste the URL of a search page. The URL must contain all search parameters. Use most exact search filters and avoid too many results.");
            SendMessage(subscriber, "Currently, no mobile URLs are supported, please copy the URL from a Desktop browser.'", enablePreview: false); // TODO: Verify URL instead
            subscriber.State = InputState.WaitingForUrl;
        }

        private void AnalyzeInputUrl(string messageText, Subscriber subscriber)
        {
            // TODO: Create proper URL validation in Parser class and call it from here
            if (!messageText.StartsWith("https://www."))
            {
                SendMessage(subscriber, "Please enter a valid URL only");
                return;
            }

            bool isValidUrl = Uri.TryCreate(messageText, UriKind.Absolute, out Uri url);
            if (!isValidUrl)
            {
                throw new InvalidOperationException("This doesn't seem to be a valid URL. Please try again.");
            }

            subscriber.IncompleteSubscription = new Subscription { QueryUrl = url };
            subscriber.State = InputState.WaitingForIncludeKeywords;
            SendMessage(subscriber, "Ok. Enter keywords which must all be included in title or description. Separate by \",\" and define alternatives by \"|\". Or write '/skip'");
            SendMessage(subscriber, "Example: 'one, two|three' will find results where 'one' and 'two' are words in the description; but it will also find results with 'one' and 'three'.");
        }

        private void AnalyzeInputIncludeKeywords(string messageText, Subscriber subscriber)
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

        private void AnalyzeInputExcludeKeywords(string messageText, Subscriber subscriber)
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

        private void AnalyzeInputInitialPull(string messageText, Subscriber subscriber)
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

        private void AnalyzeInputTitle(string messageText, Subscriber subscriber)
        {
            subscriber.IncompleteSubscription.Title = messageText.Trim();
        }

        private void FinalizeSubscription(Subscriber subscriber)
        {
            subscriber.IncompleteSubscription.Enabled = true;
            subscriber.Subscriptions.Add(subscriber.IncompleteSubscription);
            Logger.Information($"Added subscription {subscriber.IncompleteSubscription.Id}");
            subscriber.IncompleteSubscription = null;
            subscriber.State = InputState.Idle;
            _subscriptionPersistence.SaveData();
            SendMessage(subscriber, $"That's it. Added a new subscription for you. You now have {subscriber.Subscriptions.Count} subscriptions.");
            SendMessage(subscriber, "Initially it can take a while until all matches are found. I can only do 40 queries every 5 minutes :-)");
        }

        private void CancelOperation(Subscriber subscriber)
        {
            subscriber.State = InputState.Idle;
            subscriber.IncompleteSubscription = null;
            _subscriptionPersistence.SaveData();
            SendMessage(subscriber, "Cancelled the operation");
        }

        private void DisplayDontUnderstand(Subscriber subscriber)
        {
            SendMessage(subscriber, "I don't understand. Write /help for instructions.");
        }
    }
}