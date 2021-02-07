using EbayKleinanzeigenCrawler.Models;

namespace EbayKleinanzeigenCrawler.Manager.Telegram
{
    public class TelegramSubscriber : Subscriber
    {
        public long Id { get; set; }
        public TelegramInputState State { get; set; }
        public Subscription IncompleteSubscription { get; set; }

        public TelegramSubscriber()
        {
            State = TelegramInputState.Idle;
        }
    }
}
