using System;
using System.Net;

namespace KleinanzeigenCrawler.Query;

/// <summary>
/// The server answered that the page does not exist (anymore), so loading it again will not help
/// </summary>
public class PageNotFoundException : Exception
{
    public Uri Url { get; }
    public HttpStatusCode StatusCode { get; }

    public PageNotFoundException(Uri url, HttpStatusCode statusCode)
        : base($"The server responded with {(int)statusCode} {statusCode} for {url}")
    {
        Url = url;
        StatusCode = statusCode;
    }
}
