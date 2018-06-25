using HtmlAgilityPack;
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
        static void Main(string[] args)
        {
            Console.WriteLine("begin processing...");
            Task.Run(async () =>
            {
                await GetSellerAsync();
            }).Wait();
            Console.WriteLine("processing complete");
            Console.ReadKey();
        }

        public static async Task GetSellerAsync()
        {

            // Need to handle if can't get to url

            string url = string.Format("https://www.walmart.com/ip/Apple-Watch-Strap-Sport-Leather-Watch-Band-Brown-Fits-42mm-Series-1-2-Apple-Watch-Silver-Adapters/196018083");

            var httpClient = new HttpClient();
            var html = await httpClient.GetStringAsync(url);

            var htmlDocument = new HtmlDocument();
            htmlDocument.LoadHtml(html);

            var ProductsHtml = htmlDocument.DocumentNode.Descendants("span")
                .Where(node => node.GetAttributeValue("class", "")
                .Equals("price-characteristic")).ToList();

            var s = ProductsHtml[0].Attributes["content"].Value;
                
            Console.WriteLine(s);
        }
    }
}
