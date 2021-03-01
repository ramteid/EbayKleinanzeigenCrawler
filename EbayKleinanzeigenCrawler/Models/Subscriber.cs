using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Models
{
    public class Subscriber<TId>
    {
        public TId Id { get; set; }
        public InputState State { get; set; }
        public Subscription IncompleteSubscription { get; set; }

        public List<Subscription> Subscriptions { get; set; }

        public Subscriber()
        {
            State = InputState.Idle;
            Subscriptions = new List<Subscription>();
        }
    }
}