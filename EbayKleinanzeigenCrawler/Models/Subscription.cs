using System;
using System.Collections.Generic;
using System.Linq;

namespace EbayKleinanzeigenCrawler.Models
{
    public class Subscription
    {
        public Guid Id { get; set; }
        public string Title { get; set; }
        public Uri QueryUrl { get; set; }
        public List<string> IncludeKeywords { get; set; }
        public List<string> ExcludeKeywords { get; set; }
        public bool InitialPull { get; set; }
        public bool Enabled { get; set; }

        public Subscription()
        {
            Id = Guid.NewGuid();
        }

        public override bool Equals(object obj)
        {
            Subscription x = this;
            var y = obj as Subscription;

            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (y is null)
            {
                return false;
            }

            if (x.GetType() != y.GetType())
            {
                return false;
            }

            return Equals(x.QueryUrl, y.QueryUrl) && x.IncludeKeywords.SequenceEqual(y.IncludeKeywords) && x.ExcludeKeywords.SequenceEqual(y.ExcludeKeywords);
        }

        public override int GetHashCode()
        {
            return QueryUrl.GetHashCode() ^ string.Join("", IncludeKeywords).GetHashCode() ^ string.Join("", ExcludeKeywords).GetHashCode();
        }
    }
}
