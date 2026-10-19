using Xunit;
using FluentAssertions;
using HtmlAgilityPack;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Manager;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Tests.TestDoubles;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.Manager;

public class TextConversationHandlerTests : IDisposable
{
    private const string User = "user-1";
    private const string SearchUrl = "https://www.kleinanzeigen.de/s-wohnung/berlin/c203l3331";

    private readonly InMemorySubscriptionPersistence _persistence = new();
    private readonly Mock<IParserProvider> _parserProvider = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly TextConversationHandler _handler;

    public TextConversationHandlerTests()
    {
        var queryExecutor = new Mock<IQueryExecutor>();
        queryExecutor.Setup(q => q.GetHtml(It.IsAny<Uri>())).ReturnsAsync((HtmlDocument)null);
        var parser = new Mock<IParser>();
        parser.SetupGet(p => p.QueryExecutor).Returns(queryExecutor.Object);
        _parserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns(parser.Object);

        _handler = CreateHandler(allowNewUsers: true);
    }

    public void Dispose()
    {
        _disposables.ForEach(d => d.Dispose());
    }

    private TextConversationHandler CreateHandler(bool allowNewUsers)
    {
        var subscriptions = new SubscriptionService(_persistence, _parserProvider.Object, _logger.Object, MessengerChannel.Signal, allowNewUsers);
        var sessions = new SessionManager(_logger.Object);
        _disposables.Add(sessions);
        return new TextConversationHandler(subscriptions, sessions);
    }

    private Task<string> Say(string text) => _handler.HandleMessageAsync(User, text);

    private void Seed(params string[] titles)
    {
        _persistence.AddSubscriber(new Subscriber
        {
            Id = User,
            Channel = MessengerChannel.Signal,
            Subscriptions = titles.Select(title => new Subscription
            {
                Title = title,
                QueryUrl = new Uri(SearchUrl),
                IncludeKeywords = new List<string> { "old-include" },
                ExcludeKeywords = new List<string> { "old-exclude" },
                Enabled = true
            }).ToList()
        });
    }

    private Subscriber Stored() => _persistence.GetSubscribers().Single(s => s.Id == User);

    [Theory]
    [InlineData("hello")]
    [InlineData("/start")]
    [InlineData("/unknown")]
    [InlineData("START")]
    public async Task IdleInput_ShouldReplyWithWelcome(string text)
    {
        // Act
        var reply = await Say(text);

        // Assert
        reply.Should().Contain("Welcome to the Kleinanzeigen Crawler").And.Contain("/add");
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("help")]
    [InlineData("/HELP")]
    public async Task Help_ShouldDescribeCommandsAndLimits(string text)
    {
        // Act
        var reply = await Say(text);

        // Assert
        reply.Should().Contain("/add").And.Contain("/delete").And.Contain("50 results");
    }

    [Fact]
    public async Task FirstMessage_ShouldRegisterTheSubscriberOnTheChannel()
    {
        // Act
        await Say("hello");

        // Assert
        var subscriber = _persistence.GetSubscribers().Should().ContainSingle().Subject;
        subscriber.Id.Should().Be(User);
        subscriber.Channel.Should().Be(MessengerChannel.Signal);
    }

