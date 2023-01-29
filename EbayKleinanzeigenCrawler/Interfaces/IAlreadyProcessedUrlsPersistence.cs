using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IAlreadyProcessedUrlsPersistence
    {
        List<Uri> GetOrAdd(Guid id);
        bool RestoreData();
        void SaveData();
    }
}