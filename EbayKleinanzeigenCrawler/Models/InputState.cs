namespace KleinanzeigenCrawler.Models
{
    public enum InputState
    {
        Idle,
        WaitingForUrl,
        WaitingForIncludeKeywords,
        WaitingForExcludeKeywords,
        WaitingForInitialPull,
        WaitingForTitle,
        WaitingForSubscriptionToDelete,
        WaitingForTitleToDisable,
        WaitingForTitleToEnable
    }
}
