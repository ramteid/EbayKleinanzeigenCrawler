using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KleinanzeigenCrawler.Tests.Manager;

public class PlainTextFormatterTests
{
    private static readonly Subscription Subscription = new()
    {
        Title = "Berlin flats",
        QueryUrl = new Uri("https://www.kleinanzeigen.de/s-wohnung/berlin/c203l3331"),
        IncludeKeywords = new List<string> { "balcony|terrace", "parking" },
        Enabled = true
    };

    [Theory]
    [InlineData("Today", "900 EUR", "Today | 900 EUR")]
    [InlineData("Today", null, "Today")]
    [InlineData(null, "900 EUR", "900 EUR")]
    [InlineData("", "", null)]
    public void NewResult_ShouldShowTheLinkAfterTheAvailableDetails(string date, string price, string expectedDetails)
    {
        // Arrange
        var result = new Result { Link = new Uri("https://www.kleinanzeigen.de/s-anzeige/flat/1"), CreationDate = date, Price = price };

        // Act
        var message = PlainTextFormatter.NewResult(Subscription, result);

        // Assert
        var expectedLines = new[] { "New result in \"Berlin flats\":", expectedDetails, "https://www.kleinanzeigen.de/s-anzeige/flat/1" }
            .Where(line => line != null);
        message.Split('\n').Should().Equal(expectedLines);
    }

    [Fact]
    public void SubscriptionList_ShouldNumberTheSubscriptionsAndShowTheirKeywords()
    {
        // Act
        var message = PlainTextFormatter.SubscriptionList(new[] { Subscription });

        // Assert
        message.Should().Be(
            "Your subscriptions (1):\n" +
            "\n" +
            "1. Berlin flats (enabled)\n" +
            "   URL: https://www.kleinanzeigen.de/s-wohnung/berlin/c203l3331\n" +
            "   Include: balcony|terrace, parking\n" +
            "   Exclude: none");
    }

    [Fact]
    public void AddConfirmation_ShouldSummarizeTheCollectedData()
    {
        // Arrange
        var data = new AddSubscriptionData
        {
            Url = "https://www.kleinanzeigen.de/s-wohnung/berlin/c203l3331",
            Title = "Berlin flats",
            ExcludeKeywords = new List<string> { "damaged" },
            InitialPull = false
        };

        // Act
        var message = PlainTextFormatter.AddConfirmation(data);

        // Assert
        message.Should().Contain("Title: Berlin flats")
            .And.Contain("Include keywords: none")
            .And.Contain("Exclude keywords: damaged")
            .And.Contain("Notify about existing results: no");
    }

    [Fact]
    public void Messages_ShouldOnlyUsePlainAsciiCharacters()
    {
        // Arrange
        var data = new AddSubscriptionData { Url = "https://example.com", Title = "Title" };
        var messages = new[]
        {
            PlainTextFormatter.Welcome(),
            PlainTextFormatter.Help(),
            PlainTextFormatter.AddUrlPrompt(),
            PlainTextFormatter.AddIncludeKeywordsPrompt(),
            PlainTextFormatter.AddExcludeKeywordsPrompt(),
            PlainTextFormatter.AddInitialPullPrompt(),
            PlainTextFormatter.AddTitlePrompt(),
            PlainTextFormatter.AddConfirmation(data),
            PlainTextFormatter.EditIncludeKeywordsPrompt(Subscription),
            PlainTextFormatter.EditExcludeKeywordsPrompt(Subscription),
            PlainTextFormatter.SelectSubscriptionPrompt("edit", new[] { Subscription }),
            PlainTextFormatter.DeleteConfirmation(Subscription),
            PlainTextFormatter.NoSubscriptions(),
            PlainTextFormatter.Cancelled(),
            PlainTextFormatter.NothingToCancel()
        };

        // Assert
        messages.Should().OnlyContain(message => message.All(character => character < 128));
    }
}
