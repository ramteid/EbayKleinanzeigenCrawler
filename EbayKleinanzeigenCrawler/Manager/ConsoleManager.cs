using System;
using System.Threading;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;

namespace EbayKleinanzeigenCrawler.Manager;

/// <summary>
/// This class allows using the program in a console.
/// </summary>
public class ConsoleManager : StatefulManagerBase
{
    /// <summary>
    /// Define an arbitrary Id
    /// </summary>
    private readonly string _consoleSubscriberId = Guid.NewGuid().ToString();

    public ConsoleManager(ILogger logger, ISubscriptionPersistence subscriptionManager) : base(logger, subscriptionManager)
    {
        InputLoop();
    }

    /// <summary>
    /// Reads inputs from the command line asynchronously 
    /// </summary>
    private void InputLoop()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                var input = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(input))
                {
                    await ProcessCommand(_consoleSubscriberId, input);
                }
                Thread.Sleep(TimeSpan.FromMilliseconds(500));
            }
        });
    }

    protected override Task SendMessage(Subscriber subscriber, string message, bool enablePreview = true)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Out.WriteLine($"Message for subscriber {subscriber.Id}:");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Out.WriteLine(message);
        Console.ResetColor();
        return Task.CompletedTask;
    }

    protected override async Task DisplaySubscriptionList(Subscriber subscriber)
    {
        var message = "Your subscriptions:";
        foreach (var subscription in subscriber.Subscriptions)
        {
            message += "\n\n" +
                       $"Title: {subscription.Title} \n" +
                       $"URL: {subscription.QueryUrl} \n" +
                       $"Included keywords: {string.Join(", ", subscription.IncludeKeywords)} \n" +
                       $"Excluded keywords: {string.Join(", ", subscription.ExcludeKeywords)} \n" +
                       $"Enabled: {subscription.Enabled}";
        }

        await SendMessage(subscriber, message);
    }
}