using EbayKleinanzeigenCrawler.ErrorHandling;

namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IErrorStatistics
    {
        void AmendErrorStatistic(ErrorType errorType);
        void NotifyOnThreshold();
    }
}