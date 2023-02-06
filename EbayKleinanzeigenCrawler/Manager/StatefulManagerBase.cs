using System;
using System.Linq;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;

namespace EbayKleinanzeigenCrawler.Manager;

public abstract class StatefulManagerBase : IOutgoingNotifications
{
    protected readonly ILogger Logger;
    private readonly ISubscriptionPersistence _subscriptionPersistence;

    protected StatefulManagerBase(ILogger logger, ISubscriptionPersistence subscriptionManager)
    {
        Logger = logger;
        _subscriptionPersistence = subscriptionManager;
    }

    protected abstract Task SendMessage(Subscriber subscriber, string message, bool enablePreview = true);

    protected abstract Task DisplaySubscriptionList(Subscriber subscriber);

    protected async Task ProcessCommand(string clientId, string message)
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

            await HandleState(subscriber, message);
        }
        catch (Exception exception)
        {
            if (subscriber is not null)
            {
                await SendMessage(subscriber, $"Error: {exception.Message}"); // TODO: Handle Exceptions from SendMessage()?
            }
            else
            {
                Logger.Error(exception, $"Error: { exception.Message}");
            }
        }
    }

    public async Task NotifySubscribers(Subscription subscription, Result newResult)
    {
        // TODO: Bug: When there are two equal subscriptions with only one having initial results enabled, also the other subscription with initial=false will get the initial results

        var subscribers = _subscriptionPersistence.GetSubscribers()
            .Where(s => s.Subscriptions.Contains(subscription)).ToList();

        if (subscribers.Count == 0)
        {
            Logger.Error($"Attempted to notify subscribers but found none for subscription {subscription.Id}");
        }

        foreach (var subscriber in subscribers)
        {
            // As it is possible that multiple subscribers have the same subscription, this subscription could be an equal one from another subscriber
            var exactSubscription = subscriber.Subscriptions.Single(s => s.Equals(subscription) && s.Enabled);

            var message = $"New result: {newResult.Link} \n" +
                          $"{exactSubscription.Title} - {newResult.CreationDate} - {newResult.Price}";   // TODO: Amend Title of already sent notification to avoid duplicate notifications for two subscriptions
            await SendMessage(subscriber, message);
        }
    }

    private async Task HandleState(Subscriber subscriber, string messageText)
    {
        switch (subscriber.State)
        {
            case InputState.Idle when messageText == "/start":
            {
                await DisplayHelloMessage(subscriber);
                break;
            }
            case InputState.Idle when messageText == "/add":
            {
                await StartAddingSubscription(subscriber);
                return;
            }
            case InputState.Idle when messageText == "/list":
            {
                await DisplaySubscriptionList(subscriber);
                return;
            }
            case InputState.Idle when messageText == "/help":
            {
                await DisplayHelp(subscriber);
                return;
            }
            case InputState.Idle when messageText == "/reload":
            {
                await ReloadSubscriberFile(subscriber); // TODO: restrict to admins only
                return;
            }
            case InputState.Idle when messageText == "/deleteall":
            {
                await DeleteAllSubscriptions(subscriber);
                return;
            }
            case InputState.Idle when messageText == "/delete":  // TODO: add edit mode
            {
                await StartDeletingSubscription(subscriber);
                return;
            }
            case InputState.Idle when messageText is "/enable" or "/disable":
            {
                await EnableOrDisableSubscription(subscriber, messageText);
                return;
            }
            case not InputState.Idle when messageText == "/cancel":
            {
                await CancelOperation(subscriber);
                return;
            }
            case InputState.WaitingForTitleToDisable or InputState.WaitingForTitleToEnable:
            {
                await EnableOrDisableSubscriptionTitle(subscriber, messageText);
                return;
            }
            case InputState.WaitingForSubscriptionToDelete:
            {
                await DeleteSubscription(subscriber, messageText);
                return;
            }
            case InputState.WaitingForUrl:
            {
                await AnalyzeInputUrl(messageText, subscriber);
                return;
            }
            case InputState.WaitingForIncludeKeywords:
            {
                await AnalyzeInputIncludeKeywords(messageText, subscriber);
                return;
            }
            case InputState.WaitingForExcludeKeywords:
            {
                await AnalyzeInputExcludeKeywords(messageText, subscriber);
                return;
            }
            case InputState.WaitingForInitialPull: // TODO: Add answer buttons
            {
                await AnalyzeInputInitialPull(messageText, subscriber);
                return;
            }
            case InputState.WaitingForTitle:
            {
                AnalyzeInputTitle(messageText, subscriber);
                await FinalizeSubscription(subscriber);
                return;
            }
            default:
            {
                await DisplayDontUnderstand(subscriber);
                return;
            }
        }
    }

    private async Task DisplayHelloMessage(Subscriber subscriber)
    {
        await SendMessage(subscriber, "Welcome :-) Write /help for available commands");
        subscriber.State = InputState.Idle;
    }
        
    private async Task DeleteAllSubscriptions(Subscriber subscriber)
    {
        subscriber.Subscriptions.Clear();
        _subscriptionPersistence.SaveData();
        await SendMessage(subscriber, "All your subscriptions were deleted");
    }

    private async Task StartDeletingSubscription(Subscriber subscriber)
    {
        await SendMessage(subscriber, "Enter the title of the subscription to delete.\n" +
                                      "Enter /list to view your current subscriptions or /cancel to cancel");
        subscriber.State = InputState.WaitingForSubscriptionToDelete;
    }

    private async Task DeleteSubscription(Subscriber subscriber, string messageText)
    {
        var subscriptionToDelete = subscriber.Subscriptions.FirstOrDefault(s => s.Title == messageText);

        if (subscriptionToDelete is null)
        {
            await SendMessage(subscriber, "No subscription with that title was found. Please try again or /cancel.");
            return;
        }

        subscriber.Subscriptions.Remove(subscriptionToDelete);
        subscriber.State = InputState.Idle;
        _subscriptionPersistence.SaveData();
        await SendMessage(subscriber, $"Deleted subscription {subscriptionToDelete.Title}");
    }

    private async Task EnableOrDisableSubscription(Subscriber subscriber, string messageText)
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
            await SendMessage(subscriber, "Unknown command");
            return;
        }

        await SendMessage(subscriber, "Enter title of the subscription");
    }

    private async Task EnableOrDisableSubscriptionTitle(Subscriber subscriber, string messageText)
    {
        var subscription = subscriber.Subscriptions.FirstOrDefault(s => s.Title == messageText);

        if (subscription is null)
        {
            await SendMessage(subscriber, "No subscription with that title was found. Please try again or /cancel.");
            return;
        }

        var resultMessage = "";
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
        await SendMessage(subscriber, resultMessage);
    }

    private async Task DisplayHelp(Subscriber subscriber)
    {
        const string message = "Write /add to start the process of defining a subscription. \n" +
                               "Write /delete to delete a single subscription. \n" +
                               "Write /deleteall to delete all your subscriptions. \n" +
                               "Write /list to view your current subscriptions \n" +
                               "Write /disable or /enable to disable or enable a subscription \n" +
                               "At any time, you can write /cancel";
        await SendMessage(subscriber, message);
    }

    private async Task ReloadSubscriberFile(Subscriber subscriber)
    {
        var restored = _subscriptionPersistence.RestoreData();
        await SendMessage(subscriber, restored ? "Subscriber list restored" : "Failed to restore subscriber list");
    }

    private async Task StartAddingSubscription(Subscriber subscriber)
    {
        await SendMessage(subscriber, "Paste the URL of a search page. The URL must contain all search parameters. Use most exact search filters and avoid too many results.");
        await SendMessage(subscriber, "Currently, no mobile URLs are supported, please copy the URL from a Desktop browser.'"); // TODO: Verify URL instead
        subscriber.State = InputState.WaitingForUrl;
    }

    private async Task AnalyzeInputUrl(string messageText, Subscriber subscriber)
    {
        // TODO: Create proper URL validation in Parser class and call it from here
        if (!messageText.StartsWith("https://www."))
        {
            await SendMessage(subscriber, "Please enter a valid URL only");
            return;
        }

        var isValidUrl = Uri.TryCreate(messageText, UriKind.Absolute, out var url);
        if (!isValidUrl)
        {
            throw new InvalidOperationException("This doesn't seem to be a valid URL. Please try again.");
        }

        subscriber.IncompleteSubscription = new Subscription { QueryUrl = url };
        subscriber.State = InputState.WaitingForIncludeKeywords;
        await SendMessage(subscriber, "Ok. Enter keywords which must all be included in title or description. Separate by \",\" and define alternatives by \"|\". Or write '/skip'");
        await SendMessage(subscriber, "Example: 'one, two|three' will find results where 'one' and 'two' are words in the description; but it will also find results with 'one' and 'three'.");
    }

    private async Task AnalyzeInputIncludeKeywords(string messageText, Subscriber subscriber)
    {
        var includeKeywords = messageText
            .Split(",")
            .Select(keyword => keyword.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Where(keyword => keyword != "/skip")
            .ToList();

        subscriber.IncompleteSubscription.IncludeKeywords = includeKeywords;
        await SendMessage(subscriber, $"Ok. I got {includeKeywords.Count} keywords to include.");
        subscriber.State = InputState.WaitingForExcludeKeywords;
        await SendMessage(subscriber, "Enter keywords which must not appear in title or description, separated by \",\" or write '/skip'");
        await SendMessage(subscriber, "Hint: If only one of these keywords is found in title or description, there will be no notification.");
    }

    private async Task AnalyzeInputExcludeKeywords(string messageText, Subscriber subscriber)
    {
        var excludeKeywords = messageText
            .Split(",")
            .Select(keyword => keyword.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Where(keyword => keyword != "/skip")
            .ToList();

        subscriber.IncompleteSubscription.ExcludeKeywords = excludeKeywords;
        await SendMessage(subscriber, $"Ok. I got {excludeKeywords.Count} keywords to exclude.");
        subscriber.State = InputState.WaitingForInitialPull;
        await SendMessage(subscriber, "Do you want to receive all existing matches initially? (yes/no)");
    }

    private async Task AnalyzeInputInitialPull(string messageText, Subscriber subscriber)
    {
        bool initialPull;
        if (string.Equals(messageText.Trim(), "yes", StringComparison.InvariantCultureIgnoreCase))
        {
            initialPull = true;
            await SendMessage(subscriber, "Ok. You will get all currently available results.");
        }
        else if (string.Equals(messageText.Trim(), "no", StringComparison.InvariantCultureIgnoreCase))
        {
            initialPull = false;
        }
        else
        {
            await DisplayDontUnderstand(subscriber);
            return;
        }

        subscriber.IncompleteSubscription.InitialPull = initialPull;
        subscriber.State = InputState.WaitingForTitle;
        await SendMessage(subscriber, "Choose a unique title for your subscription.");
    }

    private void AnalyzeInputTitle(string messageText, Subscriber subscriber)
    {
        subscriber.IncompleteSubscription.Title = messageText.Trim();
    }

    private async Task FinalizeSubscription(Subscriber subscriber)
    {
        subscriber.IncompleteSubscription.Enabled = true;
        subscriber.Subscriptions.Add(subscriber.IncompleteSubscription);
        Logger.Information($"Added subscription {subscriber.IncompleteSubscription.Id}");
        subscriber.IncompleteSubscription = null;
        subscriber.State = InputState.Idle;
        _subscriptionPersistence.SaveData();
        await SendMessage(subscriber, $"That's it. Added a new subscription for you. You now have {subscriber.Subscriptions.Count} subscriptions.");
        await SendMessage(subscriber, "Initially it can take a while until all matches are found. I can only do 40 queries every 5 minutes :-)");
    }

    private async Task CancelOperation(Subscriber subscriber)
    {
        subscriber.State = InputState.Idle;
        subscriber.IncompleteSubscription = null;
        _subscriptionPersistence.SaveData();
        await SendMessage(subscriber, "Cancelled the operation");
    }

    private async Task DisplayDontUnderstand(Subscriber subscriber)
    {
        await SendMessage(subscriber, "I don't understand. Write /help for instructions.");
    }
}