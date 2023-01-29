using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EbayKleinanzeigenCrawler.Storage
{
    public class SubscriptionPersistence : ISubscriptionPersistence
    {
        private readonly ILogger _logger;
        protected readonly IDataStorage DataStorage;
        protected ConcurrentBag<Subscriber> SubscriberList { get; set; }

        public SubscriptionPersistence(ILogger logger, IDataStorage dataStorage)
        {
            Directory.CreateDirectory("data");
            _logger = logger;
            DataStorage = dataStorage;
            SubscriberList = new ConcurrentBag<Subscriber>();
            RestoreData();
        }

        public void AddSubscriber(Subscriber subscriber)
        {
            SubscriberList.Add(subscriber);
            SaveData();
        }

        public Subscriber[] GetSubscribers()
        {
            return SubscriberList.ToArray();
        }

        public bool RestoreData()
        {
            try
            {
                DataStorage.Load(Path.Join("data", "Subscribers.json"), out ConcurrentBag<Subscriber> data);
                SubscriberList = data;
                _logger.Information($"Restored data: {data.Count} Subscribers");
            }
            catch (Exception e)
            {
                SubscriberList ??= new ConcurrentBag<Subscriber>();

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

        public void SaveData()
        {
            DataStorage.Save(SubscriberList, Path.Join("data", "Subscribers.json"));
        }

        public List<Subscription> GetEnabledSubscriptions()
        {
            return GetSubscribers()
                .SelectMany(s => s.Subscriptions)
                .Where(s => s.Enabled)
                .ToList();
        }
    }
}
