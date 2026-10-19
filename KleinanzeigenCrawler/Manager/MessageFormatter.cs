using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Service for formatting Telegram messages with consistent styling
/// </summary>
public class MessageFormatter
{
    /// <summary>
    /// Formats the welcome message
    /// </summary>
    public static string FormatWelcome()
    {
        return @"👋 <b>Welcome to Kleinanzeigen Crawler!</b>

I'll help you monitor classified ads and notify you about new matches.

🔍 <b>What I can do:</b>
• Monitor Kleinanzeigen and WG-Gesucht
• Filter by keywords
• Send instant notifications
• Manage multiple subscriptions

Use the buttons below to get started!";
    }
    
    /// <summary>
    /// Formats the help message
    /// </summary>
    public static string FormatHelp()
    {
        return @"❓ <b>Help &amp; Instructions</b>

<b>📝 Add Subscription</b>
1. Click ""Add Subscription""
2. Paste the search URL from your browser
3. Add keywords to include (optional)
4. Add keywords to exclude (optional)
5. Choose whether to get existing results
6. Give your subscription a title

<b>📋 Manage Subscriptions</b>
• View: Click ""List Subscriptions"" to see all
• Edit: Change keywords for existing subscriptions
• Enable/Disable: Pause notifications temporarily
• Delete: Remove subscriptions you no longer need

<b>🔍 How it works:</b>
I check for new ads every minute and notify you within 5 minutes. Keywords work on both title and description.

<b>⚠️ Important:</b>
• Only desktop URLs are supported (not mobile)
• Use HTTPS URLs only
• Rate limits apply (40 requests per 5 minutes)

<b>💡 Tips:</b>
• Use specific keywords to reduce false matches
• Combine keywords with | for alternatives (e.g., ""red|blue"")
• Use exclude keywords to filter out unwanted results

Need more help? Contact an admin.";
    }

