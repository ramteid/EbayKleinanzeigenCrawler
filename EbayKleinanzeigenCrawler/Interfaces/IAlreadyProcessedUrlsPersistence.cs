using EbayKleinanzeigenCrawler.Models;
using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Interfaces;

public interface IAlreadyProcessedUrlsPersistence
{
    List<AlreadyProcessedUrl> GetAlreadyProcessedLinksForSubscription(Guid id);
    void RestoreData();
    void SaveData();
}