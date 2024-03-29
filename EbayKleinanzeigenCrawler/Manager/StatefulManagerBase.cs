﻿using System;
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
    private readonly IParserProvider _parserProvider;

    protected StatefulManagerBase(ILogger logger, ISubscriptionPersistence subscriptionManager, IParserProvider parserProvider)
    {
        Logger = logger;
        _subscriptionPersistence = subscriptionManager;
        _parserProvider = parserProvider;
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
                subscriber = new Subscriber 
                {
                    Id = clientId,
                    IsAdmin = false  // admin propery has to be set manually in Subscribers.json
                };
                _subscriptionPersistence.AddSubscriber(subscriber);
            }

            await HandleState(subscriber, message);
        }
        catch (Exception exception)
        {
            if (subscriber is not null)
            {
                await SendMessage(subscriber, $"Error: {exception.Message}");
            }
            else
            {
                Logger.Error(exception, $"Error: { exception.Message}");
            }
        }
    }

    public async Task NotifySubscribers(Subscription subscription, Result newResult)
    {
        var subscribers = _subscriptionPersistence.GetSubscribers()
            .Where(s => s.Subscriptions.Select(s => s.Id).Any(s => s.Equals(subscription.Id)))
            .ToList();

        if (subscribers.Count == 0)
        {
            throw new InvalidOperationException($"Attempted to notify subscribers but found none for subscription {subscription.Id}");
        }

        if (subscribers.Count > 1)
        {
            Logger.Error($"Attempted to notify subscribers but found more than one ({subscribers.Count}) for subscription {subscription.Id}. Notifying only the first one.");
        }

        var subscriber = subscribers.FirstOrDefault();
        var message = $"New result: {newResult.Link} \n" +
                      $"{subscription.Title} - {newResult.CreationDate} - {newResult.Price}";
        await SendMessage(subscriber, message);
    }

    public async Task NotifyAdmins(string message)
    {
        var admins = _subscriptionPersistence
            .GetSubscribers()
            .Where(s => s.IsAdmin);

        foreach (var admin in admins)
        {
            await SendMessage(admin, message);
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
            case InputState.Idle when messageText == "/edit":
            {
                await StartEditingSubscription(subscriber);
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
            case InputState.Idle or not InputState.Idle when messageText == "/list":
            {
                await DisplaySubscriptionList(subscriber);
                return;
            }
            case not InputState.Idle when messageText == "/cancel":
            {
                await CancelOperation(subscriber);
                return;
            }
            case InputState.WaitingForSubscriptionToEdit:
            {
                await EditSubscription(messageText, subscriber);
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
            case InputState.WaitingForInitialPull:
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
                               "Write /edit to edit the include and exclude keywords of a subscription. \n" +
                               "Write /delete to delete a single subscription. \n" +
                               "Write /deleteall to delete all your subscriptions. \n" +
                               "Write /list to view your current subscriptions \n" +
                               "Write /disable or /enable to disable or enable a subscription \n" +
                               "At any time, you can write /cancel";
        await SendMessage(subscriber, message);
    }

    private async Task ReloadSubscriberFile(Subscriber subscriber)
    {
        if (!subscriber.IsAdmin)
        {
            await SendMessage(subscriber, "Only admins can perform this action");
            return;
        }
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

        var subscription = new Subscription { QueryUrl = url };

        // GetParser() throws an Exception if no parser is found
        var parser = _parserProvider.GetParser(subscription);
        if (parser is null)
        {
            throw new InvalidOperationException("This doesn't seem to be a supported provider. Please try again.");
        }

        await StartInputIncudeKeywords(subscriber, subscription);
    }

    private async Task StartEditingSubscription(Subscriber subscriber)
    {
        await SendMessage(subscriber, "Enter the title of the subscription to edit.\n" +
                                      "Enter /list to view your current subscriptions or /cancel to cancel");
        subscriber.State = InputState.WaitingForSubscriptionToEdit;
    }

    private async Task EditSubscription(string messageText, Subscriber subscriber)
    {
        var subscriptionToEdit = subscriber.Subscriptions.FirstOrDefault(s => s.Title == messageText);

        if (subscriptionToEdit is null)
        {
            await SendMessage(subscriber, "No subscription with that title was found. Please try again or /cancel.");
            return;
        }

        await SendMessage(subscriber, "You can edit include keywords and exclude keywords now.");
        await StartInputIncudeKeywords(subscriber, subscriptionToEdit);
    }

    private async Task StartInputIncudeKeywords(Subscriber subscriber, Subscription subscription)
    {
        if (subscription.IncludeKeywords is not null && subscription.IncludeKeywords.Any())
        {
            await SendMessage(subscriber, "Current keywords to include are:");
            await SendMessage(subscriber, string.Join(", ", subscription.IncludeKeywords));
        }
        await SendMessage(subscriber, "Enter keywords which must all be included in title or description. Separate by \",\" and define alternatives by \"|\". Or write '/skip'");
        await SendMessage(subscriber, "Example: 'one, two|three' will find results where 'one' and 'two' are words in the description; but it will also find results with 'one' and 'three'.");
        subscriber.IncompleteSubscription = subscription;
        subscriber.State = InputState.WaitingForIncludeKeywords;
    }

    private async Task AnalyzeInputIncludeKeywords(string messageText, Subscriber subscriber)
    {
        var includeKeywords = messageText
            .Split(",")
            .Select(keyword => keyword.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Where(keyword => keyword != "/skip")
            .ToList();

        if (messageText.Trim() == "/skip")
        { 
            // When in Edit mode and skipping, keep existing keywords
            var existingIncludeKeywords = subscriber.IncompleteSubscription.IncludeKeywords;
            if (existingIncludeKeywords is not null && existingIncludeKeywords.Any())
            {
                includeKeywords.AddRange(existingIncludeKeywords);
            }
        }

        subscriber.IncompleteSubscription.IncludeKeywords = includeKeywords;
        await SendMessage(subscriber, $"Ok. I got {includeKeywords.Count} keywords to include.");
        await StartInputExcludeKeywords(subscriber, subscriber.IncompleteSubscription);
   }

    private async Task StartInputExcludeKeywords(Subscriber subscriber, Subscription subscription)
    {
        if (subscription.ExcludeKeywords is not null && subscription.ExcludeKeywords.Any())
        {
            await SendMessage(subscriber, "Current keywords to exclude are:");
            await SendMessage(subscriber, string.Join(", ", subscription.ExcludeKeywords));
        }
        await SendMessage(subscriber, "Enter keywords which must not appear in title or description, separated by \",\" or write '/skip'");
        await SendMessage(subscriber, "Hint: If any of these keywords is found in title or description, there will be no notification.");
        subscriber.State = InputState.WaitingForExcludeKeywords;
    }

    private async Task AnalyzeInputExcludeKeywords(string messageText, Subscriber subscriber)
    {
        var excludeKeywords = messageText
            .Split(",")
            .Select(keyword => keyword.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Where(keyword => keyword != "/skip")
            .ToList();

        if (messageText.Trim() == "/skip")
        {
            // When in Edit mode and skipping, keep existing keywords
            var existingExcludeKeywords = subscriber.IncompleteSubscription.ExcludeKeywords;
            if (existingExcludeKeywords is not null && existingExcludeKeywords.Any())
            {
                excludeKeywords.AddRange(existingExcludeKeywords);
            }
        }

        await SendMessage(subscriber, $"Ok. I got {excludeKeywords.Count} keywords to exclude.");

        if (subscriber.IncompleteSubscription.ExcludeKeywords is not null && subscriber.IncompleteSubscription.ExcludeKeywords.Any())
        {
            subscriber.IncompleteSubscription.ExcludeKeywords = excludeKeywords;

            // Remove the old subscription, which is currently edited
            subscriber.Subscriptions = subscriber.Subscriptions
                .Where(s => s.Id != subscriber.IncompleteSubscription.Id)
                .ToList();
            
            // Move the edited subscription to the final list and save
            PersistIncompleteSubscription(subscriber);
            await SendMessage(subscriber, "Finished editing!");
        }
        else
        {
            subscriber.IncompleteSubscription.ExcludeKeywords = excludeKeywords;
            subscriber.State = InputState.WaitingForInitialPull;
            await SendMessage(subscriber, "Do you want to receive all existing matches initially? (yes/no)");
        }
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
        var id = PersistIncompleteSubscription(subscriber);
        Logger.Information($"Added subscription {id}");
        await SendMessage(subscriber, $"That's it. Added a new subscription for you. You now have {subscriber.Subscriptions.Count} subscriptions.");
        await SendMessage(subscriber, "Initially it can take a while until all matches are found. I can only do 40 queries every 5 minutes :-)");
    }

    private Guid PersistIncompleteSubscription(Subscriber subscriber)
    {
        var id = subscriber.IncompleteSubscription.Id;
        subscriber.IncompleteSubscription.Enabled = true;
        subscriber.Subscriptions.Add(subscriber.IncompleteSubscription);
        subscriber.IncompleteSubscription = null;
        subscriber.State = InputState.Idle;
        _subscriptionPersistence.SaveData();
        return id;
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