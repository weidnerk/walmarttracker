using dsmodels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace wm
{
    class Program
    {
        static DataModelsDB db = new DataModelsDB();

        static void Main(string[] args)
        {
            int outofstock = 0;
            Task.Run(async () =>
            {
                outofstock = await ScanItems(); 

            }).Wait();

            Console.WriteLine("Found " + outofstock.ToString() + " out of stock");
            //Console.WriteLine("press any key to continue...");
            //Console.ReadKey();
        }

        public static async Task<int> ScanItems()
        {
            int i = 0;
            int outofstock = 0;
            var walListings = db.Listings.Where(x => x.SourceID == 1 && !x.OOS).ToList();
            foreach (ListingX listing in walListings)
            {
                var w = await wallib.Class1.GetDetail(listing.Source);
                Console.WriteLine((++i) + " " + listing.Title);
                if (w.OutOfStock)
                {
                    Console.WriteLine(listing.Title);
                    ++outofstock;
                    //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing");
                    string reviseResult = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, qty: 0);
                    await db.UpdateOOS(listing.ListedItemID, true);
                }
            }
            return outofstock;
        }
    }
}
