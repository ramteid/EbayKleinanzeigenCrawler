using EbayKleinanzeigenCrawler.Models;
using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface IAlreadyProcessedUrlsPersistence
{
    void AddOrUpdate(Guid key, List<AlreadyProcessedUrl> data);
    List<AlreadyProcessedUrl> GetAlreadyProcessedLinksForSubscription(Guid id);
    void RestoreData();
    void PersistData();
}