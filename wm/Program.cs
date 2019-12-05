/*
 * 
 * listen to my voice memo on sending email "getting email to work"
 * 
 * 
 */
using dsmodels;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Data.Entity;

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
            string connStr = ConfigurationManager.ConnectionStrings["OPWContext"].ConnectionString;
            int outofstock = 0;
            Task.Run(async () =>
            {
                outofstock = await ScanItems(connStr, 1);

            }).Wait();

            //Console.WriteLine("press any key to continue...");
            //Console.ReadKey();
        }

        public static async Task<int> ScanItems(string connStr, int storeID)
        {
            int i = 0;
            int outofstock = 0;
            int shippingNotAvailable = 0;
            int mispriceings = 0;
            var response = new List<string>();
            string body = null;
            string oosBody = null;

            try
            {
                var settings = new UserSettingsView();
                if (storeID == 1)
                {
                    settings = db.GetUserSettings(connStr, HOME_DECOR_USER_ID);
                }
                if (storeID == 4)
                {
                    settings = db.GetUserSettings(connStr, EAGLE_USER_ID);
                }
                var walListings = db.Listings.Include(c => c.SellerListing).Where(x => x.SourceID == 1 && !x.OOS && x.Listed != null && x.StoreID == storeID).ToList();

                foreach (Listing listing in walListings)
                {
                    var wmItem = await wallib.wmUtility.GetDetail(listing.SourceUrl);
                    Console.WriteLine((++i) + " " + listing.SellerListing.Title);
                    if (wmItem == null)  // could not fetch from walmart website
                    {
                        //response = scrapeAPI.ebayAPIs.ReviseItem(settings, listing.ListedItemID, qty: 0);
                        //await db.UpdateOOS(listing.ListedItemID, true);

                        Console.WriteLine(listing.SellerListing.Title);
                        ++outofstock;
                        string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "INVALID URL " + listing.SellerListing.Title, "revise listing");
                    }
                    else
                    {
                        if (wmItem.OutOfStock)
                        {
                            response = Utility.eBayItem.ReviseItem(settings, listing.ListedItemID, qty: 0);
                            await db.OOSUpdate(listing.ListedItemID, true);
                            ++outofstock;

                            oosBody += "<br/><br/>" + listing.SellerListing.Title;

                            //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                            //string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing");
                        }
                        if (wmItem.SupplierPrice != listing.SupplierPrice)
                        {
                            decimal newPrice = Utility.eBayItem.wmNewPrice(wmItem.SupplierPrice.Value, 5);
                            response = Utility.eBayItem.ReviseItem(settings, listing.ListedItemID, price: (double)newPrice);
                            await db.UpdatePrice(listing, (decimal)newPrice, wmItem.SupplierPrice.Value);

                            ++mispriceings;
                            body += "<br/><br/>" + "<b>" + listing.SellerListing.Title + "</b>";
                            body += "<br/><br/>" + listing.ListedItemID + " db supplier price " + listing.SupplierPrice.ToString("c") + " different from just captured " + wmItem.SupplierPrice.Value.ToString("c");

                            if (wmItem.SupplierPrice < listing.SupplierPrice)
                            {
                                body += "<br/>Supplier dropped their price.";
                            }
                            else
                            {
                                body += "<br/>Supplier INCREASED their price!";
                            }
                            dsutil.DSUtil.WriteFile(_logfile, body, log_username);
                            body += "<br/><br/>";
                        }
                        if (wmItem.ShippingNotAvailable)
                        {
                            //response = scrapeAPI.ebayAPIs.ReviseItem(listing.ListedItemID, qty: 0);
                            //await db.UpdateOOS(listing.ListedItemID, true);
                            Console.WriteLine(listing.SellerListing.Title);
                            Console.WriteLine("ShippingNotAvailable");
                            ++shippingNotAvailable;
                            //string ret = await dsutil.DSUtil.SendMailProd("ventures2019@gmail.com", "OUT OF STO " + listing.Title, "revise listing", "localhost");
                            //string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "Shipping Not Available " + listing.Title, "revise listing");
                        }
                    }
                }
                if (mispriceings > 0)
                {
                    string title = "PRICE CHANGE ";
                    if (outofstock > 0)
                    {
                        oosBody = "<br/><br/>" + "<b>OUT OF STOCK</b>" + "<br/>" + oosBody;
                        body += oosBody;
                        title += "/OUT OF STOCK";
                    }
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", title, body);
                }
                else
                {
                    if (outofstock > 0)
                    {
                        oosBody += "<br/><br/>" + "OUT OF STOCK" + "<br/><br/> " + oosBody;
                        string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "OUT OF STOCK ", oosBody);
                    }
                }
                if (mispriceings == 0 && outofstock == 0)
                {
                    string ret = dsutil.DSUtil.SendMailDev("ventures2019@gmail.com", "WM TRACKER", "No discrepencies found.");
                }
                string msg = "Found " + outofstock.ToString() + " out of stock";
                dsutil.DSUtil.WriteFile(_logfile, msg, log_username);

                msg = "Found " + mispriceings.ToString() + " mispricings";
                dsutil.DSUtil.WriteFile(_logfile, msg, log_username);

                msg = "Found " + shippingNotAvailable.ToString() + " shippingNotAvailable";
                dsutil.DSUtil.WriteFile(_logfile, msg, log_username);

                return outofstock;
            }
            catch(Exception exc)
            {
                string msg = exc.Message;
                return outofstock;
            }
        }
    }
}
