using System;

namespace EbayKleinanzeigenCrawler.Models
{
    public class Result
    {
        public Uri Link { get; set; }
        public string CreationDate { get; set; }
        public string Price { get; set; }

        public override bool Equals(object obj)
        {
            Result x = this;
            var y = obj as Result;

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

            return Equals(x.Link, y.Link);
        }

        public override int GetHashCode()
        {
            return Link.GetHashCode();
        }
    }
}
