using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;

namespace KleinanzeigenCrawler.Persistence;

public class SubscriptionPersistence : ISubscriptionPersistence
{
    private readonly ILogger _logger;
    private readonly IDataStorage _dataStorage;
    private readonly string _dataDirectory;
    private ConcurrentDictionary<string, Subscriber> _subscribers = new();
    private static readonly object _lock = new();

    public SubscriptionPersistence(ILogger logger, IDataStorage dataStorage, AppConfiguration configuration)
    {
        _logger = logger;
        _dataStorage = dataStorage;
        configuration ??= new AppConfiguration();
        _dataDirectory = Path.GetFullPath(configuration.Crawler?.DataPath ?? "data");
        Directory.CreateDirectory(_dataDirectory);
        
        // Initialize to empty dictionary before attempting restore
        _subscribers = new ConcurrentDictionary<string, Subscriber>();
        RestoreData();
    }

    private bool RestoreData()
    {
        lock(_lock)
        {
            try
            {
                _dataStorage.Load(GetSubscriberFilePath(), out ConcurrentBag<Subscriber> data);
                data ??= new ConcurrentBag<Subscriber>();
                
                // Convert to dictionary for thread-safe operations
                var mergedSubscribers = data
                    .Where(s => !string.IsNullOrWhiteSpace(s?.Id))
                    .GroupBy(s => s.Id)
                    .Select(group =>
                    {
                        var deduplicatedSubscriptions = group
                            .SelectMany(s => s.Subscriptions ?? new List<Subscription>())
                            .GroupBy(subscription => subscription.Id)
                            .Select(subscriptionGroup => 
                            {
                                var subs = subscriptionGroup.ToList();
                                if (subs.Count > 1)
                                {
                                    _logger.Warning($"Found {subs.Count} subscriptions with duplicate ID {subscriptionGroup.Key} for user {group.Key}. Using most recent. Titles: {string.Join(", ", subs.Select(s => $"'{s.Title}'"))}");
                                }
                                return subscriptionGroup.Last();
                            })
                            .ToList();

                        return new Subscriber
                        {
                            Id = group.Key,
                            Subscriptions = deduplicatedSubscriptions
                        };
                    })
                    .ToDictionary(s => s.Id, s => s);

                _subscribers = new ConcurrentDictionary<string, Subscriber>(mergedSubscribers);
                _logger.Information($"Restored data: {data.Count} Subscribers (merged into {mergedSubscribers.Count})");
            }
            catch (Exception e)
            {
                if (e is FileNotFoundException)
                {
                    _logger.Warning($"Could not restore subscribers: {e.Message}");
                }
                else
                {
                    _logger.Error(e, $"Error when restoring subscribers: {e.Message}");
                    _logger.Error($"CRITICAL: Failed to load subscriber data. Existing users may be unable to access the bot. Please check the Subscribers.json file for corruption or encoding issues.");
                }

                // Ensure _subscribers is always initialized
                if (_subscribers == null)
                {
                    _subscribers = new ConcurrentDictionary<string, Subscriber>();
                }
                return false;
            }
        }

        return true;
    }

    private void SaveData()
    {
        lock(_lock)
        {
            var subscriberList = new ConcurrentBag<Subscriber>(_subscribers.Values);
            _dataStorage.Save(subscriberList, GetSubscriberFilePath());
        }
    }

    public void AddSubscriber(Subscriber subscriber)
    {
        lock(_lock)
        {
            _subscribers.TryAdd(subscriber.Id, subscriber);
            SaveData();
        }
    }

    public Subscriber[] GetSubscribers()
    {
        lock(_lock)
        {
            return _subscribers.Values.ToArray();
        }
    }

    public List<Subscription> GetEnabledSubscriptions()
    {
        return GetSubscribers()
            .SelectMany(s => s.Subscriptions)
            .Where(s => s.Enabled)
            .ToList();
    }

    public void EnsureFirstRunCompletedAndSave(Subscription subscription)
    {
        lock(_lock)
        {
            // Check again inside lock to prevent race condition
            if (subscription.FirstRunCompleted)
            {
                return;
            }
            
            // Find the subscriber that has this subscription
            var subscriber = _subscribers.Values
                .FirstOrDefault(s => s.Subscriptions.Any(sub => sub.Id == subscription.Id));

            if (subscriber == null)
            {
                // Subscription may have been deleted
                _logger.Error($"Cannot complete first run for subscription {subscription.Id} as the subscription was not found");
                return;
            }
            
            var subscriptionToModify = subscriber.Subscriptions
                .FirstOrDefault(s => s.Id == subscription.Id);
                
            if (subscriptionToModify != null)
            {
                subscriptionToModify.FirstRunCompleted = true;
                SaveData();
            }
        }
    }

    public void UpdateSubscriber(Subscriber subscriber)
    {
        lock(_lock)
        {
            _subscribers.AddOrUpdate(subscriber.Id, subscriber, (key, existing) => subscriber);
            SaveData();
        }
    }

    private string GetSubscriberFilePath() => Path.Join(_dataDirectory, "Subscribers.json");
}
