using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;

namespace EbayKleinanzeigenCrawler.Persistence;

public class SubscriptionPersistence : ISubscriptionPersistence
{
    private readonly ILogger _logger;
    private readonly IDataStorage _dataStorage;
    private ConcurrentBag<Subscriber> _subscriberList = new();
    private static readonly object _lock = new();

    public SubscriptionPersistence(ILogger logger, IDataStorage dataStorage)
    {
        _logger = logger;
        _dataStorage = dataStorage;
        Directory.CreateDirectory("data");
        RestoreData();
    }

    public bool RestoreData()
    {
        lock(_lock)
        {
            try
            {
                _dataStorage.Load(Path.Join("data", "Subscribers.json"), out ConcurrentBag<Subscriber> data);
                _subscriberList = data;
                _logger.Information($"Restored data: {data.Count} Subscribers");
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
                }

                _subscriberList ??= new ConcurrentBag<Subscriber>();
                return false;
            }
        }

        return true;
    }

    public void SaveData()
    {
        lock(_lock)
        {            
            _dataStorage.Save(_subscriberList, Path.Join("data", "Subscribers.json"));
        }
    }

    public void AddSubscriber(Subscriber subscriber)
    {
        lock(_lock)
        {
            _subscriberList.Add(subscriber);
            SaveData();
        }
    }

    public Subscriber[] GetSubscribers()
    {
        lock(_lock)
        {
            return _subscriberList.ToArray();
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
        if (subscription.FirstRunCompleted)
        {
            return;
        }

        lock(_lock)
        {
            // The supplied subscription was initially copied by .ToArray(), so modifying it won't modify the instance in _subscriberList
            var subscriptionToModify = _subscriberList
                .SelectMany(s => s.Subscriptions)
                .SingleOrDefault(s => s.Id == subscription.Id);

            if (subscriptionToModify is null)
            {
                // Subscription may have been deleted
                _logger.Error($"Cannot complete first run for subscription {subscription.Id} as the subscription was not found");
                return;
            }
            
            subscriptionToModify.FirstRunCompleted = true;
            SaveData();
        }
    }
}