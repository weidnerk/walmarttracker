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
using System.Threading;
using eBayUtility;
using System.Data.Entity.Migrations.Infrastructure;

namespace wm
{
    class Program
    {
        static string _toEmail = "ventures2019@gmail.com";
        static int _sourceID = 1;

        static DataModelsDB db = new DataModelsDB();
        //readonly static string _logfile = "log.txt";
        const string log_username = "admin";

        // My ID is used for the tracker.
        readonly static string HOME_DECOR_USER_ID = "65e09eec-a014-4526-a569-9f2d3600aa89";
        readonly static string EAGLE_USER_ID = "56aba33d-b046-41fb-b647-5bb42174a58b";

        static void Main(string[] args)
        {
            int daysBack = 21;
            UserSettingsView settings = null;
            string logfile = null;
            try 
            { 
                int storeID;
                if (args.Length != 3)
                {
                    Console.WriteLine("Invalid arguments.");
                }
                else
                {
                    logfile = args[1];
                    storeID = Convert.ToInt32(args[0]);
                    daysBack = Convert.ToInt32(args[2]);
                    string userID = UserID(storeID);
                    string connStr = ConfigurationManager.ConnectionStrings["OPWContext"].ConnectionString;
                    settings = db.GetUserSettingsView(connStr, userID, storeID);
                    var wmShipping = Convert.ToDecimal(db.GetAppSetting(settings, "Walmart shipping"));
                    var wmFreeShippingMin = Convert.ToDecimal(db.GetAppSetting(settings, "Walmart free shipping min"));
                    int imgLimit = Convert.ToInt32(db.GetAppSetting(settings, "Listing Image Limit"));

                    byte handlingTime = settings.HandlingTime;
                    byte maxShippingDays = settings.MaxShippingDays;
                    var allowedDeliveryDays = handlingTime + maxShippingDays;

                    int outofstock = 0;

                    Task.Run(async () =>
                    { 
                        await GetOrders(settings, logfile);
                    }).Wait();

                    Task.Run(async () =>
                    {
                        outofstock = await ScanItems(settings, _sourceID, wmShipping, wmFreeShippingMin, settings.FinalValueFeePct, imgLimit, allowedDeliveryDays, logfile, daysBack);
                    }).Wait();
                }
            }
            catch (Exception exc)
            {
                string msg = dsutil.DSUtil.ErrMsg("Main", exc);
                dsutil.DSUtil.WriteFile(logfile, msg, settings.UserName);
            }
        }

