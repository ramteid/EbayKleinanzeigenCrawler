using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Drives the conversation with a subscriber through plain text messages and step-by-step prompts.
/// The messages of one user must be handled sequentially.
/// </summary>
public class TextConversationHandler
{
    private const string AddDataKey = "add_data";
    private const string EditDataKey = "edit_data";
    private const string SelectionKey = "selection";
    private const string PendingDeleteKey = "pending_delete";
    private const string SkipCommand = "/skip";
    private const string NoneCommand = "/none";

    private readonly SubscriptionService _subscriptions;
    private readonly SessionManager _sessions;
    private readonly Dictionary<string, Func<ConversationSession, string>> _commands;

    public TextConversationHandler(SubscriptionService subscriptions, SessionManager sessions)
    {
        _subscriptions = subscriptions;
        _sessions = sessions;
        _commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = ShowWelcome,
            ["help"] = _ => PlainTextFormatter.Help(),
            ["list"] = session => PlainTextFormatter.SubscriptionList(_subscriptions.GetSubscriptions(session.UserId)),
            ["add"] = StartAdding,
            ["edit"] = session => StartSelecting(session, SelectionAction.Edit),
            ["enable"] = session => StartSelecting(session, SelectionAction.Enable),
            ["disable"] = session => StartSelecting(session, SelectionAction.Disable),
            ["delete"] = session => StartSelecting(session, SelectionAction.Delete),
            ["cancel"] = Cancel
        };
    }

    /// <summary>
    /// Handles a message of a user and returns the reply to send back
    /// </summary>
    public async Task<string> HandleMessageAsync(string userId, string text)
    {
        var registration = _subscriptions.GetOrRegisterSubscriber(userId);
        if (!registration.IsSuccess)
        {
            return PlainTextFormatter.Error(registration.ErrorMessage);
        }

        var session = _sessions.GetOrCreate(userId);
        var input = text.Trim();
        if (TryGetCommand(session, input, out var command))
        {
            return command(session);
        }

        return session.State switch
        {
            ConversationState.AddingSubscription => await HandleAddStepAsync(session, input),
            ConversationState.EditingSubscription => HandleEditStep(session, input),
            ConversationState.SelectingSubscription => HandleSelection(session, input),
            ConversationState.ConfirmingDelete => HandleDeleteConfirmation(session, input),
            _ => PlainTextFormatter.Welcome()
        };
    }

    private bool TryGetCommand(ConversationSession session, string input, out Func<ConversationSession, string> command)
    {
        // Without a leading slash a word is only a command outside of a conversation, where it cannot be an answer
        var name = input.StartsWith('/') ? input[1..] : session.State == ConversationState.Idle ? input : null;
        if (name is null)
        {
            command = null;
            return false;
        }

        return _commands.TryGetValue(name, out command);
    }

    private string ShowWelcome(ConversationSession session)
    {
        Reset(session);
        return PlainTextFormatter.Welcome();
    }

    private static string Cancel(ConversationSession session)
    {
        var wasIdle = session.State == ConversationState.Idle;
        Reset(session);
        return wasIdle ? PlainTextFormatter.NothingToCancel() : PlainTextFormatter.Cancelled();
    }

    private static string StartAdding(ConversationSession session)
    {
        Begin(session, ConversationState.AddingSubscription, AddDataKey, new AddSubscriptionData());
        return PlainTextFormatter.AddUrlPrompt();
    }

    private async Task<string> HandleAddStepAsync(ConversationSession session, string input)
    {
        var data = session.GetData<AddSubscriptionData>(AddDataKey);
        switch (data.Step)
        {
            case AddSubscriptionData.STEP_URL:
                if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    return PlainTextFormatter.Error("Invalid URL. Please provide a valid HTTPS URL.");
                }

                data.Url = input;
                data.Step = AddSubscriptionData.STEP_INCLUDE_KEYWORDS;
                return PlainTextFormatter.AddIncludeKeywordsPrompt();

            case AddSubscriptionData.STEP_INCLUDE_KEYWORDS:
                data.IncludeKeywords = IsCommand(input, SkipCommand) ? new List<string>() : KeywordParser.Parse(input);
                data.Step = AddSubscriptionData.STEP_EXCLUDE_KEYWORDS;
                return PlainTextFormatter.AddExcludeKeywordsPrompt();

            case AddSubscriptionData.STEP_EXCLUDE_KEYWORDS:
                data.ExcludeKeywords = IsCommand(input, SkipCommand) ? new List<string>() : KeywordParser.Parse(input);
                data.Step = AddSubscriptionData.STEP_INITIAL_PULL;
                return PlainTextFormatter.AddInitialPullPrompt();

            case AddSubscriptionData.STEP_INITIAL_PULL:
                if (!TryParseYesNo(input, out var initialPull))
                {
                    return PlainTextFormatter.Error("Please reply yes or no.");
                }

                data.InitialPull = initialPull;
                data.Step = AddSubscriptionData.STEP_TITLE;
                return PlainTextFormatter.AddTitlePrompt();

            case AddSubscriptionData.STEP_TITLE:
                var title = _subscriptions.ValidateTitle(session.UserId, input);
                if (!title.IsSuccess)
                {
                    return PlainTextFormatter.Error(title.ErrorMessage);
                }

                data.Title = title.Data;
                data.Step = AddSubscriptionData.STEP_CONFIRM;
                return PlainTextFormatter.AddConfirmation(data);

            default:
                return await ConfirmAddAsync(session, data, input);
        }
    }

    private async Task<string> ConfirmAddAsync(ConversationSession session, AddSubscriptionData data, string input)
    {
        if (!TryParseYesNo(input, out var confirmed))
        {
            return PlainTextFormatter.Error("Please reply yes to create the subscription or send /cancel to abort.");
        }

        if (!confirmed)
        {
            return Cancel(session);
        }

        var result = await _subscriptions.CreateSubscriptionAsync(
            session.UserId,
            data.Url,
            data.IncludeKeywords,
            data.ExcludeKeywords,
            data.InitialPull,
            data.Title);
        Reset(session);

        return result.IsSuccess
            ? $"Subscription \"{data.Title}\" created. You will be notified about new matches."
            : $"{PlainTextFormatter.Error(result.ErrorMessage)}\n\nSend /add to try again.";
    }

    private string StartSelecting(ConversationSession session, SelectionAction action)
    {
        Reset(session);
        var subscriptions = _subscriptions.GetSubscriptions(session.UserId);
        if (subscriptions.Count == 0)
        {
            return PlainTextFormatter.NoSubscriptions();
        }

        Begin(session, ConversationState.SelectingSubscription, SelectionKey, new Selection(action));
        return PlainTextFormatter.SelectSubscriptionPrompt(action.ToString().ToLowerInvariant(), subscriptions);
    }

    private string HandleSelection(ConversationSession session, string input)
    {
        var subscription = FindSubscription(_subscriptions.GetSubscriptions(session.UserId), input);
        if (subscription is null)
        {
            return PlainTextFormatter.Error($"No subscription matches \"{input}\". Send a number or title from /list, or send /cancel to abort.");
        }

        var selection = session.GetData<Selection>(SelectionKey);
        switch (selection.Action)
        {
            case SelectionAction.Edit:
                Begin(session, ConversationState.EditingSubscription, EditDataKey, new EditData { SubscriptionId = subscription.Id });
                return PlainTextFormatter.EditIncludeKeywordsPrompt(subscription);

            case SelectionAction.Delete:
                Begin(session, ConversationState.ConfirmingDelete, PendingDeleteKey, new PendingDelete(subscription.Id, subscription.Title));
                return PlainTextFormatter.DeleteConfirmation(subscription);

            default:
                var enabled = selection.Action == SelectionAction.Enable;
                var result = _subscriptions.SetSubscriptionEnabled(session.UserId, subscription.Id, enabled);
                Reset(session);
                return result.IsSuccess
                    ? $"Subscription \"{subscription.Title}\" {(enabled ? "enabled" : "disabled")}."
                    : PlainTextFormatter.Error(result.ErrorMessage);
        }
    }

    private string HandleEditStep(ConversationSession session, string input)
    {
        var edit = session.GetData<EditData>(EditDataKey);
        var subscription = _subscriptions.GetSubscription(session.UserId, edit.SubscriptionId);
        if (subscription is null)
        {
            Reset(session);
            return PlainTextFormatter.Error("Subscription not found.");
        }

        if (!edit.AwaitingExcludeKeywords)
        {
            edit.IncludeKeywords = ResolveKeywords(input, subscription.IncludeKeywords);
            edit.AwaitingExcludeKeywords = true;
            return PlainTextFormatter.EditExcludeKeywordsPrompt(subscription);
        }

        var excludeKeywords = ResolveKeywords(input, subscription.ExcludeKeywords);
        var result = _subscriptions.UpdateSubscription(session.UserId, subscription.Id, edit.IncludeKeywords, excludeKeywords);
        Reset(session);

        return result.IsSuccess
            ? $"Subscription \"{subscription.Title}\" updated."
            : PlainTextFormatter.Error(result.ErrorMessage);
    }

    private string HandleDeleteConfirmation(ConversationSession session, string input)
    {
        if (!TryParseYesNo(input, out var confirmed))
        {
            return PlainTextFormatter.Error("Please reply yes to delete the subscription or send /cancel to abort.");
        }

        if (!confirmed)
        {
            return Cancel(session);
        }

        var pending = session.GetData<PendingDelete>(PendingDeleteKey);
        var result = _subscriptions.DeleteSubscription(session.UserId, pending.SubscriptionId);
        Reset(session);

        return result.IsSuccess
            ? $"Subscription \"{pending.Title}\" deleted."
            : PlainTextFormatter.Error(result.ErrorMessage);
    }

    private static List<string> ResolveKeywords(string input, List<string> current)
    {
        if (IsCommand(input, SkipCommand))
        {
            return current is null ? new List<string>() : new List<string>(current);
        }

        return IsCommand(input, NoneCommand) ? new List<string>() : KeywordParser.Parse(input);
    }

    private static Subscription FindSubscription(IReadOnlyList<Subscription> subscriptions, string input)
    {
        if (int.TryParse(input, out var number) && number >= 1 && number <= subscriptions.Count)
        {
            return subscriptions[number - 1];
        }

        return subscriptions.FirstOrDefault(s => s.Title == input)
            ?? subscriptions.FirstOrDefault(s => string.Equals(s.Title, input, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCommand(string input, string command) => string.Equals(input, command, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseYesNo(string input, out bool value)
    {
        switch (input.ToLowerInvariant())
        {
            case "yes" or "y":
                value = true;
                return true;
            case "no" or "n":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static void Begin(ConversationSession session, ConversationState state, string dataKey, object data)
    {
        session.ClearData();
        session.State = state;
        session.SetData(dataKey, data);
    }

    private static void Reset(ConversationSession session)
    {
        session.State = ConversationState.Idle;
        session.ClearData();
    }

    private enum SelectionAction
    {
        Edit,
        Enable,
        Disable,
        Delete
    }

    private sealed record Selection(SelectionAction Action);

    private sealed record PendingDelete(Guid SubscriptionId, string Title);

    private sealed class EditData
    {
        public Guid SubscriptionId { get; init; }
        public bool AwaitingExcludeKeywords { get; set; }
        public List<string> IncludeKeywords { get; set; }
    }
}
