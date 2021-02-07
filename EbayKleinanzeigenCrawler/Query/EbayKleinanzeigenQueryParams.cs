using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EbayKleinanzeigenCrawler.Query
{
    public class EbayKleinanzeigenQueryParams : QueryParams
    {
        public override string InvalidHtml { get => "<html><head><meta charset=\"utf-8\"><script>"; }
    }
}
