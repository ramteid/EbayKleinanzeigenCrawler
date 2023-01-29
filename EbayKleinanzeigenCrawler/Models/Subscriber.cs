using System.Collections.Generic;

namespace KleinanzeigenCrawler.Models
{
    public class Subscriber
    {
        public string Id { get; set; }
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