using dsmodels;
using scrapeAPI.Controllers;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace wm
{
    class Program
    {
        static DataModelsDB db = new DataModelsDB();
        readonly static string _logfile = "scrape_log.txt";

        static void Main(string[] args)
        {
            int outofstock = 0;
            Task.Run(async () =>
            {
                outofstock = await ScanItems(); 

            }).Wait();

            // Console.WriteLine("press any key to continue...");
            // Console.ReadKey();
        }

        public static async Task<int> ScanItems()
        {
            int i = 0;
            int outofstock = 0;
            int shippingNotAvailable = 0;
            int mispriceings = 0;
            var walListings = db.Listings.Where(x => x.SourceID == 1 && !x.OOS).ToList();
            foreach (ListingX listing in walListings)
            {
                var w = await wallib.Class1.GetDetail(listing.Source);
                Console.WriteLine((++i) + " " + listing.Title);
                //Console.WriteLine(w.Price);
                if (w.OutOfStock)
                {
                    string reviseResult = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, qty: 0);
                    await db.UpdateOOS(listing.ListedItemID, true);

                    Console.WriteLine(listing.Title);
                    ++outofstock;
                    //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing");
                }
                if (w.Price > listing.SupplierPrice)
                {
                    string reviseResult = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, qty: 0);
                    await db.UpdateOOS(listing.ListedItemID, true);

                    ++mispriceings;
                    string body = listing.ListedItemID + " listing price: " + listing.ListingPrice + " source price: " + w.Price;
                    Console.WriteLine(body);
                    dsutil.DSUtil.WriteFile(_logfile, body);
                    body += "\n";
                    body += listing.Source;
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "PRICE INCREASE " + listing.Title, body);
                }
                if (w.ShippingNotAvailable)
                {
                    string reviseResult = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, qty: 0);
                    await db.UpdateOOS(listing.ListedItemID, true);

                    Console.WriteLine(listing.Title);
                    ++shippingNotAvailable;
                    //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "Shipping Not Available " + listing.Title, "revise listing");
                }
            }
            string msg = "Found " + outofstock.ToString() + " out of stock";
            Console.WriteLine(msg);
            dsutil.DSUtil.WriteFile(_logfile, msg);

            msg = "Found " + mispriceings.ToString() + " mispricings";
            Console.WriteLine(msg);
            dsutil.DSUtil.WriteFile(_logfile, msg);

            msg = "Found " + shippingNotAvailable.ToString() + " shippingNotAvailable";
            Console.WriteLine(msg);
            dsutil.DSUtil.WriteFile(_logfile, msg);

            return outofstock;
        }
    }
}
