using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Manager
{
    /// <summary>
    /// This class allows using the program in a console.
    /// Change binding in <see cref="AutofacConfig"/> from TelegramManager to ConsoleManager.
    /// </summary>
    public class ConsoleManager : StatefulManagerBase
    {
        /// <summary>
        /// Define an arbitrary Id
        /// </summary>
        private readonly string ConsoleSubscriberId = Guid.NewGuid().ToString();

        public ConsoleManager(ILogger logger, ISubscriptionPersistence subscriptionManager) : base(logger, subscriptionManager)
        {
            InputLoop();
        }

        /// <summary>
        /// Reads inputs from the command line asynchronously 
        /// </summary>
        private void InputLoop()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    string input = Console.ReadLine();
                    ProcessCommand(ConsoleSubscriberId, input);
                    Thread.Sleep(TimeSpan.FromMilliseconds(500));
                }
            });
        }

        protected override void SendMessage(Subscriber subscriber, string message, bool enablePreview = false)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Out.WriteLine($"Message for subscriber {subscriber.Id}:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Out.WriteLine(message);
            Console.ResetColor();
        }

        protected override void DisplaySubscriptionList(Subscriber subscriber)
        {
            var message = "Your subscriptions:";
            foreach (Subscription subscription in subscriber.Subscriptions)
            {
                message += "\n\n" +
                           $"Title: {subscription.Title} \n" +
                           $"URL: {subscription.QueryUrl} \n" +
                           $"Included keywords: {string.Join(", ", subscription.IncludeKeywords)} \n" +
                           $"Excluded keywords: {string.Join(", ", subscription.ExcludeKeywords)} \n" +
                           $"Enabled: {subscription.Enabled}";
            }

            SendMessage(subscriber, message);
        }
    }
}