        static async Task GetOrders(UserSettingsView settings, string logfile)
        {
            try
            {
                DateTime ed = DateTime.Now;
                DateTime sd = ed.AddHours(-3);
                var orders = ebayAPIs.GetOrdersByDate(settings, sd, ed);
                if (orders.Count > 0)
                {
                    var msg = new List<string>();
                    foreach(var o in orders)
                    {
                        // sync db qty
                        var sellerListing = await ebayAPIs.GetSingleItem(settings, o.ListedItemID, false);
                        var listing = db.ListingGet(o.ListedItemID);

                        listing.Qty = sellerListing.Qty.Value;
                        await db.ListingSaveAsync(settings, listing, false, "Qty");

                        msg.Add(o.ListedItemID);
                        msg.Add(listing.ListingTitle);
                        msg.Add(o.Buyer);
                        msg.Add(o.DatePurchased.ToString());
                        msg.Add(o.Qty.ToString());
                        msg.Add("");
                    }
                    SendAlertEmail(_toEmail, settings.StoreName + " ORDERS", msg);
                }
            }
            catch (Exception exc)
            {
                string msg = dsutil.DSUtil.ErrMsg("GetOrders", exc);
                dsutil.DSUtil.WriteFile(logfile, msg, settings.UserName);
                throw;
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
            decimal wmShipping, 
            decimal wmFreeShippingMin, 
            double eBayPct, 
            int imgLimit,
            int allowedDeliveryDays,
            string logfile,
            int daysBack)
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
            int parseArrivalDate = 0;
            int notWalmart = 0;
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
            var parseArrivalDateList = new List<string>();
            var notWalmartList = new List<string>();

            try
            {
                DateTime startTime, endTime;
                startTime = DateTime.Now;

                string token = db.GetToken(settings);
                var walListings = db.Listings
                    .Include(d => d.SupplierItem)
                    .Where(x => x.SupplierItem.SourceID == sourceID && x.Listed != null && x.StoreID == settings.StoreID && !x.InActive)
                    .ToList();

                foreach (Listing listing in walListings)
                {
                    try
                    {
                        Random random = new Random();
                        int sec = random.Next(2);
                        Thread.Sleep(sec * 1000);

                        listingID = listing.ID;

                        //if (listing.SupplierItem.ItemURL != "https://www.walmart.com/ip/ShelterLogic-Super-Max-12-x-20-White-Canopy-Enclosure-Kit/17665893")
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

                            int cnt = CountMsgID(listing.ID, 500, daysBack);
                            int total = CountMsgID(listing.ID, 0, daysBack);
                            invalidURLList.Add(string.Format("Invalid URL: {0}/{1}", cnt, total));
                            invalidURLList.Add(string.Empty);

                            ++invalidURL;
                            var log = new ListingLog { ListingID = listing.ID, MsgID = 500, UserID = settings.UserID };
                            await db.ListingLogAdd(log);

                            if (listing.Qty > 0)
                            {
                                listing.Qty = 0;
                                await db.ListingSaveAsync(settings, listing, false, "Qty");
                                response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                            }
                        }
                        else
                        {
                            if (wmItem.ArrivalDateFlag == 1 || wmItem.ArrivalDateFlag == 2)
                            {
                                ++parseArrivalDate;
                                parseArrivalDateList.Add(listing.ListingTitle);
                                parseArrivalDateList.Add(listing.SupplierItem.ItemURL);
                                parseArrivalDateList.Add(string.Format("Code: {0}", wmItem.ArrivalDateFlag));
                                parseArrivalDateList.Add(string.Empty);

                                int cnt = CountMsgID(listing.ID, 1000, daysBack);
                                int total = CountMsgID(listing.ID, 0, daysBack);
                                parseArrivalDateList.Add(string.Format("Parse arrival date: {0}/{1}", cnt, total));
                                parseArrivalDateList.Add(string.Empty);

                                var log = new ListingLog { ListingID = listing.ID, MsgID = 1000, UserID = settings.UserID };
                                await db.ListingLogAdd(log);
                            }
                            if (!wmItem.SoldAndShippedBySupplier ?? false)
                            {
                                ++notWalmart;
                                notWalmartList.Add(listing.ListingTitle);
                                notWalmartList.Add(listing.SupplierItem.ItemURL);
                                notWalmartList.Add(string.Empty);

                                int cnt = CountMsgID(listing.ID, 1100, daysBack);
                                int total = CountMsgID(listing.ID, 0, daysBack);
                                notWalmartList.Add(string.Format("Not Walmart: {0}/{1}", cnt, total));
                                notWalmartList.Add(string.Empty);

                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }

                                var log = new ListingLog { ListingID = listing.ID, MsgID = 1100, UserID = settings.UserID };
                                await db.ListingLogAdd(log);
                            }
                            if (wmItem.ShippingNotAvailable)
                            {
                                shipNotAvailList.Add(listing.ListingTitle);
                                shipNotAvailList.Add(listing.SupplierItem.ItemURL);
                                shipNotAvailList.Add(string.Empty);

                                int cnt = CountMsgID(listing.ID, 400, daysBack);
                                int total = CountMsgID(listing.ID, 0, daysBack);
                                shipNotAvailList.Add(string.Format("Delivery not available: {0}/{1}", cnt, total));
                                shipNotAvailList.Add(string.Empty);

                                ++shippingNotAvailable;
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 400, UserID = settings.UserID };
                                await db.ListingLogAdd(log);

                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }
                            }
                            if (!wmItem.ShippingNotAvailable && wmItem.OutOfStock)
                            {
                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }                              
                                outofStockList.Add(listing.ListingTitle);
                                outofStockList.Add(listing.SupplierItem.ItemURL);
                                outofStockList.Add(string.Empty);
                                ++outofstock;
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 300, UserID = settings.UserID };
                                await db.ListingLogAdd(log);

                            }
                            if (!wmItem.OutOfStock && !wmItem.ShippingNotAvailable && !wmItem.Arrives.HasValue)
                            {
                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await db.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }                             
                                outofStockBadArrivalList.Add(listing.ListingTitle);
                                outofStockBadArrivalList.Add(listing.SupplierItem.ItemURL);
                                outofStockBadArrivalList.Add(string.Empty);
                                ++outofstockBadArrivalDate;
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 200, UserID = settings.UserID };
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
                                    deliveryTooLongList.Add(string.Format("{0} days, over by {1} days(s)", days, days - allowedDeliveryDays));
                                    var note = string.Format("{0} days", days);
                                    deliveryTooLongList.Add(string.Format("Qty was {0}", listing.Qty));
                                    note += string.Format(" (Qty was {0})", listing.Qty);
                                    deliveryTooLongList.Add(string.Empty);

                                    int cnt = CountMsgID(listing.ID, 100, daysBack);
                                    int total = CountMsgID(listing.ID, 0, daysBack);
                                    deliveryTooLongList.Add(string.Format("Delivery too long: {0}/{1}", cnt, total));
                                    deliveryTooLongList.Add(string.Empty);

                                    note += string.Format(" (Qty was {0})", listing.Qty);
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 100, Note = note, UserID = settings.UserID };
                                    await db.ListingLogAdd(log);

                                    if (listing.Qty > 0)
                                    {
                                        listing.Qty = 0;
                                        await db.ListingSaveAsync(settings, listing, false, "Qty");
                                        response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                    }
                                }
                            }

                            // PUT BACK IN STOCK
                            if (listing.Qty == 0
                                && wmItem.Arrives.HasValue
                                && !wmItem.OutOfStock 
                                && !wmItem.ShippingNotAvailable 
                                && !lateDelivery 
                                && (wmItem.SoldAndShippedBySupplier ?? false))
                            {
                                var newListedQty = 1;
                                ++putBackInStock;
                                putBackInStockList.Add(listing.ListingTitle);
                                putBackInStockList.Add(listing.SupplierItem.ItemURL);
                                putBackInStockList.Add(string.Empty);
                              
                                var priceProfit = wallib.wmUtility.wmNewPrice(wmItem.SupplierPrice.Value, listing.PctProfit, wmShipping, wmFreeShippingMin, eBayPct);
                                decimal newPrice = priceProfit.ProposePrice;
                                listing.ListingPrice = newPrice;
                                response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, price: (double)newPrice, qty: newListedQty);

                                if (response.Count > 0)
                                {
                                    var output = dsutil.DSUtil.ListToDelimited(response, ';');
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 600, UserID = settings.UserID, Note = output };
                                    await db.ListingLogAdd(log);
                                }
                                else
                                {
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 600, UserID = settings.UserID };
                                    await db.ListingLogAdd(log);
                                }
                                listing.Qty = newListedQty;
                                await db.ListingSaveAsync(settings, listing, false, "Qty", "ListingPrice");
                            }

                            // SUPPLIER PRICE CHANGE?
                            if (listing.Qty > 0
                                      && !wmItem.OutOfStock
                                      && !wmItem.ShippingNotAvailable
                                      && !lateDelivery
                                      && (wmItem.SoldAndShippedBySupplier ?? false))
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
                                    dsutil.DSUtil.WriteFile(logfile, body, log_username);

                                    var priceProfit = wallib.wmUtility.wmNewPrice(wmItem.SupplierPrice.Value, listing.PctProfit, wmShipping, wmFreeShippingMin, eBayPct);
                                    decimal newPrice = priceProfit.ProposePrice;
                                    priceChangeList.Add(string.Format("New price: {0:c}", newPrice));
                                    note += string.Format(" New price: {0:c}", newPrice);
                                    priceChangeList.Add(string.Empty);
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, price: (double)newPrice);
                                    await db.UpdatePrice(listing, (decimal)newPrice, wmItem.SupplierPrice.Value);
                                    ++mispriceings;
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 700, Note = note, UserID = settings.UserID };
                                    await db.ListingLogAdd(log);
                                }
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        ++numErrors;
                        var log = new ListingLog { ListingID = listing.ID, MsgID = 10000, UserID = settings.UserID };
                        await db.ListingLogAdd(log);

                        string msg = "ERROR IN LOOP -> " + listing.ListingTitle + " -> " + exc.Message;
                        errors.Add(msg);
                        dsutil.DSUtil.WriteFile(logfile, msg, "");
                    }
                }

                endTime = DateTime.Now;
                double elapsedMinutes = ((TimeSpan)(endTime - startTime)).TotalMinutes;

                if (mispriceings > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " PRICE CHANGE", priceChangeList);
                }
                if (outofstock > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " OUT OF STOCK - LABEL", outofStockList);
                }
                if (outofstockBadArrivalDate > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " OUT OF STOCK - Bad Arrival Date", outofStockBadArrivalList);
                }
                if (invalidURL > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " INVALID URL ", invalidURLList);
                }
                if (shippingNotAvailable > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " DELIVERY NOT AVAILABLE ", shipNotAvailList);
                }
                if (putBackInStock > 0)
                {
                    putBackInStockList.Add(string.Format("elapsed time: {0} minutes", Math.Round(elapsedMinutes, 2)));
                    SendAlertEmail(_toEmail, settings.StoreName + " RE-STOCK ", putBackInStockList);
                }
                if (deliveryTooLong > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " DELIVERY TOO LONG ", deliveryTooLongList);
                }
                if (parseArrivalDate > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " PARSE ARRIVAL DATE - START SELENIUM ", parseArrivalDateList);
                }
                if (notWalmart > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " NOT SOLD & SHIPPED BY SUPPLIER ", notWalmartList);
                }
                if (numErrors > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " TRACKER ERRORS ", errors);
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
                dsutil.DSUtil.WriteFile(logfile, msg, "");
                throw;
            }
        }

        /// <summary>
        /// How many times has msgID appeard in last x times
        /// </summary>
        /// <param name="listingID"></param>
        /// <param name="msgID">Pass 0 for all</param>
        /// <returns></returns>
        static int CountMsgID(int listingID, int msgID, int daysBack)
        {
            DateTime twoWeeks = DateTime.Now.AddDays(-daysBack);
            int count = 0;
            if (msgID > 0)
            {
                count = db.ListingLogs.Where(x => x.ListingID == listingID && x.Created > twoWeeks && x.MsgID == msgID).Count();
            }
            else
            {
                count = db.ListingLogs.Where(x => x.ListingID == listingID && x.Created > twoWeeks).Count();
            }
            return count;
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
