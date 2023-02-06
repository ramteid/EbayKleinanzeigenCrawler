using System;

namespace EbayKleinanzeigenCrawler.Models;

public class Result
{
    public Uri Link { get; set; }
    public string CreationDate { get; set; }
    public string Price { get; set; }
}