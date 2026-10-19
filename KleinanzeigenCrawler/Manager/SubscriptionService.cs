using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Query;
using Serilog;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Service for managing subscription business logic of the subscribers of one messenger channel
/// </summary>
public class SubscriptionService
{
    private readonly ISubscriptionPersistence _persistence;
    private readonly IParserProvider _parserProvider;
    private readonly ILogger _logger;
    private readonly MessengerChannel _channel;
    private readonly bool _allowNewUsers;
    
    public SubscriptionService(
        ISubscriptionPersistence persistence,
        IParserProvider parserProvider,
        ILogger logger,
        MessengerChannel channel,
        bool allowNewUsers)
    {
        _persistence = persistence;
        _parserProvider = parserProvider;
        _logger = logger;
        _channel = channel;
        _allowNewUsers = allowNewUsers;
    }
    
    /// <summary>
    /// Gets the subscriber with the given ID and registers a new one if it does not exist yet and new users are allowed
    /// </summary>
    public ServiceResult<SubscriberRegistration> GetOrRegisterSubscriber(string subscriberId)
    {
        var subscriber = FindSubscriber(subscriberId);
        if (subscriber != null)
        {
            return ServiceResult<SubscriberRegistration>.Ok(new SubscriberRegistration(subscriber, IsNew: false));
        }
        
        if (!_allowNewUsers)
        {
            _logger.Warning("New {Channel} user registration blocked for userId: {UserId}", _channel, subscriberId);
            return ServiceResult<SubscriberRegistration>.Fail("New user registrations are currently disabled. Please contact the maintainer.");
        }
        
        subscriber = new Subscriber
        {
            Id = subscriberId,
            Channel = _channel
        };
        _persistence.AddSubscriber(subscriber);
        _logger.Information("Created new {Channel} subscriber: {UserId}", _channel, subscriberId);
        return ServiceResult<SubscriberRegistration>.Ok(new SubscriberRegistration(subscriber, IsNew: true));
    }
    
    /// <summary>
    /// Gets the subscribers of this channel that currently want to be notified about the given subscription
    /// </summary>
    public List<Subscriber> GetRecipients(Subscription subscription)
    {
        return _persistence.GetSubscribers()
            .Where(s => s.Channel == _channel && s.Subscriptions.Any(sub => sub.Id == subscription.Id && sub.Enabled))
            .ToList();
    }
    
    /// <summary>
    /// Checks whether a subscriber may use the title for a new subscription and returns the normalized title.
    /// Conversations use this to reject a title right away instead of after all other answers were collected.
    /// </summary>
    public ServiceResult<string> ValidateTitle(string subscriberId, string title)
    {
        var subscriber = FindSubscriber(subscriberId);
        return subscriber == null
            ? ServiceResult<string>.Fail("Subscriber not found.")
            : ValidateTitle(subscriber, title);
    }
    
    /// <summary>
    /// Creates a new subscription for a subscriber
    /// </summary>
    public async Task<ServiceResult<Subscription>> CreateSubscriptionAsync(
        string subscriberId,
        string url,
        List<string> includeKeywords,
        List<string> excludeKeywords,
        bool initialPull,
        string title)
    {
        try
        {
            // Validate URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return ServiceResult<Subscription>.Fail("Invalid URL. Please provide a valid HTTPS URL.");
            }
            
            var subscriber = FindSubscriber(subscriberId);
            if (subscriber == null)
            {
                return ServiceResult<Subscription>.Fail("Subscriber not found.");
            }
            
            // Validate the title before the rate limited request below is made
            var validatedTitle = ValidateTitle(subscriber, title);
            if (!validatedTitle.IsSuccess)
            {
                return ServiceResult<Subscription>.Fail(validatedTitle.ErrorMessage);
            }
            
            title = validatedTitle.Data;
            
            // Create subscription
            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                QueryUrl = uri,
                IncludeKeywords = includeKeywords ?? new List<string>(),
                ExcludeKeywords = excludeKeywords ?? new List<string>(),
                InitialPull = initialPull,
                Title = title,
                Enabled = true,
                FirstRunCompleted = false
            };
            
