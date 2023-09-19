using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EbayKleinanzeigenCrawler.Persistence;

internal class AlreadyProcessedUrlsPersistence : IAlreadyProcessedUrlsPersistence
{
    private static readonly object _lockObject = new();
    private readonly ILogger _logger;
    private readonly IDataStorage _dataStorage;
    private ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>> _alreadyProcessedUrlsPerSubscription;
    private readonly string _filePath = Path.Join("data", "AlreadyProcessedUrls.json");

    public AlreadyProcessedUrlsPersistence(ILogger logger, IDataStorage dataStorage)
    {
        Directory.CreateDirectory("data");
        _logger = logger;
        _dataStorage = dataStorage;
    }

    public void AddOrUpdate(Guid key, List<AlreadyProcessedUrl> data)
    {
        lock(_lockObject)
        {
            _alreadyProcessedUrlsPerSubscription.AddOrUpdate(key, data, (_, __) => data);
        }
    }

    public List<AlreadyProcessedUrl> GetAlreadyProcessedLinksForSubscription(Guid subscriptionId)
    {
        lock(_lockObject)
        {
            return _alreadyProcessedUrlsPerSubscription.GetOrAdd(subscriptionId, valueFactory: _ => new List<AlreadyProcessedUrl>());
        }
    }

    public void RestoreData()
    {
        lock (_lockObject)
        {
            try
            {
                _dataStorage.Load(_filePath, out ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>> data);
                _alreadyProcessedUrlsPerSubscription = data;
                _logger.Information($"Restored processed URLs for {_alreadyProcessedUrlsPerSubscription.Count} subscriptions");
            }
            catch (FileNotFoundException e)
            {
                _logger.Warning($"File '{_filePath}' not found ('{e.Message}'). Starting clean.");
                _alreadyProcessedUrlsPerSubscription = new ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>>();
            }
            catch (Exception e)
            {
                _logger.Error(e, $"Error when restoring subscribers: '{e.Message}'. Starting clean.");
                _alreadyProcessedUrlsPerSubscription = new ConcurrentDictionary<Guid, List<AlreadyProcessedUrl>>();
            }

            // Make sure the in-memory storage and the file are in sync
            PersistData();
        }
    }

    public void PersistData()
    {
        lock(_lockObject)
        {
            // Remove urls which have not been found for more than X days as it's safe to assume they won't be found again
            // Hint: If the app was turned off longer than that, there might be some duplicate notifications about matches.
            var emptySubscriptions = new List<Guid>();
            foreach (var element in _alreadyProcessedUrlsPerSubscription)
            {
                var urls = element.Value
                    .Where(url => url.LastFound > DateTime.Now - TimeSpan.FromDays(31))?
                    .ToList();
                
                if (urls.Any())
                {
                    AddOrUpdate(element.Key, urls);
                }
                else
                {
                    emptySubscriptions.Add(element.Key);
                }
            }

            foreach (var emptySubscription in emptySubscriptions)
            {
                _alreadyProcessedUrlsPerSubscription.Remove(emptySubscription, out _);
            }

            _dataStorage.Save(_alreadyProcessedUrlsPerSubscription, _filePath);
        }
    }
}