using System;
using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IAlreadyProcessedUrlsPersistence
    {
        List<Uri> GetOrAddSubscription(Guid id);
        bool RestoreData();
        void SaveData();
    }
}