using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Jobs;
using EbayKleinanzeigenCrawler.Models;
using Serilog;

namespace EbayKleinanzeigenCrawler.Scheduler
{
    /// <summary>
    /// Rudimentary scheduler
    /// TODO: Introduce a proper, more intelligent scheduler
    /// </summary>
    public class JobScheduler
    {
        private readonly ISubscriptionManager _subscriptionManager;
        private readonly IJobFactory _jobFactory;
        private readonly ILogger _logger;
        private readonly IDataStorage _dataStorage;
        private ConcurrentDictionary<Guid, List<Uri>> _alreadyProcessedUrls;

        public JobScheduler(ISubscriptionManager subscriptionManager, IJobFactory jobFactory, ILogger logger, IDataStorage dataStorage)
        {
            _subscriptionManager = subscriptionManager;
            _jobFactory = jobFactory;
            _logger = logger;
            _dataStorage = dataStorage;
            RestoreData();
        }

        public void Run()
        {
            while (true)
            {
                List<Subscription> subscriptions = _subscriptionManager.GetDistinctSubscriptions();
                _logger.Information($"Found {subscriptions.Count} distinct subscriptions");
                foreach (Subscription subscription in subscriptions)
                {
                    _logger.Information($"Processing subscription '{subscription.Title}' {subscription.Id}");
                    ExecuteJobForSubscription(subscription);
                    _logger.Information($"Finished processing subscription '{subscription.Title}' {subscription.Id}");
                }
                SaveData();
                
                _logger.Information("Processed all subscriptions. Waiting 5 minutes...");
                Thread.Sleep(TimeSpan.FromMinutes(5));
            }
        }

        private void ExecuteJobForSubscription(Subscription subscription)
        {
            CrawlJob job = _jobFactory.CreateInstance();
            List<Uri> alreadyProcessedUrls = _alreadyProcessedUrls.GetOrAdd(subscription.Id, valueFactory: _ => new List<Uri>());
            job.Execute(subscription, alreadyProcessedUrls);
        }

        private void RestoreData()  // TODO: move restoring logic into IDataStorage
        {
            try
            {
                _dataStorage.Load("AlreadyProcessedUrls.json", out ConcurrentDictionary<Guid, List<Uri>> data); // TODO: remove URLs for deleted Subscriptions
                _alreadyProcessedUrls = data;
                _logger.Information($"Restored processed URLs for {data.Count} subscriptions");
            }
            catch (FileNotFoundException e)
            {
                if (_alreadyProcessedUrls is null)
                {
                    _alreadyProcessedUrls = new ConcurrentDictionary<Guid, List<Uri>>();
                }

                if (e is FileNotFoundException)
                {
                    _logger.Warning($"Could not restore subscribers: {e.Message}");
                }
                else
                {
                    _logger.Error(e, $"Error when restoring subscribers: {e.Message}");
                }
            }
        }

        private void SaveData()
        {
            _dataStorage.Save(_alreadyProcessedUrls, "AlreadyProcessedUrls.json");
        }
    }
}
