using System;

namespace EbayKleinanzeigenCrawler.Models
{
    public class AlreadyProcessedUrl
    {
        public Uri Uri { get; init; }
        public DateTime LastFound { get; set; }
    }
}
