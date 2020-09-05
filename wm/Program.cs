/*
 * Scan walmart and update price and inventory as needed.
 * 
 */
using dsmodels;
using eBayUtility;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace wm
{
    class Program
    {
        static string _toEmail = "ventures2019@gmail.com";
        static int _sourceID = 1;

        static IRepository _repository = new Repository();
        const string log_username = "admin";

        static void Main(string[] args)
        {
            byte forceSendEmail = 0;
            int daysBack = 21;
            UserSettingsView settings = null;
            string logfile = null;
            try 
            { 
                int storeID;
                if (args.Length != 4)
                {
                    Console.WriteLine("Invalid arguments: wm [storeID] [logfile] [daysBack] [forceEmail(0,1)]");
                }
                else
                {
                    logfile = args[1];
                    storeID = Convert.ToInt32(args[0]);
                    daysBack = Convert.ToInt32(args[2]);
                    forceSendEmail = Convert.ToByte(args[3]);
                    string userID = UserID(storeID);
                    if (!string.IsNullOrEmpty(userID))
                    {
                        string connStr = ConfigurationManager.ConnectionStrings["OPWContext"].ConnectionString;

                        ebayAPIs.Init(_repository);
                        //eBayItem.Init(_repository);
                        //eBayItemVariation.Init(_repository);
                        FetchSeller.Init(_repository);
                        //StoreCheck.Init(_repository);
                        wallib.wmUtility.Init(_repository);

                        settings = _repository.GetUserSettingsView(connStr, userID, storeID);
                        var wmShipping = Convert.ToDecimal(_repository.GetAppSetting(settings, "Walmart shipping"));
                        var wmFreeShippingMin = Convert.ToDecimal(_repository.GetAppSetting(settings, "Walmart free shipping min"));
                        int imgLimit = Convert.ToInt32(_repository.GetAppSetting(settings, "Listing Image Limit"));

                        byte handlingTime = settings.HandlingTime;
                        byte maxShippingDays = settings.MaxShippingDays;
                        var allowedDeliveryDays = handlingTime + maxShippingDays;

                        int outofstock = 0;

                        Task.Run(async () =>
                        {
                            await GetOrders(settings, logfile, 0.0915);
                        }).Wait();

                        Task.Run(async () =>
                        {
                            outofstock = await ScanItems(settings, _sourceID, wmShipping, wmFreeShippingMin, settings.FinalValueFeePct, imgLimit, allowedDeliveryDays, logfile, daysBack, forceSendEmail);
                        }).Wait();
                    }
                    else
                    {
                        Console.WriteLine("Could not find a user with RepricerEmail flag set for this store.");
                    }
                }
            }
            catch (Exception exc)
            {
                string msg = dsutil.DSUtil.ErrMsg("Main", exc);
                dsutil.DSUtil.WriteFile(logfile, msg, settings.UserName);
            }
        }

        static async Task GetOrders(UserSettingsView settings, string logfile, double finalValueFeePct)
        {
            try
            {
                DateTime ed = DateTime.Now;
                DateTime sd = ed.AddHours(-3);
                var orders = ebayAPIs.GetOrdersByDate(settings, sd, ed, finalValueFeePct, "");
                if (orders.Count > 0)
                {
                    var msg = new List<string>();
                    foreach(var o in orders)
                    {
                        // sync db qty
                        var sellerListing = await ebayAPIs.GetSingleItem(settings, o.ListedItemID, false);
                        var listing = _repository.ListingGet(o.ListedItemID);

                        listing.Qty = sellerListing.Qty.Value;
                        await _repository.ListingSaveAsync(settings, listing, false, "Qty");

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
            var r = _repository.Context.UserSettingsView.Where(p => p.RepricerEmail && p.StoreID == storeID).FirstOrDefault();
            if (r != null)
            {
                return r.UserID;
            }
            return null;
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
            int daysBack,
            byte forceSendEmail)
        {
            int i = 0;
            int outofstock = 0;
            int outofstockBadArrivalDate = 0;
            int invalidURL = 0;
            int shippingNotAvailable = 0;
            int mispriceings = 0;
            int numErrors = 0;
            int putBackInStock = 0;
            int notInStockLongEnough = 0;
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
            var notInStockLongEnoughList = new List<string>();

            try
            {
                DateTime startTime, endTime;
                startTime = DateTime.Now;

                string token = _repository.GetToken(settings);
                var walListings = _repository.Context.Listings
                    .Include(d => d.SupplierItem)
                    .Where(x => x.SupplierItem.SourceID == sourceID && x.Listed != null && x.StoreID == settings.StoreID)
                    .ToList();

                foreach (Listing listing in walListings)
                {
                    try
                    {
                        Random random = new Random();
                        int sec = random.Next(2);
                        Thread.Sleep(sec * 1000);

                        listingID = listing.ID;

                        //if (listing.SupplierItem.ItemURL != "https://www.walmart.com/ip/Briggs-Stratton-8-Gallon-Hotdog-Oil-free-Air-Compressor/800727967")
                        //{
                        //    continue;
                        //}

                        var wmItem = await wallib.wmUtility.GetDetail(listing.SupplierItem.ItemURL, imgLimit, true);
                        Console.WriteLine((++i) + " " + listing.ListingTitle);
                        if (wmItem == null)  // could not fetch from walmart website
                        {
                            ++invalidURL;
                            invalidURLList.Add(listing.ListingTitle);
                            invalidURLList.Add(listing.SupplierItem.ItemURL);
                            invalidURLList.Add(string.Format("Qty was {0}", listing.Qty));
                            invalidURLList.Add(string.Empty);

                            int cnt = CountMsgID(listing.ID, 500, daysBack);
                            int total = CountMsgID(listing.ID, 0, daysBack);
                            invalidURLList.Add(string.Format("Invalid URL: {0}/{1}", cnt, total));
                            invalidURLList.Add(string.Empty);

                            var log = new ListingLog { ListingID = listing.ID, MsgID = 500, UserID = settings.UserID };
                            await _repository.ListingLogAdd(log);

                            if (listing.Qty > 0)
                            {
                                listing.Qty = 0;
                                await _repository.ListingSaveAsync(settings, listing, false, "Qty");
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
                                await _repository.ListingLogAdd(log);
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
                                    await _repository.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }

                                var log = new ListingLog { ListingID = listing.ID, MsgID = 1100, UserID = settings.UserID };
                                await _repository.ListingLogAdd(log);
                            }
                            if (wmItem.ShippingNotAvailable)
                            {
                                ++shippingNotAvailable;
                                shipNotAvailList.Add(listing.ListingTitle);
                                shipNotAvailList.Add(listing.SupplierItem.ItemURL);
                                shipNotAvailList.Add(string.Empty);

                                int cnt = CountMsgID(listing.ID, 400, daysBack);
                                int total = CountMsgID(listing.ID, 0, daysBack);
                                shipNotAvailList.Add(string.Format("Delivery not available: {0}/{1}", cnt, total));
                                shipNotAvailList.Add(string.Empty);

                                var log = new ListingLog { ListingID = listing.ID, MsgID = 400, UserID = settings.UserID };
                                await _repository.ListingLogAdd(log);

                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await _repository.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }
                            }
                            if (!wmItem.ShippingNotAvailable && wmItem.OutOfStock)
                            {
                                ++outofstock;
                                outofStockList.Add(listing.ListingTitle);
                                outofStockList.Add(listing.SupplierItem.ItemURL);
                                outofStockList.Add(string.Empty);

                                var log = new ListingLog { ListingID = listing.ID, MsgID = 300, UserID = settings.UserID };
                                await _repository.ListingLogAdd(log);

                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await _repository.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }
                            }
                            if (!wmItem.OutOfStock && !wmItem.ShippingNotAvailable && !wmItem.Arrives.HasValue)
                            {
                                ++outofstockBadArrivalDate;
                                outofStockBadArrivalList.Add(listing.ListingTitle);
                                outofStockBadArrivalList.Add(listing.SupplierItem.ItemURL);
                                outofStockBadArrivalList.Add(string.Empty);
                                
                                var log = new ListingLog { ListingID = listing.ID, MsgID = 200, UserID = settings.UserID };
                                await _repository.ListingLogAdd(log);

                                if (listing.Qty > 0)
                                {
                                    listing.Qty = 0;
                                    await _repository.ListingSaveAsync(settings, listing, false, "Qty");
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                }
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
                                    deliveryTooLongList.Add(string.Format("{0} business days, over by {1} day(s)", days, days - allowedDeliveryDays));
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
                                    await _repository.ListingLogAdd(log);

                                    if (listing.Qty > 0)
                                    {
                                        listing.Qty = 0;
                                        await _repository.ListingSaveAsync(settings, listing, false, "Qty");
                                        response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                                    }
                                }
                            }

                            // PUT BACK IN STOCK
                            if (listing.Qty == 0
                                && !listing.InActive
                                && wmItem.Arrives.HasValue
                                && !wmItem.OutOfStock 
                                && !wmItem.ShippingNotAvailable 
                                && !lateDelivery 
                                && (wmItem.SoldAndShippedBySupplier ?? false))
                            {
                                string msg = null;
                                bool inStockLongEnough = InStockLongEnough(listing.ID, 1, out msg);
                                string output = msg + " ";
                                if (inStockLongEnough)
                                {
                                    Console.WriteLine("Put back in stock.");
                                    var newListedQty = 1;
                                    ++putBackInStock;
                                    putBackInStockList.Add(listing.ListingTitle);
                                    putBackInStockList.Add(listing.SupplierItem.ItemURL);
                                    putBackInStockList.Add(string.Empty);

                                    var priceProfit = wallib.wmUtility.wmNewPrice(wmItem.SupplierPrice.Value, listing.PctProfit, wmShipping, wmFreeShippingMin, eBayPct);
                                    decimal newPrice = priceProfit.ProposePrice;
                                    output += string.Format(" Price: {0:0.00} ", newPrice); 
                                    listing.ListingPrice = newPrice;
                                    response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, price: (double)newPrice, qty: newListedQty);

                                    if (response.Count > 0)
                                    {
                                        output += dsutil.DSUtil.ListToDelimited(response, ';');
                                    }
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 600, UserID = settings.UserID, Note = output };
                                    await _repository.ListingLogAdd(log);

                                    listing.Qty = newListedQty;
                                    await _repository.ListingSaveAsync(settings, listing, false, "Qty", "ListingPrice");
                                }
                                else
                                {
                                    ++notInStockLongEnough;
                                    notInStockLongEnoughList.Add(listing.ListingTitle);
                                    notInStockLongEnoughList.Add(listing.SupplierItem.ItemURL);
                                    notInStockLongEnoughList.Add(string.Empty);

                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 1200, UserID = settings.UserID, Note = output };
                                    await _repository.ListingLogAdd(log);
                                }
                            }

                            // inactive and might qualify to put back in stock
                            if (listing.Qty == 0
                              && listing.InActive
                              && wmItem.Arrives.HasValue
                              && !wmItem.OutOfStock
                              && !wmItem.ShippingNotAvailable
                              && !lateDelivery
                              && (wmItem.SoldAndShippedBySupplier ?? false))
                            {
                                string msg = null;
                                if (InStockLongEnough(listing.ID, 1, out msg))
                                {
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 1300, UserID = settings.UserID };
                                    await _repository.ListingLogAdd(log);
                                }
                                else
                                {
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 1400, UserID = settings.UserID };
                                    await _repository.ListingLogAdd(log);
                                }
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

                                    Console.WriteLine("Price change.");
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
                                    await _repository.UpdatePrice(listing, (decimal)newPrice, wmItem.SupplierPrice.Value);
                                    ++mispriceings;
                                    var log = new ListingLog { ListingID = listing.ID, MsgID = 700, Note = note, UserID = settings.UserID };
                                    await _repository.ListingLogAdd(log);
                                }
                            }
                        }
                    }
                    catch (Exception exc)
                    {
                        string msg = "ERROR IN LOOP -> " + listing.ListingTitle + " -> " + exc.Message;
                        errors.Add(msg);
                        dsutil.DSUtil.WriteFile(logfile, msg, "");

                        ++numErrors;
                        var log = new ListingLog { ListingID = listing.ID, MsgID = 10000, UserID = settings.UserID, Note = exc.Message };
                        await _repository.ListingLogAdd(log);
                        
                        if (listing.Qty > 0)
                        {
                            listing.Qty = 0;
                            await _repository.ListingSaveAsync(settings, listing, false, "Qty");
                            response = Utility.eBayItem.ReviseItem(token, listing.ListedItemID, qty: 0);
                        }
                    }
                }   // end for loop

                endTime = DateTime.Now;
                double elapsedMinutes = ((TimeSpan)(endTime - startTime)).TotalMinutes;
                var storeProfile = new StoreProfile { ID = settings.StoreID, RepricerLastRan = DateTime.Now, ElapsedTime = elapsedMinutes };
                await _repository.StoreProfileUpdate(storeProfile, "RepricerLastRan", "ElapsedTime");

                var elapsedMinutesList = new List<string>();
                var elapsedTimeMsg = string.Format("Elapsed time: {0} minutes; Total scanned {1}", Math.Round(elapsedMinutes, 2), i);
                elapsedMinutesList.Add(elapsedTimeMsg);
                //SendAlertEmail(_toEmail, settings.StoreName + " ELAPSED TIME ", elapsedMinutesList);
               
                if (numErrors > 0)
                {
                    SendAlertEmail(_toEmail, settings.StoreName + " TRACKER ERRORS ", errors);
                }

                var sendEmail = false;
                var rightNow = DateTime.Now;
                if ((rightNow.Hour >= 8 && rightNow.Hour < 9) || forceSendEmail == 1)
                {
                    sendEmail = true;
                }
                if (sendEmail)
                {
                    if (putBackInStock > 0)
                    {
                        SendAlertEmail(_toEmail, settings.StoreName + " RE-STOCK ", putBackInStockList);
                    }
                    if (notInStockLongEnough > 0)
                    {
                        SendAlertEmail(_toEmail, settings.StoreName + " NOT IN-STOCK LONG ENOUGH ", notInStockLongEnoughList);
                    }
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
                   
                    if (mispriceings + outofstock + invalidURL + shippingNotAvailable + numErrors + putBackInStock == 0)
                    {
                        string ret = dsutil.DSUtil.SendMailDev(_toEmail, string.Format(settings.StoreName + "REPRICER - scanned {0} items", walListings.Count), "No issues found.");
                    }
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
        /// Called if trying to put an item back in stock.
        /// </summary>
        /// <param name="listingID"></param>
        /// <param name="daysLookBack"></param>
        /// <returns>null if cannot put back in stock</returns>
        protected static bool InStockLongEnough(int listingID, int daysLookBack, out string msg)
        {
            string ret = null;
            var back = DateTime.Now.AddDays(-daysLookBack);
            var items = _repository.Context.ListingLogs.Where(p => p.Created > back && p.ListingID == listingID).ToList();

            byte defect = 0;
            bool putBackInStock = false;

            foreach (var i in items)
            {
                if (i.MsgID != 600 && i.MsgID != 700 && i.MsgID != 1200)
                {
                    ++defect;
                }
            }
            double defectRate = ((double)defect / (double)items.Count) * 100.0;
            //defectRate = 19;
            if (defectRate < 20)
            {
                putBackInStock = true;
            }
            msg = items.Count + " data points;";
            msg = string.Format("Data points: {0}; Defect rate: {1}%", items.Count, Math.Round(defectRate, 2));
            return putBackInStock;
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
                count = _repository.Context.ListingLogs.Where(x => x.ListingID == listingID && x.Created > twoWeeks && x.MsgID == msgID).Count();
            }
            else
            {
                count = _repository.Context.ListingLogs.Where(x => x.ListingID == listingID && x.Created > twoWeeks).Count();
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