            // Validate that a parser exists for this URL
            IParser parser;
            try
            {
                parser = _parserProvider.GetParser(subscription);
                if (parser == null)
                {
                    return ServiceResult<Subscription>.Fail("No parser found for this URL. Supported sites: kleinanzeigen.de, wg-gesucht.de");
                }
            }
            catch (Exception ex)
            {
                return ServiceResult<Subscription>.Fail($"Unsupported website: {ex.Message}");
            }
            
            // Check result count to prevent overloading
            try
            {
                _logger.Information($"Checking result count for URL: {uri}");
                var html = await parser.QueryExecutor.GetHtml(uri);
                if (html != null)
                {
                    // First, try to get the actual total from the parser (if supported)
                    var actualTotal = parser.GetTotalResultCount(html);
                    
                    if (actualTotal.HasValue)
                    {
                        _logger.Information($"Parser reported actual total: {actualTotal.Value} results");
                        
                        if (actualTotal.Value > 50)
                        {
                            return ServiceResult<Subscription>.Fail(
                                "Too many results!\n\n" +
                                $"This search returns {actualTotal.Value} results, but the limit is 50.\n\n" +
                                $"Please narrow down your search parameters (e.g., add location, price range, or more specific keywords) until you get fewer than 50 results.");
                        }
                    }
                    else
                    {
                        // Fallback to estimation if parser doesn't support GetTotalResultCount
                        var results = parser.ParseLinks(html).ToList();
                        var additionalPages = parser.GetAdditionalPages(html);
                        var totalPages = additionalPages.Count + 1; // +1 for the first page
                        var totalEstimatedResults = results.Count * totalPages;
                        
                        _logger.Information($"Found {results.Count} results on first page, {additionalPages.Count} additional pages. Estimated total: {totalEstimatedResults}");
                        
                        if (totalEstimatedResults > 50)
                        {
                            return ServiceResult<Subscription>.Fail(
                                "Too many results!\n\n" +
                                $"This search returns approximately {totalEstimatedResults} results, but the limit is 50.\n\n" +
                                $"Please narrow down your search parameters (e.g., add location, price range, or more specific keywords) until you get fewer than 50 results.");
                        }
                    }
                }
            }
            catch (PageNotFoundException ex)
            {
                return ServiceResult<Subscription>.Fail(
                    $"The search page was not found (the site answered {(int)ex.StatusCode}). Please check the URL and send the current one from your browser.");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to check result count, allowing subscription anyway");
                // Don't fail subscription creation if we can't check the count
            }
            
            // The list is replaced instead of modified, because the crawler and the persistence enumerate it concurrently
            subscriber.Subscriptions = new List<Subscription>(subscriber.Subscriptions) { subscription };
            _persistence.UpdateSubscriber(subscriber);
            
