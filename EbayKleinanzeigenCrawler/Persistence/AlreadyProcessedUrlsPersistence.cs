using EbayKleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace EbayKleinanzeigenCrawler.Persistence
{
    internal class AlreadyProcessedUrlsPersistence : IAlreadyProcessedUrlsPersistence
    {

        private readonly ILogger _logger;
        private readonly IDataStorage DataStorage;
        private ConcurrentDictionary<Guid, List<Uri>> _alreadyProcessedUrls = new();

        public AlreadyProcessedUrlsPersistence(ILogger logger, IDataStorage dataStorage)
        {
            Directory.CreateDirectory("data");
            _logger = logger;
            DataStorage = dataStorage;
            RestoreData();
        }

        public List<Uri> GetOrAdd(Guid id)
        {
            return _alreadyProcessedUrls.GetOrAdd(id, valueFactory: _ => new List<Uri>());
        }

        public bool RestoreData()  // TODO: move restoring logic into IDataStorage
        {
            try
            {
                DataStorage.Load(Path.Join("data", "AlreadyProcessedUrls.json"), out ConcurrentDictionary<Guid, List<Uri>> data); // TODO: remove URLs for deleted Subscriptions
                _alreadyProcessedUrls = data;
                _logger.Information($"Restored processed URLs for {data.Count} subscriptions");
            }
            catch (Exception e)
            {
                _alreadyProcessedUrls ??= new ConcurrentDictionary<Guid, List<Uri>>();

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
            DataStorage.Save(_alreadyProcessedUrls, Path.Join("data", "AlreadyProcessedUrls.json"));
        }
    }
}
