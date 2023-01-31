using EbayKleinanzeigenCrawler.Models;
using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IAlreadyProcessedUrlsPersistence
    {
        List<AlreadyProcessedUrl> GetAlreadyProcessedLinksForSubscripition(Guid id);
        void RestoreData();
        void SaveData();
    }
}