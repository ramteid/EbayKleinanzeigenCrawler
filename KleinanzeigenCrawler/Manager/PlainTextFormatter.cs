using System.Collections.Generic;
using System.Linq;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Formats plain text messages for messengers without markup or buttons
/// </summary>
public static class PlainTextFormatter
{
    private const string Commands = """
        Commands:
        /add - create a subscription
        /list - show your subscriptions
        /edit - change the keywords of a subscription
        /enable - resume a paused subscription
        /disable - pause a subscription
        /delete - delete a subscription
        /cancel - abort the current action
        /help - show detailed instructions
        """;

    public static string Welcome()
    {
        return $"""
            Welcome to the Kleinanzeigen Crawler.

            I watch searches on kleinanzeigen.de and wg-gesucht.de and message you as soon as new matching ads appear.

            {Commands}
            """;
    }

    public static string Help()
    {
        return $"""
            {Commands}

            How it works:
            - I check regularly for new ads and notify you shortly after they appear.
            - Keywords are matched against the title and the description of an ad.
            - Only desktop URLs using HTTPS are supported.
            - Searches with more than 50 results are rejected. Narrow down the search first.
            """;
    }

    public static string AddUrlPrompt()
    {
        return """
            New subscription

            Step 1 of 5: search URL

            Open your search on kleinanzeigen.de or wg-gesucht.de in a desktop browser, apply all filters and send me the URL (HTTPS only).

            Send /cancel to abort.
            """;
    }

    public static string AddIncludeKeywordsPrompt()
    {
        return """
            Step 2 of 5: include keywords

            Send the keywords that must appear in the title or description, separated by commas.
            Use | for alternatives: "red|blue, large" matches ads containing red or blue, and large.

            Send /skip to continue without include keywords.
            """;
    }

    public static string AddExcludeKeywordsPrompt()
    {
        return """
            Step 3 of 5: exclude keywords

            Send the keywords separated by commas. You will not be notified about ads containing any of them.
            Example: damaged, defect, broken

            Send /skip to continue without exclude keywords.
            """;
    }

    public static string AddInitialPullPrompt()
    {
        return """
            Step 4 of 5: existing results

            Do you want to be notified about all ads that already exist?
            Reply "yes" to receive all current results, or "no" to only receive ads that appear from now on.
            """;
    }

    public static string AddTitlePrompt()
    {
        return """
            Step 5 of 5: title

            Send a unique title for this subscription.
            Example: Apartment Berlin Mitte
            """;
    }

    public static string AddConfirmation(AddSubscriptionData data)
    {
        return $"""
            Please review your subscription:

            Title: {data.Title}
            URL: {data.Url}
            Include keywords: {FormatKeywords(data.IncludeKeywords)}
            Exclude keywords: {FormatKeywords(data.ExcludeKeywords)}
            Notify about existing results: {(data.InitialPull ? "yes" : "no")}

            Reply "yes" to create it or send /cancel to abort.
            """;
    }

    public static string EditIncludeKeywordsPrompt(Subscription subscription)
    {
        return $"""
            Editing "{subscription.Title}"

            Current include keywords: {FormatKeywords(subscription.IncludeKeywords)}
            Send the new include keywords separated by commas. Send /skip to keep them or /none to remove all of them.
            """;
    }

    public static string EditExcludeKeywordsPrompt(Subscription subscription)
    {
        return $"""
            Current exclude keywords: {FormatKeywords(subscription.ExcludeKeywords)}
            Send the new exclude keywords separated by commas. Send /skip to keep them or /none to remove all of them.
            """;
    }

    public static string SubscriptionList(IReadOnlyList<Subscription> subscriptions)
    {
        if (subscriptions.Count == 0)
        {
            return NoSubscriptions();
        }

        var lines = new List<string> { $"Your subscriptions ({subscriptions.Count}):" };
        for (var i = 0; i < subscriptions.Count; i++)
        {
            var subscription = subscriptions[i];
            lines.Add(string.Empty);
            lines.Add($"{i + 1}. {subscription.Title} ({(subscription.Enabled ? "enabled" : "disabled")})");
            lines.Add($"   URL: {subscription.QueryUrl.AbsoluteUri}");
            lines.Add($"   Include: {FormatKeywords(subscription.IncludeKeywords)}");
            lines.Add($"   Exclude: {FormatKeywords(subscription.ExcludeKeywords)}");
        }

        return string.Join("\n", lines);
    }

    public static string NoSubscriptions()
    {
        return "You have no subscriptions yet. Send /add to create one.";
    }

    public static string SelectSubscriptionPrompt(string action, IReadOnlyList<Subscription> subscriptions)
    {
        var lines = new List<string> { $"Which subscription do you want to {action}?", string.Empty };
        lines.AddRange(subscriptions.Select((subscription, index) => $"{index + 1}. {subscription.Title}"));
        lines.Add(string.Empty);
        lines.Add("Send its number or title, or send /cancel to abort.");
        return string.Join("\n", lines);
    }

    public static string DeleteConfirmation(Subscription subscription)
    {
        return $"""
            Delete the subscription "{subscription.Title}"? This cannot be undone.
            Reply "yes" to delete it or send /cancel to abort.
            """;
    }

    public static string NewResult(Subscription subscription, Result result)
    {
        var lines = new List<string> { $"New result in \"{subscription.Title}\":" };

        var details = new[] { result.CreationDate, result.Price }.Where(value => !string.IsNullOrEmpty(value)).ToList();
        if (details.Count > 0)
        {
            lines.Add(string.Join(" | ", details));
        }

        lines.Add(result.Link.AbsoluteUri);
        return string.Join("\n", lines);
    }

    public static string Error(string message)
    {
        return $"Error: {message}";
    }

    public static string Cancelled()
    {
        return "Cancelled.";
    }

    public static string NothingToCancel()
    {
        return "There is nothing to cancel.";
    }

    private static string FormatKeywords(IEnumerable<string> keywords)
    {
        var formatted = keywords?.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).ToList();
        return formatted is { Count: > 0 } ? string.Join(", ", formatted) : "none";
    }
}
