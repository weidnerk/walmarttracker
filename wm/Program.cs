/*
 * 
 * listen to my voice memo on sending email "getting email to work"
 * 
 * 
 */
using dsmodels;
using scrapeAPI.Controllers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace wm
{
    class Program
    {
        static DataModelsDB db = new DataModelsDB();
        readonly static string _logfile = "scrape_log.txt";
        const string log_username = "admin";

        readonly static string HOME_DECOR_USER_ID = "65e09eec-a014-4526-a569-9f2d3600aa89";
        readonly static string EAGLE_USER_ID = "56aba33d-b046-41fb-b647-5bb42174a58b";

        static void Main(string[] args)
        {
            //string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "test email ", "revise listing");
            //if (!string.IsNullOrEmpty(ret))
            //{
            //    Console.WriteLine(ret);
            //}

            int outofstock = 0;
            Task.Run(async () =>
            {
                outofstock = await ScanItems();

            }).Wait();

            //Console.WriteLine("press any key to continue...");
            //Console.ReadKey();
        }

        public static async Task<int> ScanItems()
        {
            int i = 0;
            int outofstock = 0;
            int shippingNotAvailable = 0;
            int mispriceings = 0;
            var response = new List<string>();
            string body = null;
            string oosBody = null;

            var walListings = db.Listings.Where(x => x.SourceID == 1 && !x.OOS && x.Listed != null).ToList();

            foreach (Listing listing in walListings)
            {
                var settings = new UserSettingsView();
                if (listing.StoreID == 1)
                {
                    settings = db.UserSettingsView.Find(HOME_DECOR_USER_ID);
                }
                if (listing.StoreID == 4)
                {
                    settings = db.UserSettingsView.Find(EAGLE_USER_ID);
                }

                var w = await wallib.Class1.GetDetail(listing.SourceUrl);
                Console.WriteLine((++i));
                if (w == null)
                {
                    //response = scrapeAPI.ebayAPIs.ReviseItem(settings, listing.ListedItemID, qty: 0);
                    //await db.UpdateOOS(listing.ListedItemID, true);

                    Console.WriteLine(listing.Title);
                    ++outofstock;
                    //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "INVALID URL " + listing.Title, "revise listing");
                }
                else
                {
                    //Console.WriteLine(w.Price);
                    if (w.OutOfStock)
                    {
                        response = scrapeAPI.ebayAPIs.ReviseItem(settings, listing.ListedItemID, qty: 0);
                        await db.UpdateOOS(listing.ListedItemID, true);
                        Console.WriteLine(listing.Title);
                        Console.WriteLine("OOS");
                        Console.WriteLine("");
                        ++outofstock;

                        oosBody += "<br/><br/>" + listing.Title;

                        //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                        //string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing");
                    }
                    if (w.Price != listing.SupplierPrice)
                    {
                        decimal oldPrice = Math.Round(listing.ListingPrice, 2);
                        //response = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, price: (double)newPrice);
                        // await db.UpdatePrice(listing, (decimal)newPrice, w.Price);

                        ++mispriceings;
                        body += "<br/><br/>" + "<b>" + listing.Title + "</b>";
                        body += "<br/><br/>" + listing.ListedItemID + " db supplier price " + listing.SupplierPrice.ToString("c") + " different from just captured " + w.Price.ToString("c");
                        // Console.WriteLine(body);
                        if (w.Price < listing.SupplierPrice)
                        {
                            body += "<br/>Supplier dropped their price.";
                        }
                        else
                        {
                            body += "<br/>Supplier INCREASED their price!";
                        }
                        //Console.WriteLine(listing.Title);
                        Console.WriteLine(body);
                        Console.WriteLine("");
                        dsutil.DSUtil.WriteFile(_logfile, body, log_username);
                        body += "<br/><br/>";
                        // body += listing.SourceUrl;
                        // string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "PRICE CHANGE " + listing.Title, body);
                    }
                    if (w.ShippingNotAvailable)
                    {
                        //response = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, qty: 0);
                        //await db.UpdateOOS(listing.ListedItemID, true);
                        Console.WriteLine(listing.Title);
                        Console.WriteLine("ShippingNotAvailable");
                        ++shippingNotAvailable;
                        //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                        //string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "Shipping Not Available " + listing.Title, "revise listing");
                    }
                }
            }
            if (!string.IsNullOrEmpty(body))
            {
                string title = "PRICE CHANGE ";
                if (!string.IsNullOrEmpty(oosBody))
                {
                    oosBody = "<br/><br/>" + "<b>OUT OF STOCK</b>" + "<br/>" + oosBody;
                    body += oosBody;
                    title += "/OUT OF STOCK";
                }
                string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", title, body);
            }
            else
            {
                if (!string.IsNullOrEmpty(oosBody))
                {
                    oosBody += "<br/><br/>" + "OUT OF STOCK" + "<br/><br/> " + oosBody;
                }
                string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "OUT OF STOCK ", oosBody);
            }
            string msg = "Found " + outofstock.ToString() + " out of stock";
            Console.WriteLine(msg);
            dsutil.DSUtil.WriteFile(_logfile, msg, log_username);

            msg = "Found " + mispriceings.ToString() + " mispricings";
            Console.WriteLine(msg);
            dsutil.DSUtil.WriteFile(_logfile, msg, log_username);

            msg = "Found " + shippingNotAvailable.ToString() + " shippingNotAvailable";
            Console.WriteLine(msg);
            dsutil.DSUtil.WriteFile(_logfile, msg, log_username);

            return outofstock;
        }
    }
}
