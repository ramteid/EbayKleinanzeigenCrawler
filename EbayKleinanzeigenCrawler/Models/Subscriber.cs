using System.Collections.Generic;

namespace EbayKleinanzeigenCrawler.Models
{
    public class Subscriber
    {
        public List<Subscription> Subscriptions { get; set; }

        protected Subscriber()
        {
            Subscriptions = new List<Subscription>();
        }
    }
}