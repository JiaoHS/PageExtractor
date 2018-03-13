using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PageExtractor
{
    public class SearchViewModel
    {
        public int id { get; set; }
        public long PublishDate { get; set; }
        public string Url { get; set; }
        public string Html { get; set; }
        public string Title { get; set; }
        public string Type { get; set; }

        public string Urllist { get; set; }
        public string Classified { get; set; }
        public string Rank { get; set; }
        public int Recommend { get; set; }
    }
}