    /// <summary>
    /// Formats a compact report of the user's current subscriptions
    /// </summary>
    public static string FormatUserSnapshot(IReadOnlyCollection<Subscription> subscriptions)
    {
        var total = subscriptions?.Count ?? 0;
        var sb = new StringBuilder();
        sb.AppendLine("📊 <b>Your Report</b>\n");
        
        if (total == 0)
        {
            sb.AppendLine("You're not tracking anything yet. Tap \"Add Subscription\" to create your first alert!");
            return sb.ToString();
        }
        
        var active = subscriptions.Count(s => s.Enabled);
        var paused = total - active;
        var withInitialPull = subscriptions.Count(s => s.InitialPull);
        var awaitingFirstRun = subscriptions.Count(s => !s.FirstRunCompleted);
        
        sb.AppendLine($"Total subscriptions: <b>{total}</b>");
        sb.AppendLine($"📡 Active: {active}");
        sb.AppendLine($"🔕 Paused: {paused}");
        sb.AppendLine($"📥 With initial pull: {withInitialPull}");
        
        if (awaitingFirstRun > 0)
        {
            sb.AppendLine($"⏳ Awaiting first run: {awaitingFirstRun}");
        }
        
        sb.AppendLine("\nUse the quick buttons below to add new searches or fine-tune existing ones.");
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats a subscription list
    /// </summary>
    public static string FormatSubscriptionList(List<Subscription> subscriptions)
    {
        if (subscriptions == null || subscriptions.Count == 0)
        {
            return @"📭 <b>No Subscriptions</b>

You don't have any subscriptions yet.
Click ""Add Subscription"" to create your first one!";
        }
        
        var sb = new StringBuilder();
        sb.AppendLine($"📋 <b>Your Subscriptions ({subscriptions.Count})</b>\n");
        
        var enabled = subscriptions.Count(s => s.Enabled);
        var disabled = subscriptions.Count - enabled;
        
        sb.AppendLine($"✅ Active: {enabled}");
        sb.AppendLine($"🔕 Disabled: {disabled}\n");
        sb.AppendLine("Click on a subscription to view details:");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats a single subscription detail view
    /// </summary>
    public static string FormatSubscriptionDetail(Subscription subscription)
    {
        var statusIcon = subscription.Enabled ? "✅" : "🔕";
        var status = subscription.Enabled ? "Enabled" : "Disabled";
        
        var sb = new StringBuilder();
        sb.AppendLine($"{statusIcon} <b>{Encode(subscription.Title)}</b>\n");
        sb.AppendLine($"<b>Status:</b> {status}");
        sb.AppendLine($"<b>URL:</b> {Encode(subscription.QueryUrl?.ToString() ?? string.Empty)}\n");
        
        var includeKeywords = FormatKeywords(subscription.IncludeKeywords);
        sb.AppendLine($"<b>➕ Include Keywords:</b> {includeKeywords}\n");
        
        var excludeKeywords = FormatKeywords(subscription.ExcludeKeywords);
        sb.AppendLine($"<b>➖ Exclude Keywords:</b> {excludeKeywords}\n");
        
        sb.AppendLine($"<b>📥 Initial Pull:</b> {(subscription.InitialPull ? "Yes" : "No")}");
        sb.AppendLine($"<b>🏁 First Run:</b> {(subscription.FirstRunCompleted ? "Completed" : "Pending")}");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats a notification about a new result
    /// </summary>
    public static string FormatNewResult(Subscription subscription, Result result)
    {
        var sb = new StringBuilder();
        var resultUrl = result.Link?.ToString() ?? string.Empty;
        var subscriptionUrl = subscription.QueryUrl?.ToString() ?? string.Empty;
        
        // Put result URL first for preview generation
        sb.AppendLine($"<a href=\"{Encode(resultUrl)}\">New result</a> in <a href=\"{Encode(subscriptionUrl)}\">{Encode(subscription.Title)}</a>:");
        
        // Combine date and price on one line if available
        var hasDate = !string.IsNullOrEmpty(result.CreationDate);
        var hasPrice = !string.IsNullOrEmpty(result.Price);
        
        if (hasDate && hasPrice)
        {
            sb.AppendLine($"{Encode(result.CreationDate)} | {Encode(result.Price)}");
        }
        else if (hasDate)
        {
            sb.AppendLine(Encode(result.CreationDate));
        }
        else if (hasPrice)
        {
            sb.AppendLine(Encode(result.Price));
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats the start of add subscription flow
    /// </summary>
    public static string FormatAddSubscriptionStart()
    {
        return @"📝 <b>Add New Subscription</b>

<b>Step 1 of 5: Enter URL</b>

Please paste the search URL from your browser.

⚠️ <b>Important:</b>
• Use desktop URLs (not mobile)
• Include all search filters in the URL
• Only HTTPS URLs are supported

<b>Example:</b>
<code>https://www.kleinanzeigen.de/s-wohnung-mieten/berlin/c203l3331</code>

Supported sites:
• kleinanzeigen.de
• wg-gesucht.de";
    }
    
    /// <summary>
    /// Formats the include keywords step
    /// </summary>
    public static string FormatAddIncludeKeywords(List<string> currentKeywords = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📝 <b>Add New Subscription</b>\n");
        sb.AppendLine("<b>Step 2 of 5: Include Keywords</b>\n");
        
        if (currentKeywords?.Count > 0)
        {
            sb.AppendLine($"<b>Current keywords:</b> {FormatKeywords(currentKeywords)}\n");
        }
        
        sb.AppendLine("Enter keywords that must appear in the title or description.");
        sb.AppendLine("\n<b>Format:</b>");
        sb.AppendLine("• Separate multiple keywords with commas: <code>keyword1, keyword2</code>");
        sb.AppendLine("• Use | for alternatives: <code>red|blue, large</code>");
        sb.AppendLine("  (finds results with 'red' OR 'blue', AND 'large')");
        sb.AppendLine("\n<b>Example:</b> <code>balcony|terrace, parking</code>");
        sb.AppendLine("\nOr click <b>Skip</b> to continue without keywords.");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats the exclude keywords step
    /// </summary>
    public static string FormatAddExcludeKeywords(List<string> currentKeywords = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📝 <b>Add New Subscription</b>\n");
        sb.AppendLine("<b>Step 3 of 5: Exclude Keywords</b>\n");
        
        if (currentKeywords?.Count > 0)
        {
            sb.AppendLine($"<b>Current keywords:</b> {FormatKeywords(currentKeywords)}\n");
        }
        
        sb.AppendLine("Enter keywords that should NOT appear in results.");
        sb.AppendLine("\n<b>Format:</b>");
        sb.AppendLine("• Separate with commas: <code>keyword1, keyword2</code>");
        sb.AppendLine("\n<b>Example:</b> <code>damaged, defect, broken</code>");
        sb.AppendLine("\n⚠️ If ANY of these words appear, you won't be notified.");
        sb.AppendLine("\nOr click <b>Skip</b> to continue without exclude keywords.");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Formats the initial pull step
    /// </summary>
    public static string FormatAddInitialPull()
    {
        return @"📝 <b>Add New Subscription</b>

<b>Step 4 of 5: Initial Results</b>

Do you want to receive notifications for all currently available results, or only for new ones that appear after now?

<b>✅ Yes, show all existing:</b>
You'll get notified about all current matches immediately. Good for new searches.

<b>❌ No, only new ones:</b>
You'll only get notified about ads that appear after this moment. Good for ongoing monitoring.";
    }
    
    /// <summary>
    /// Formats the title step
    /// </summary>
    public static string FormatAddTitle()
    {
        return @"📝 <b>Add New Subscription</b>

<b>Step 5 of 5: Title</b>

Choose a unique title for this subscription.

This helps you identify it in your subscription list.

<b>Example:</b> <code>Apartment Berlin Mitte</code>";
    }
    
    /// <summary>
    /// Formats the confirmation step
    /// </summary>
    public static string FormatAddConfirmation(AddSubscriptionData data)
    {
        return $@"📝 <b>Add New Subscription</b>

<b>Review Your Subscription:</b>

{data.GetSummary()}

Everything correct? Click <b>Confirm</b> to create the subscription!";
    }
    
    /// <summary>
    /// Formats a success message
    /// </summary>
    public static string FormatSuccess(string message)
    {
        return $"✅ <b>Success!</b>\n\n{Encode(message)}";
    }
    
    /// <summary>
    /// Formats a plain message without a heading
    /// </summary>
    public static string FormatNotice(string message)
    {
        return Encode(message);
    }
    
    /// <summary>
    /// Formats an error message
    /// </summary>
    public static string FormatError(string message)
    {
        return $"❌ <b>Error</b>\n\n{Encode(message)}";
    }
    
    /// <summary>
    /// Formats a cancelled operation message
    /// </summary>
    public static string FormatCancelled()
    {
        return "❌ <b>Cancelled</b>\n\nOperation cancelled. You're back at the main menu.";
    }
    
    /// <summary>
    /// Formats delete confirmation message
    /// </summary>
    public static string FormatDeleteConfirmation(Subscription subscription)
    {
        return $@"⚠️ <b>Delete Subscription?</b>

Are you sure you want to delete:

<b>{Encode(subscription.Title)}</b>

This action cannot be undone!";
    }
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    private static string Encode(string value) => Encoder.Encode(value ?? string.Empty);

    private static string FormatKeywords(IEnumerable<string> keywords)
    {
        if (keywords is null)
        {
            return "None";
        }

        var formatted = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(Encode)
            .ToList();

        return formatted.Count > 0 ? string.Join(", ", formatted) : "None";
    }
}
