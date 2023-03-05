using System;

namespace EbayKleinanzeigenCrawler.ErrorHandling
{
    public class ErrorEntry
    {
        public DateTime Timestamp { get; init; }
        public ErrorType ErrorType { get; init; }
    }
}
