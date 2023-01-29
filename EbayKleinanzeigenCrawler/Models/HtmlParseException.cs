using System;

namespace KleinanzeigenCrawler.Models
{
    class HtmlParseException : Exception
    {
        public HtmlParseException(string message) : base(message)
        {
        }
    }
}
