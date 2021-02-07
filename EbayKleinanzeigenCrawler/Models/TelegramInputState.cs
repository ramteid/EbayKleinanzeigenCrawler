namespace EbayKleinanzeigenCrawler.Models
{
    public enum TelegramInputState
    {
        Idle,
        WaitingForUrl,
        WaitingForIncludeKeywords,
        WaitingForExcludeKeywords,
        WaitingForInitialPull,
        WaitingForTitle
    }
}
