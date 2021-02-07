using System;

namespace EbayKleinanzeigenCrawler.Models
{
    class HtmlParseException : Exception
    {
        public HtmlParseException(string message) : base(message)
        {
        }
    }
}