            _logger.Information($"Created subscription {subscription.Id} for subscriber {subscriberId}");
            return ServiceResult<Subscription>.Ok(subscription);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error creating subscription");
            return ServiceResult<Subscription>.Fail($"Error creating subscription: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Updates an existing subscription
    /// </summary>
    public ServiceResult<Subscription> UpdateSubscription(
        string subscriberId,
        Guid subscriptionId,
        List<string> includeKeywords,
        List<string> excludeKeywords)
    {
        try
        {
            var subscriber = FindSubscriber(subscriberId);
            
            if (subscriber == null)
            {
                return ServiceResult<Subscription>.Fail("Subscriber not found.");
            }
            
            var subscription = subscriber.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
            if (subscription == null)
            {
                return ServiceResult<Subscription>.Fail("Subscription not found.");
            }
            
            subscription.IncludeKeywords = includeKeywords ?? new List<string>();
            subscription.ExcludeKeywords = excludeKeywords ?? new List<string>();
            
            _persistence.UpdateSubscriber(subscriber);
            
            _logger.Information($"Updated subscription {subscriptionId} for subscriber {subscriberId}");
            return ServiceResult<Subscription>.Ok(subscription);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error updating subscription");
            return ServiceResult<Subscription>.Fail($"Error updating subscription: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Deletes a subscription
    /// </summary>
    public ServiceResult<bool> DeleteSubscription(string subscriberId, Guid subscriptionId)
    {
        try
        {
            var subscriber = FindSubscriber(subscriberId);
            
            if (subscriber == null)
            {
                return ServiceResult<bool>.Fail("Subscriber not found.");
            }
            
            var subscription = subscriber.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
            if (subscription == null)
            {
                return ServiceResult<bool>.Fail("Subscription not found.");
            }
            
            subscriber.Subscriptions = subscriber.Subscriptions.Where(s => s.Id != subscriptionId).ToList();
            _persistence.UpdateSubscriber(subscriber);
            
            _logger.Information($"Deleted subscription {subscriptionId} for subscriber {subscriberId}");
            return ServiceResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error deleting subscription");
            return ServiceResult<bool>.Fail($"Error deleting subscription: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Enables or disables a subscription
    /// </summary>
    public ServiceResult<bool> SetSubscriptionEnabled(string subscriberId, Guid subscriptionId, bool enabled)
    {
        try
        {
            var subscriber = FindSubscriber(subscriberId);
            
            if (subscriber == null)
            {
                return ServiceResult<bool>.Fail("Subscriber not found.");
            }
            
            var subscription = subscriber.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
            if (subscription == null)
            {
                return ServiceResult<bool>.Fail("Subscription not found.");
            }
            
            subscription.Enabled = enabled;
            _persistence.UpdateSubscriber(subscriber);
            
            var action = enabled ? "enabled" : "disabled";
            _logger.Information($"{action} subscription {subscriptionId} for subscriber {subscriberId}");
            return ServiceResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error changing subscription enabled state");
            return ServiceResult<bool>.Fail($"Error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Disables all subscriptions of a subscriber, e.g. because messages cannot be delivered to it anymore.
    /// Returns the number of subscriptions that were disabled.
    /// </summary>
    public ServiceResult<int> DisableAllSubscriptions(string subscriberId)
    {
        try
        {
            var subscriber = FindSubscriber(subscriberId);
            if (subscriber == null)
            {
                return ServiceResult<int>.Fail("Subscriber not found.");
            }
            
            var enabledSubscriptions = subscriber.Subscriptions.Where(s => s.Enabled).ToList();
            if (enabledSubscriptions.Count == 0)
            {
                return ServiceResult<int>.Ok(0);
            }
            
            enabledSubscriptions.ForEach(s => s.Enabled = false);
            _persistence.UpdateSubscriber(subscriber);
            
            _logger.Information("Disabled {Count} subscriptions of {Channel} subscriber {SubscriberId}", enabledSubscriptions.Count, _channel, subscriberId);
            return ServiceResult<int>.Ok(enabledSubscriptions.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error disabling all subscriptions");
            return ServiceResult<int>.Fail($"Error disabling subscriptions: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets all subscriptions for a subscriber
    /// </summary>
    public List<Subscription> GetSubscriptions(string subscriberId)
    {
        var subscriber = FindSubscriber(subscriberId);
        return subscriber?.Subscriptions ?? new List<Subscription>();
    }
    
    /// <summary>
    /// Gets a specific subscription
    /// </summary>
    public Subscription GetSubscription(string subscriberId, Guid subscriptionId)
    {
        var subscriber = FindSubscriber(subscriberId);
        return subscriber?.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
    }
    
    /// <summary>
    /// Deletes all subscriptions for a subscriber
    /// </summary>
    public ServiceResult<bool> DeleteAllSubscriptions(string subscriberId)
    {
        try
        {
            var subscriber = FindSubscriber(subscriberId);
            
            if (subscriber == null)
            {
                return ServiceResult<bool>.Fail("Subscriber not found.");
            }
            
            var count = subscriber.Subscriptions.Count;
            subscriber.Subscriptions = new List<Subscription>();
            _persistence.UpdateSubscriber(subscriber);
            
            _logger.Information($"Deleted all {count} subscriptions for subscriber {subscriberId}");
            return ServiceResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error deleting all subscriptions");
            return ServiceResult<bool>.Fail($"Error deleting subscriptions: {ex.Message}");
        }
    }
    
    private static ServiceResult<string> ValidateTitle(Subscriber subscriber, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ServiceResult<string>.Fail("Please provide a title for the subscription.");
        }
        
        title = title.Trim();
        return subscriber.Subscriptions.Any(s => string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase))
            ? ServiceResult<string>.Fail($"A subscription with title '{title}' already exists. Please choose a different title.")
            : ServiceResult<string>.Ok(title);
    }
    
    private Subscriber FindSubscriber(string subscriberId)
    {
        return _persistence.GetSubscribers().FirstOrDefault(s => s.Channel == _channel && s.Id == subscriberId);
    }
}
