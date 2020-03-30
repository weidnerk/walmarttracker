/*
 * Scan walmart and update price and inventory as needed.
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
        static string _toEmail = "ventures2019@gmail.com";
        static int _sourceID = 1;

        static DataModelsDB db = new DataModelsDB();
        readonly static string _logfile = "log.txt";
        const string log_username = "admin";

        // My ID is used for the tracker.
        readonly static string HOME_DECOR_USER_ID = "65e09eec-a014-4526-a569-9f2d3600aa89";
        readonly static string EAGLE_USER_ID = "56aba33d-b046-41fb-b647-5bb42174a58b";

        static void Main(string[] args)
        {
            int storeID;
            if (args.Length != 1)
            {
                Console.WriteLine("Invalid arguments.");
            }
            else
            {
                storeID = Convert.ToInt32(args[0]);
                string userID = UserID(storeID);
                string connStr = ConfigurationManager.ConnectionStrings["OPWContext"].ConnectionString;
                var settings = db.GetUserSettingsView(connStr, userID);
                var pctProfit = Convert.ToDouble(db.GetAppSetting("pctProfit"));
                var wmShipping = Convert.ToDecimal(db.GetAppSetting("Walmart shipping"));
                var wmFreeShippingMin = Convert.ToDecimal(db.GetAppSetting("Walmart free shipping min"));
                var eBayPct = Convert.ToDouble(db.GetAppSetting("eBay pct"));
                int imgLimit = Convert.ToInt32(db.GetAppSetting("Listing Image Limit"));

                int outofstock = 0;
                Task.Run(async () =>
                {
                    outofstock = await ScanItems(settings, _sourceID, pctProfit, wmShipping, wmFreeShippingMin, eBayPct, imgLimit);

                }).Wait();
            }
        }

        /// <summary>
        /// Return the user's GUID for a store for user=admin.
        /// </summary>
        /// <param name="storeID"></param>
        /// <returns></returns>
        protected static string UserID(int storeID)
        {
            string userID = null;
            switch (storeID)
            {
                case 1:
                    userID = HOME_DECOR_USER_ID;
                    break;
                case 4:
                    userID = EAGLE_USER_ID;
                    break;
            }
            return userID;
        }

        /// <summary>
        /// Main driver to scan listings for issues.
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="sourceID"></param>
        /// <param name="pctProfit"></param>
        /// <param name="wmShipping"></param>
        /// <param name="wmFreeShippingMin"></param>
        /// <param name="eBayPct"></param>
        /// <param name="imgLimit"></param>
        /// <returns></returns>
        public static async Task<int> ScanItems(UserSettingsView settings, 
            int sourceID, 
            double pctProfit, 
            decimal wmShipping, 
            decimal wmFreeShippingMin, 
            double eBayPct, 
            int imgLimit)
        {
            int i = 0;
            int outofstock = 0;
            int outofstockBadArrivalDate = 0;
            int invalidURL = 0;
            int shippingNotAvailable = 0;
            int mispriceings = 0;
            int numErrors = 0;
            var response = new List<string>();
            string body = null;
            int listingID = 0;

            var outofStockList = new List<string>();
            var outofStockBadArrivalList = new List<string>();
            var priceChangeList = new List<string>();
            var shipNotAvailList = new List<string>();
            var invalidURLList = new List<string>();
            var errors = new List<string>();

            try
            {
                string token = db.GetToken(settings);
                var walListings = db.Listings.Include(c => c.SellerListing).Include(d => d.SupplierItem).Where(x => x.SupplierItem.SourceID == sourceID && x.Qty > 0 && x.Listed != null && x.StoreID == settings.StoreID).ToList();

                foreach (Listing listing in walListings)
                {
                    try
                    {
                        listingID = listing.ID;
                        //if (listing.SupplierItem.ItemURL != "https://www.walmart.com/ip/Sentinel-18-Gun-Fully-Convertible-Cabinet-Black/19216477")
                        //{
                        //    continue;
                        //}
                        var wmItem = await wallib.wmUtility.GetDetail(listing.SupplierItem.ItemURL, imgLimit, true);
                        Console.WriteLine((++i) + " " + listing.SellerListing.Title);
                        if (wmItem == null)  // could not fetch from walmart website
                        {
                            invalidURLList.Add(listing.ListingTitle);

                            Console.WriteLine(listing.ListingTitle);
                            ++invalidURL;
                        }
                        else
                        {
                            if (wmItem.OutOfStock)
                            {
                                response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                listing.Qty = 0;
                                await db.ListingSaveAsync(settings, listing, "Qty");

                                if (!wmItem.Arrives.HasValue)
                                {
                                    outofStockBadArrivalList.Add(listing.ListingTitle);
                                    outofStockBadArrivalList.Add(listing.SupplierItem.ItemURL);
                                    ++outofstockBadArrivalDate;
                                }
                                else
                                {
                                    outofStockList.Add(listing.ListingTitle);
                                    ++outofstock;
                                }
                            }
                            else
                            {
                                if (Math.Round(wmItem.SupplierPrice.Value, 2) != Math.Round(listing.SupplierItem.SupplierPrice.Value, 2))
                                {
                                    priceChangeList.Add(listing.ListingTitle);
                                    var str = listing.ListedItemID + " db supplier price " + listing.SupplierItem.SupplierPrice.Value.ToString("c") + " different from just captured " + wmItem.SupplierPrice.Value.ToString("c");
                                    priceChangeList.Add(str);

                                    if (wmItem.SupplierPrice < listing.SupplierItem.SupplierPrice)
                                    {
                                        str = "Supplier dropped their price.";
                                        priceChangeList.Add(str);
                                    }
                                    else
                                    {
                                        str = "Supplier INCREASED their price!";
                                        priceChangeList.Add(str);
                                    }
                                    dsutil.DSUtil.WriteFile(_logfile, body, log_username);

                                    var priceProfit = wallib.wmUtility.wmNewPrice(wmItem.SupplierPrice.Value, pctProfit, wmShipping, wmFreeShippingMin, eBayPct);
                                    decimal newPrice = priceProfit.ProposePrice;
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, price: (double)newPrice);
                                    await db.UpdatePrice(listing, (decimal)newPrice, wmItem.SupplierPrice.Value);
                                    ++mispriceings;
                                }
                                if (wmItem.ShippingNotAvailable)
                                {
                                    shipNotAvailList.Add(listing.ListingTitle);
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, "Qty");

                                    ++shippingNotAvailable;
                                }
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        ++numErrors;
                        string msg = "ERROR IN LOOP listingID: " + listingID + " -> " + exc.Message;
                        errors.Add(msg);
                        dsutil.DSUtil.WriteFile(_logfile, msg, "");
                    }
                }
                if (mispriceings > 0)
                {
                    SendAlertEmail(_toEmail, "PRICE CHANGE", priceChangeList);
                }
                if (outofstock > 0)
                {
                    SendAlertEmail(_toEmail, "OUT OF STOCK - LABEL", outofStockList);
                }
                if (outofstockBadArrivalDate > 0)
                {
                    SendAlertEmail(_toEmail, "OUT OF STOCK - Bad Arrival Date", outofStockBadArrivalList);
                }
                if (invalidURL > 0)
                {
                    SendAlertEmail(_toEmail, "INVALID URL", invalidURLList);
                }
                if (shippingNotAvailable > 0)
                {
                    SendAlertEmail(_toEmail, "SHIPPING NOT AVAILABLE", shipNotAvailList);
                }
                if (numErrors > 0)
                {
                    SendAlertEmail(_toEmail, "TRACKER ERRORS", errors);
                }
                if (mispriceings + outofstock + invalidURL + shippingNotAvailable + numErrors == 0)
                {
                    string ret = dsutil.DSUtil.SendMailDev(_toEmail, string.Format("REPRICER - scanned {0} items", walListings.Count), "No issues found.");
                }
                return outofstock;
            }
            catch(Exception exc)
            {
                string msg = "listingID: " + listingID + " -> " + exc.Message;
                dsutil.DSUtil.WriteFile(_logfile, msg, "");
                return outofstock;
            }
        }

        /// <summary>
        /// Compose status email.
        /// </summary>
        /// <param name="toEmail"></param>
        /// <param name="title"></param>
        /// <param name="titles"></param>
        protected static void SendAlertEmail(string toEmail, string title, List<string> titles)
        {
            string body = null;
            foreach (string s in titles)
            {
                body += s + "<br/>";
            }
            if (!string.IsNullOrEmpty(body)) { 
                string ret = dsutil.DSUtil.SendMailDev(toEmail, title, body);
            }
        }
    }
}