    [Fact]
    public async Task FirstMessage_ShouldBeRejectedWhenRegistrationIsClosed()
    {
        // Arrange
        var closedHandler = CreateHandler(allowNewUsers: false);

        // Act
        var reply = await closedHandler.HandleMessageAsync(User, "/add");

        // Assert
        reply.Should().Contain("registrations are currently disabled");
        _persistence.GetSubscribers().Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ShouldGuideThroughAllStepsAndCreateTheSubscription()
    {
        // Act
        var urlPrompt = await Say("/add");
        var includePrompt = await Say(SearchUrl);
        var excludePrompt = await Say("red|blue, large");
        var initialPullPrompt = await Say("damaged, defect");
        var titlePrompt = await Say("yes");
        var confirmation = await Say("Berlin flats");
        var created = await Say("yes");

        // Assert
        urlPrompt.Should().Contain("Step 1 of 5");
        includePrompt.Should().Contain("Step 2 of 5");
        excludePrompt.Should().Contain("Step 3 of 5");
        initialPullPrompt.Should().Contain("Step 4 of 5");
        titlePrompt.Should().Contain("Step 5 of 5");
        confirmation.Should().Contain("Title: Berlin flats")
            .And.Contain($"URL: {SearchUrl}")
            .And.Contain("Include keywords: red|blue, large")
            .And.Contain("Exclude keywords: damaged, defect")
            .And.Contain("Notify about existing results: yes");
        created.Should().Contain("Subscription \"Berlin flats\" created");

        var subscription = Stored().Subscriptions.Should().ContainSingle().Subject;
        subscription.Title.Should().Be("Berlin flats");
        subscription.QueryUrl.Should().Be(new Uri(SearchUrl));
        subscription.IncludeKeywords.Should().Equal("red|blue", "large");
        subscription.ExcludeKeywords.Should().Equal("damaged", "defect");
        subscription.InitialPull.Should().BeTrue();
        subscription.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Add_ShouldSkipOptionalKeywordSteps()
    {
        // Act
        await Say("/add");
        await Say(SearchUrl);
        await Say("/skip");
        await Say("/SKIP");
        await Say("no");
        await Say("Plain search");
        await Say("y");

        // Assert
        var subscription = Stored().Subscriptions.Should().ContainSingle().Subject;
        subscription.IncludeKeywords.Should().BeEmpty();
        subscription.ExcludeKeywords.Should().BeEmpty();
        subscription.InitialPull.Should().BeFalse();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://www.kleinanzeigen.de/s-wohnung")]
    [InlineData("/skip")]
    public async Task Add_ShouldRejectInvalidUrlAndRepeatTheStep(string input)
    {
        // Arrange
        await Say("/add");

        // Act
        var rejected = await Say(input);
        var accepted = await Say(SearchUrl);

        // Assert
        rejected.Should().StartWith("Error:").And.Contain("HTTPS URL");
        accepted.Should().Contain("Step 2 of 5");
    }

    [Fact]
    public async Task Add_ShouldRepeatTheInitialPullStepOnAnUnclearAnswer()
    {
        // Arrange
        await Say("/add");
        await Say(SearchUrl);
        await Say("/skip");
        await Say("/skip");

        // Act
        var unclear = await Say("maybe");
        var accepted = await Say("no");

        // Assert
        unclear.Should().StartWith("Error:").And.Contain("yes or no");
        accepted.Should().Contain("Step 5 of 5");
    }

    [Fact]
    public async Task Add_ShouldAbortWhenTheConfirmationIsDeclined()
    {
        // Arrange
        await Say("/add");
        await Say(SearchUrl);
        await Say("/skip");
        await Say("/skip");
        await Say("no");
        await Say("Declined");

        // Act
        var reply = await Say("no");

        // Assert
        reply.Should().Be("Cancelled.");
        Stored().Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ShouldRepeatTheConfirmationOnAnUnclearAnswer()
    {
        // Arrange
        await Say("/add");
        await Say(SearchUrl);
        await Say("/skip");
        await Say("/skip");
        await Say("no");
        await Say("Unclear");

        // Act
        var unclear = await Say("hmm");
        var created = await Say("yes");

        // Assert
        unclear.Should().StartWith("Error:").And.Contain("/cancel");
        created.Should().Contain("created");
    }

    [Fact]
    public async Task Add_ShouldReportAFailedCreationAndReturnToIdle()
    {
        // Arrange
        _parserProvider.Setup(p => p.GetParser(It.IsAny<Subscription>())).Returns((IParser)null);
        await Say("/add");
        await Say("https://example.com/search");
        await Say("/skip");
        await Say("/skip");
        await Say("no");
        await Say("Unsupported");

        // Act
        var failure = await Say("yes");
        var afterwards = await Say("hello");

        // Assert
        failure.Should().StartWith("Error:").And.Contain("No parser found").And.Contain("/add to try again");
        afterwards.Should().Contain("Welcome");
        Stored().Subscriptions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Taken")]
    [InlineData("taken")]
    public async Task Add_ShouldRejectADuplicateTitleAtTheTitleStepWithoutLosingTheOtherAnswers(string title)
    {
        // Arrange
        Seed("Taken");
        await Say("/add");
        await Say(SearchUrl);
        await Say("alpha");
        await Say("beta");
        await Say("no");

        // Act
        var rejected = await Say(title);
        var confirmation = await Say("Free title");
        var created = await Say("yes");

        // Assert
        rejected.Should().StartWith("Error:").And.Contain("already exists");
        confirmation.Should().Contain("Title: Free title").And.Contain("Include keywords: alpha");
        created.Should().Contain("created");
        Stored().Subscriptions.Select(s => s.Title).Should().Equal("Taken", "Free title");
    }

    [Fact]
    public async Task Cancel_ShouldAbortTheConversationSoThatTheNextMessageIsNotAnAnswer()
    {
        // Arrange
        await Say("/add");

        // Act
        var cancelled = await Say("/cancel");
        var afterwards = await Say(SearchUrl);

        // Assert
        cancelled.Should().Be("Cancelled.");
        afterwards.Should().Contain("Welcome");
    }

    [Fact]
    public async Task Cancel_ShouldReportThatThereIsNothingToCancelWhenIdle()
    {
        // Act
        var reply = await Say("/cancel");

        // Assert
        reply.Should().Be("There is nothing to cancel.");
    }

    [Fact]
    public async Task Add_ShouldRestartWhenIssuedDuringAConversation()
    {
        // Arrange
        await Say("/add");
        await Say(SearchUrl);

        // Act
        var restarted = await Say("/add");
        var accepted = await Say(SearchUrl);

        // Assert
        restarted.Should().Contain("Step 1 of 5");
        accepted.Should().Contain("Step 2 of 5");
    }

    [Fact]
    public async Task BareWords_ShouldOnlyBeCommandsOutsideOfAConversation()
    {
        // Arrange
        await Say("/add");

        // Act
        var duringConversation = await Say("list");

        // Assert
        duringConversation.Should().StartWith("Error:").And.Contain("HTTPS URL");
    }

    [Fact]
    public async Task List_ShouldShowNumberedSubscriptionsWithTheirSettings()
    {
        // Arrange
        Seed("First", "Second");
        Stored().Subscriptions[1].Enabled = false;

        // Act
        var reply = await Say("/list");

        // Assert
        reply.Should().Contain("Your subscriptions (2):")
            .And.Contain("1. First (enabled)")
            .And.Contain("2. Second (disabled)")
            .And.Contain($"URL: {SearchUrl}")
            .And.Contain("Include: old-include")
            .And.Contain("Exclude: old-exclude");
    }

    [Fact]
    public async Task List_ShouldPointToAddWhenThereAreNoSubscriptions()
    {
        // Act
        var reply = await Say("/list");

        // Assert
        reply.Should().Contain("no subscriptions").And.Contain("/add");
    }

    [Fact]
    public async Task List_ShouldNotInterruptAConversation()
    {
        // Arrange
        await Say("/add");

        // Act
        await Say("/list");
        var reply = await Say(SearchUrl);

        // Assert
        reply.Should().Contain("Step 2 of 5");
    }

    [Fact]
    public async Task Edit_ShouldReplaceBothKeywordListsOfTheSubscriptionSelectedByNumber()
    {
        // Arrange
        Seed("First", "Second");

        // Act
        var selection = await Say("/edit");
        var includePrompt = await Say("2");
        var excludePrompt = await Say("alpha, beta");
        var updated = await Say("gamma");

        // Assert
        selection.Should().Contain("Which subscription do you want to edit?").And.Contain("1. First").And.Contain("2. Second");
        includePrompt.Should().Contain("Editing \"Second\"").And.Contain("Current include keywords: old-include");
        excludePrompt.Should().Contain("Current exclude keywords: old-exclude");
        updated.Should().Be("Subscription \"Second\" updated.");
        var subscriptions = Stored().Subscriptions;
        subscriptions[1].IncludeKeywords.Should().Equal("alpha", "beta");
        subscriptions[1].ExcludeKeywords.Should().Equal("gamma");
        subscriptions[0].IncludeKeywords.Should().Equal("old-include");
    }

    [Fact]
    public async Task Edit_ShouldKeepKeywordsOnSkipAndClearThemOnNone()
    {
        // Arrange
        Seed("First");

        // Act
        await Say("/edit");
        await Say("1");
        await Say("/skip");
        await Say("/none");

        // Assert
        var subscription = Stored().Subscriptions.Single();
        subscription.IncludeKeywords.Should().Equal("old-include");
        subscription.ExcludeKeywords.Should().BeEmpty();
    }

    [Fact]
    public async Task Selection_ShouldFindASubscriptionWhoseTitleIsANumberOutsideTheListRange()
    {
        // Arrange
        Seed("2024");
        await Say("/edit");

        // Act
        var reply = await Say("2024");

        // Assert
        reply.Should().Contain("Editing \"2024\"");
    }

    [Fact]
    public async Task Edit_ShouldFindTheSubscriptionByTitleIgnoringCase()
    {
        // Arrange
        Seed("Berlin Flats");
        await Say("/edit");

        // Act
        var reply = await Say("berlin flats");

        // Assert
        reply.Should().Contain("Editing \"Berlin Flats\"");
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("unknown title")]
    public async Task Edit_ShouldRepeatTheSelectionForAnUnknownSubscription(string input)
    {
        // Arrange
        Seed("First");
        await Say("/edit");

        // Act
        var rejected = await Say(input);
        var accepted = await Say("1");

        // Assert
        rejected.Should().StartWith("Error:").And.Contain("No subscription matches");
        accepted.Should().Contain("Editing \"First\"");
    }

    [Theory]
    [InlineData("/edit")]
    [InlineData("/enable")]
    [InlineData("/disable")]
    [InlineData("/delete")]
    public async Task SelectionCommands_ShouldPointToAddWhenThereAreNoSubscriptions(string command)
    {
        // Act
        var reply = await Say(command);

        // Assert
        reply.Should().Contain("no subscriptions").And.Contain("/add");
    }

    [Fact]
    public async Task DisableAndEnable_ShouldToggleTheSelectedSubscription()
    {
        // Arrange
        Seed("First");

        // Act
        await Say("/disable");
        var disabled = await Say("First");
        var enabledAfterDisable = Stored().Subscriptions.Single().Enabled;
        await Say("/enable");
        var enabled = await Say("1");

        // Assert
        disabled.Should().Be("Subscription \"First\" disabled.");
        enabledAfterDisable.Should().BeFalse();
        enabled.Should().Be("Subscription \"First\" enabled.");
        Stored().Subscriptions.Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheSubscriptionAfterConfirmation()
    {
        // Arrange
        Seed("First", "Second");

        // Act
        await Say("/delete");
        var confirmation = await Say("1");
        var deleted = await Say("yes");

        // Assert
        confirmation.Should().Contain("Delete the subscription \"First\"?");
        deleted.Should().Be("Subscription \"First\" deleted.");
        Stored().Subscriptions.Select(s => s.Title).Should().Equal("Second");
    }

    [Fact]
    public async Task Delete_ShouldKeepTheSubscriptionWhenDeclined()
    {
        // Arrange
        Seed("First");
        await Say("/delete");
        await Say("1");

        // Act
        var reply = await Say("no");

        // Assert
        reply.Should().Be("Cancelled.");
        Stored().Subscriptions.Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_ShouldRepeatTheConfirmationOnAnUnclearAnswer()
    {
        // Arrange
        Seed("First");
        await Say("/delete");
        await Say("1");

        // Act
        var unclear = await Say("maybe");
        var deleted = await Say("yes");

        // Assert
        unclear.Should().StartWith("Error:").And.Contain("/cancel");
        deleted.Should().Contain("deleted");
        Stored().Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Conversations_ShouldBeIndependentPerUser()
    {
        // Arrange
        await Say("/add");

        // Act
        var otherUser = await _handler.HandleMessageAsync("user-2", SearchUrl);

        // Assert
        otherUser.Should().Contain("Welcome");
    }
}
