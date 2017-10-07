using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace DemoWebsite.Web.Models
{
    public class DetailsModel
    {
        public DetailsModel()
        {
            Celebrities = new List<DetailItem>();
            Landmarks = new List<DetailItem>();
        }

        public List<DetailItem> Celebrities { get; set; }

        public List<DetailItem> Landmarks { get; set; }
    }

    public class DetailItem
    {
        public string Name { get; set; }

        public Double Score { get; set; }
    }
}