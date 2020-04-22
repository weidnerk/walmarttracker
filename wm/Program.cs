/*
 * Scan walmart and update price and inventory as needed.
 * 
 * 100   deliveryTooLongList
 * 200   outofStockBadArrivalList
 * 300   outofStockList
 * 400   shipNotAvailList
 * 500   invalidURLList
 * 600   putBackInStockList
 * 700   priceChangeList

 * 10000 error

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
                var settings = db.GetUserSettingsView(connStr, userID, storeID);
                var pctProfit = settings.PctProfit;
                var wmShipping = Convert.ToDecimal(db.GetAppSetting(settings, "Walmart shipping"));
                var wmFreeShippingMin = Convert.ToDecimal(db.GetAppSetting(settings, "Walmart free shipping min"));
                var eBayPct = Convert.ToDouble(db.GetAppSetting(settings, "eBay pct"));
                int imgLimit = Convert.ToInt32(db.GetAppSetting(settings, "Listing Image Limit"));

                byte handlingTime = settings.HandlingTime;
                byte maxShippingDays = settings.MaxShippingDays;
                var allowedDeliveryDays = handlingTime + maxShippingDays;

                int outofstock = 0;

                Task.Run(async () =>
                {
                    outofstock = await ScanItems(settings, _sourceID, pctProfit, wmShipping, wmFreeShippingMin, eBayPct, imgLimit, allowedDeliveryDays, storeID);
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
            int imgLimit,
            int allowedDeliveryDays)
        {
            int i = 0;
            int outofstock = 0;
            int outofstockBadArrivalDate = 0;
            int invalidURL = 0;
            int shippingNotAvailable = 0;
            int mispriceings = 0;
            int numErrors = 0;
            int putBackInStock = 0;
            int deliveryTooLong = 0;
            var response = new List<string>();
            string body = null;
            int listingID = 0;

            var outofStockList = new List<string>();
            var outofStockBadArrivalList = new List<string>();
            var priceChangeList = new List<string>();
            var shipNotAvailList = new List<string>();
            var invalidURLList = new List<string>();
            var errors = new List<string>();
            var putBackInStockList = new List<string>();
            var deliveryTooLongList = new List<string>();

            try
            {
                DateTime startTime, endTime;
                startTime = DateTime.Now;

                string token = db.GetToken(settings);
                var walListings = db.Listings
                    .Include(d => d.SupplierItem)
                    .Where(x => x.SupplierItem.SourceID == sourceID && x.Listed != null && x.StoreID == settings.StoreID)
                    .ToList();

                foreach (Listing listing in walListings)
                {
                    try
                    {
                        listingID = listing.ID;
                        //if (listing.SupplierItem.ItemURL != "https://www.walmart.com/ip/Shop-Vac-6-Gallon-4-5-Peak-HP-Stainless-Steel-Wet-Dry-Vacuum/55042495")
                        //{
                        //    continue;
                        //}
                        var wmItem = await wallib.wmUtility.GetDetail(listing.SupplierItem.ItemURL, imgLimit, true);
                        Console.WriteLine((++i) + " " + listing.ListingTitle);
                        if (wmItem == null)  // could not fetch from walmart website
                        {
                            invalidURLList.Add(listing.ListingTitle);
                            invalidURLList.Add(listing.SupplierItem.ItemURL);
                            invalidURLList.Add(string.Format("Qty was {0}", listing.Qty));
                            invalidURLList.Add(string.Empty);
                            Console.WriteLine(listing.ListingTitle);
                            ++invalidURL;
                            var log = new ListingLog { ListingID = listing.ID, MsgID = 500 };
                            await db.ListingLogAdd(log);

                            if (listing.Qty > 0)
                            {
                                listing.Qty = 0;
                                await db.ListingSaveAsync(settings, listing, "Qty");
                                response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                            }
                        }
                        else
                        {
                            if (wmItem.ShippingNotAvailable)
                            {
                                shipNotAvailList.Add(listing.ListingTitle);
                                shipNotAvailList.Add(listing.SupplierItem.ItemURL);
                                shipNotAvailList.Add(string.Empty);
                                ++shippingNotAvailable;
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 400 };
                                await db.ListingLogAdd(log);

                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }
                            }
                            if (!wmItem.ShippingNotAvailable && wmItem.OutOfStock)
                            {
                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }                              
                                outofStockList.Add(listing.ListingTitle);
                                outofStockList.Add(listing.SupplierItem.ItemURL);
                                outofStockList.Add(string.Empty);
                                ++outofstock;
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 300 };
                                await db.ListingLogAdd(log);

                            }
                            if (!wmItem.OutOfStock && !wmItem.ShippingNotAvailable && !wmItem.Arrives.HasValue)
                            {
                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }                             
                                outofStockBadArrivalList.Add(listing.ListingTitle);
                                outofStockBadArrivalList.Add(listing.SupplierItem.ItemURL);
                                outofStockBadArrivalList.Add(string.Empty);
                                ++outofstockBadArrivalDate;
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 200 };
                                await db.ListingLogAdd(log);
                            }
                            bool lateDelivery = false;
                            if (wmItem.Arrives.HasValue)
                            {
                                int days = dsutil.DSUtil.GetBusinessDays(DateTime.Now, wmItem.Arrives.Value);
                                if (days > allowedDeliveryDays)
                                {
                                    lateDelivery = true;
                                    ++deliveryTooLong;
                                    deliveryTooLongList.Add(listing.ListingTitle);
                                    deliveryTooLongList.Add(listing.SupplierItem.ItemURL);
                                    deliveryTooLongList.Add(string.Format("{0} days", days));
                                    var note = string.Format("{0} days", days);
                                    deliveryTooLongList.Add(string.Format("Qty was {0}", listing.Qty));
                                    note += string.Format(" (Qty was {0})", listing.Qty);
                                    deliveryTooLongList.Add(string.Empty);
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 100, Note = note };
                                    await db.ListingLogAdd(log);

                                    if (listing.Qty > 0)
                                    {
                                        listing.Qty = 0;
                                        await db.ListingSaveAsync(settings, listing, "Qty");
                                        response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                    }
                                }
                            }
                            if (listing.Qty == 0 && !wmItem.OutOfStock && !wmItem.ShippingNotAvailable && !lateDelivery && (wmItem.SoldAndShippedBySupplier ?? false))
                            {
                                ++putBackInStock;
                                putBackInStockList.Add(listing.ListingTitle);
                                putBackInStockList.Add(listing.SupplierItem.ItemURL);
                                putBackInStockList.Add(string.Empty);
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 600 };
                                await db.ListingLogAdd(log);

                            }
                            else
                            {
                                if (Math.Round(wmItem.SupplierPrice.Value, 2) != Math.Round(listing.SupplierItem.SupplierPrice.Value, 2))
                                {
                                    priceChangeList.Add(listing.ListingTitle);
                                    var str = listing.ListedItemID + " db supplier price " + listing.SupplierItem.SupplierPrice.Value.ToString("c") + " different from just captured " + wmItem.SupplierPrice.Value.ToString("c");
                                    string note = "db supplier price " + listing.SupplierItem.SupplierPrice.Value.ToString("c") + " different from just captured " + wmItem.SupplierPrice.Value.ToString("c");
                                    priceChangeList.Add(str);

                                    if (wmItem.SupplierPrice < listing.SupplierItem.SupplierPrice)
                                    {
                                        str = "Supplier dropped their price.";
                                        note += " " + str;
                                        priceChangeList.Add(str);
                                        priceChangeList.Add(listing.SupplierItem.ItemURL);
                                    }
                                    else
                                    {
                                        str = "Supplier INCREASED their price!";
                                        note += " " + str;
                                        priceChangeList.Add(str);
                                        priceChangeList.Add(listing.SupplierItem.ItemURL);
                                    }
                                    dsutil.DSUtil.WriteFile(_logfile, body, log_username);

                                    var priceProfit = wallib.wmUtility.wmNewPrice(wmItem.SupplierPrice.Value, pctProfit, wmShipping, wmFreeShippingMin, eBayPct);
                                    decimal newPrice = priceProfit.ProposePrice;
                                    priceChangeList.Add(string.Format("New price: {0:c}", newPrice));
                                    note += string.Format(" New price: {0:c}", newPrice);
                                    priceChangeList.Add(string.Empty);
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, price: (double)newPrice);
                                    await db.UpdatePrice(listing, (decimal)newPrice, wmItem.SupplierPrice.Value);
                                    ++mispriceings;
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 700, Note = note };
                                    await db.ListingLogAdd(log);

                                }
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        ++numErrors;
                        var log = new ListingLog { ListingID = listing.ID, MsgID = 10000 };
                        await db.ListingLogAdd(log);

                        string msg = "ERROR IN LOOP -> " + listing.ListingTitle + " -> " + exc.Message;
                        errors.Add(msg);
                        dsutil.DSUtil.WriteFile(_logfile, msg, "");
                    }
                }

                endTime = DateTime.Now;
                double elapsedMinutes = ((TimeSpan)(endTime - startTime)).TotalMinutes;

                if (mispriceings > 0)
                {
                    foreach(var s in priceChangeList)
                    {
                    }
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
                    SendAlertEmail(_toEmail, "DELIVERY NOT AVAILABLE", shipNotAvailList);
                }
                if (putBackInStock > 0)
                {
                    SendAlertEmail(_toEmail, "POSSIBLY RE-STOCK " + settings.StoreName, putBackInStockList);
                }
                if (deliveryTooLong > 0)
                {
                    SendAlertEmail(_toEmail, "DELIVERY TOO LONG", deliveryTooLongList);
                }
                if (numErrors > 0)
                {
                    SendAlertEmail(_toEmail, "TRACKER ERRORS", errors);
                }
                if (mispriceings + outofstock + invalidURL + shippingNotAvailable + numErrors + putBackInStock == 0)
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
