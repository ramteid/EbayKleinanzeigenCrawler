using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using KleinanzeigenCrawler.Monitoring;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Modernized Telegram manager with button-based UI and simplified state management
/// </summary>
public class TelegramManager : IMessenger
{
    private const int MaxSendAttempts = 3;
    private const int MaxRetryAfterSeconds = 60;
    private static readonly TimeSpan PollingErrorDelay = TimeSpan.FromSeconds(5);
    
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger _logger;
    private readonly MetricsService _metricsService;
    private readonly SessionManager _sessionManager;
    private readonly SubscriptionService _subscriptionService;
    private readonly AccessGate _accessGate;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Dictionary<string, Func<UserContext, Task>> _commandHandlers;
    private readonly Dictionary<string, Func<CallbackContext, Task>> _callbackHandlers;
    private readonly Dictionary<string, Func<UserContext, Task>> _menuButtonHandlers;
    
    public MessengerChannel Channel => MessengerChannel.Telegram;
    
    public TelegramManager(
        ITelegramBotClient botClient,
        ILogger logger,
        ISubscriptionPersistence subscriptionPersistence,
        IAccessAttemptPersistence accessAttemptPersistence,
        IParserProvider parserProvider,
        MessagingConfiguration messaging,
        MetricsService metricsService = null)
    {
        _botClient = botClient;
        _logger = logger;
        _metricsService = metricsService;
        _sessionManager = new SessionManager(logger, TimeSpan.FromMinutes(messaging.SessionTimeoutMinutes));
        _subscriptionService = new SubscriptionService(subscriptionPersistence, parserProvider, logger, Channel);
        _accessGate = new AccessGate(_subscriptionService, accessAttemptPersistence, messaging, logger);
        _commandHandlers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["/start"] = context => ShowMainMenuAsync(context.UserId, context.Session, includeWelcome: true),
            ["/help"] = context => SendHelpCardAsync(context.UserId),
            ["/menu"] = context => ShowMainMenuAsync(context.UserId, context.Session),
            ["/cancel"] = context => CancelConversationAsync(context.UserId, context.Session)
        };
        _callbackHandlers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = HandleActionCallback,
            ["add"] = HandleAddCallback,
            ["sub"] = HandleSubscriptionCallback,
            ["confirm"] = HandleConfirmCallback,
            ["edit"] = HandleEditCallback
        };

        _menuButtonHandlers = new(StringComparer.OrdinalIgnoreCase);
        RegisterQuickAction(context => StartAddSubscriptionFlowAsync(context.UserId, context.Session), QuickButtons.AddSubscription);
        RegisterQuickAction(context => ShowSubscriptionList(context.UserId, null), QuickButtons.MySubscriptions);
        RegisterQuickAction(SendSnapshotAsync, QuickButtons.Report);
        RegisterQuickAction(context => SendHelpCardAsync(context.UserId), QuickButtons.Help);
        RegisterQuickAction(context => CancelConversationAsync(context.UserId, context.Session), QuickButtons.Cancel);

        void RegisterQuickAction(Func<UserContext, Task> handler, params string[] labels)
        {
            foreach (var label in labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                {
                    _menuButtonHandlers[label] = handler;
                }
            }
        }
    }
    
    public void Start()
    {
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
            },
            cancellationToken: _cancellationTokenSource.Token
        );
        
        _logger.Information("Telegram bot started with button-based UI");
    }
    
    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Message is { } message)
            {
                await HandleMessageAsync(message);
            }
            else if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackQueryAsync(callbackQuery);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error handling update");
        }
    }
    
    private async Task HandleMessageAsync(Message message)
    {
        if (message.Text is not { } messageText)
        {
            return;
        }
        
        var userId = message.Chat.Id.ToString();
        var context = await GetUserContextAsync(userId, messageText);
        if (context is null)
        {
            return;
        }
        
        // Logged only after the admission, because the text of a user who is not a subscriber yet may be the access password
        _logger.Information($"Message from {userId}: {messageText}");
        if (messageText.StartsWith("/"))
        {
            await HandleCommandAsync(context, messageText);
            return;
        }

        if (await TryHandleQuickActionAsync(context, messageText))
        {
            return;
        }
        
        await HandleTextInputAsync(context, messageText);
    }
    
    private async Task HandleCommandAsync(UserContext context, string command)
    {
        if (_commandHandlers.TryGetValue(command, out var handler))
        {
            await handler(context);
            return;
        }
        
        await SendMessage(context.UserId, "Unknown command. Use /help for assistance.", KeyboardBuilder.CreateMainMenu());
    }

    private async Task<bool> TryHandleQuickActionAsync(UserContext context, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        
        var normalized = text.Trim();
        if (_menuButtonHandlers.TryGetValue(normalized, out var handler))
        {
            // Reset the session state before handling quick actions to avoid
            // carrying over state from a previous workflow (e.g., user was adding
            // a subscription but tapped Report - we don't want the next text
            // input to be treated as a subscription URL)
            ResetSession(context.Session);
            
            await handler(context);
            return true;
        }
        
        return false;
    }
    
    private async Task HandleTextInputAsync(UserContext context, string text)
    {
        switch (context.Session.State)
        {
            case ConversationState.AddingSubscription:
                await HandleAddSubscriptionInput(context, text);
                break;
                
            case ConversationState.EditingSubscription:
                await HandleEditSubscriptionInput(context, text);
                break;
                
            default:
                await ShowMainMenuAsync(context.UserId, context.Session);
                break;
        }
    }
    
    private async Task HandleAddSubscriptionInput(UserContext context, string text)
    {
        var session = context.Session;
        var userId = context.UserId;
        var data = session.GetData<AddSubscriptionData>("add_data") ?? new AddSubscriptionData();
        
        switch (data.Step)
        {
            case AddSubscriptionData.STEP_URL:
                // Validate URL
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    await SendMessage(userId, MessageFormatter.FormatError("Invalid URL. Please provide a valid HTTPS URL."), KeyboardBuilder.CreateCancelKeyboard());
                    return;
                }
                
                data.Url = text;
                data.Step = AddSubscriptionData.STEP_INCLUDE_KEYWORDS;
                session.SetData("add_data", data);
                _sessionManager.Update(session);
                
                await SendMessage(userId, MessageFormatter.FormatAddIncludeKeywords(), KeyboardBuilder.CreateSkipCancelKeyboard("add:skip:include"));
                break;
                
            case AddSubscriptionData.STEP_INCLUDE_KEYWORDS:
                data.IncludeKeywords = KeywordParser.Parse(text);
                data.Step = AddSubscriptionData.STEP_EXCLUDE_KEYWORDS;
                session.SetData("add_data", data);
                _sessionManager.Update(session);
                
                await SendMessage(userId, MessageFormatter.FormatAddExcludeKeywords(), KeyboardBuilder.CreateSkipCancelKeyboard("add:skip:exclude"));
                break;
                
            case AddSubscriptionData.STEP_EXCLUDE_KEYWORDS:
                data.ExcludeKeywords = KeywordParser.Parse(text);
                data.Step = AddSubscriptionData.STEP_INITIAL_PULL;
                session.SetData("add_data", data);
                _sessionManager.Update(session);
                
                await SendMessage(userId, MessageFormatter.FormatAddInitialPull(), KeyboardBuilder.CreateInitialPullKeyboard());
                break;
                
            case AddSubscriptionData.STEP_TITLE:
                data.Title = text.Trim();
                data.Step = AddSubscriptionData.STEP_CONFIRM;
                session.SetData("add_data", data);
                _sessionManager.Update(session);
                
                await SendMessage(userId, MessageFormatter.FormatAddConfirmation(data), KeyboardBuilder.CreateConfirmationKeyboard("add"));
                break;
        }
    }
    
    private async Task HandleEditSubscriptionInput(UserContext context, string text)
    {
        var session = context.Session;
        var userId = context.UserId;
        var subscriptionId = session.GetData<string>("edit_subscription_id");
        var editStep = session.GetData<string>("edit_step");
        
        if (string.IsNullOrEmpty(subscriptionId))
        {
            await SendMessage(userId, MessageFormatter.FormatError("Session expired. Please try again."), KeyboardBuilder.CreateMainMenu());
            ResetSession(session);
            return;
        }
        
        var subscription = _subscriptionService.GetSubscription(userId, Guid.Parse(subscriptionId));
        if (subscription == null)
        {
            await SendMessage(userId, MessageFormatter.FormatError("Subscription not found."), KeyboardBuilder.CreateMainMenu());
            ResetSession(session);
            return;
        }
        
        if (editStep == "include")
        {
            var keywords = KeywordParser.Parse(text);
                
            session.SetData("include_keywords", keywords);
            session.SetData("edit_step", "exclude");
            _sessionManager.Update(session);
            
            await SendMessage(userId, MessageFormatter.FormatAddExcludeKeywords(subscription.ExcludeKeywords), KeyboardBuilder.CreateSkipCancelKeyboard("edit:skip:exclude"));
        }
        else if (editStep == "exclude")
        {
            var keywords = KeywordParser.Parse(text);
                
            var includeKeywords = session.GetData<List<string>>("include_keywords");
            
            var result = _subscriptionService.UpdateSubscription(userId, Guid.Parse(subscriptionId), includeKeywords, keywords);
            
            if (result.IsSuccess)
            {
                await SendMessage(userId, MessageFormatter.FormatSuccess("Subscription updated successfully!"), KeyboardBuilder.CreateMainMenu());
            }
            else
            {
                await SendMessage(userId, MessageFormatter.FormatError(result.ErrorMessage), KeyboardBuilder.CreateMainMenu());
            }
            
            ResetSession(session);
        }
    }
    
    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery)
    {
        if (callbackQuery.Data is not { } data || callbackQuery.Message is null)
        {
            return;
        }
        
        var userId = callbackQuery.From.Id.ToString();
        _logger.Information($"Callback from {userId}: {data}");
        
        await _botClient.AnswerCallbackQuery(callbackQuery.Id);
        
        var context = await CreateCallbackContextAsync(userId, callbackQuery.Message.MessageId, data);
        if (context == null)
        {
            return;
        }
        
        var action = context.Parts[0];
        if (_callbackHandlers.TryGetValue(action, out var handler))
        {
            await handler(context);
        }
    }
    
    private async Task HandleActionCallback(CallbackContext context)
    {
        if (context.Parts.Length < 2)
        {
            return;
        }
        
        var session = context.Session;
        
        switch (context.Parts[1])
        {
            case "menu":
                await ShowMainMenuAsync(context.UserId, session, context.MessageId);
                break;
                
            case "add":
                await StartAddSubscriptionFlowAsync(context.UserId, session, context.MessageId);
                break;
                
            case "list":
                await ShowSubscriptionList(context.UserId, context.MessageId);
                break;
                
            case "help":
                await SendHelpCardAsync(context.UserId, context.MessageId);
                break;
                
            case "cancel":
                await CancelConversationAsync(context.UserId, session, context.MessageId);
                break;
        }
    }

    private async Task ShowMainMenuAsync(string userId, ConversationSession session, int? messageId = null, bool includeWelcome = false)
    {
        if (session != null)
        {
            ResetSession(session);
        }
        
        var text = includeWelcome
            ? MessageFormatter.FormatWelcome()
            : "🏠 <b>Main Menu</b>\n\nWhat would you like to do?";
        await SendOrEditInlineAsync(userId, messageId, text, KeyboardBuilder.CreateMainMenu());
        
        // Always send the quick command keyboard to ensure it's visible
        // This handles cases where the bot restarted or the user's keyboard was cleared
        await SendQuickCommandPromptAsync(userId);
    }
    
    private Task StartAddSubscriptionFlowAsync(string userId, ConversationSession session, int? messageId = null)
    {
        if (session != null)
        {
            session.ClearData();
            session.State = ConversationState.AddingSubscription;
            session.SetData("add_data", new AddSubscriptionData());
            _sessionManager.Update(session);
        }
        
        return SendOrEditInlineAsync(userId, messageId, MessageFormatter.FormatAddSubscriptionStart(), KeyboardBuilder.CreateCancelKeyboard());
    }
    
    private Task SendHelpCardAsync(string userId, int? messageId = null)
    {
        return SendOrEditInlineAsync(userId, messageId, MessageFormatter.FormatHelp(), KeyboardBuilder.CreateMainMenu());
    }
    
    private async Task CancelConversationAsync(string userId, ConversationSession session, int? messageId = null)
    {
        if (session != null)
        {
            ResetSession(session);
        }
        
        await SendOrEditInlineAsync(userId, messageId, MessageFormatter.FormatCancelled(), KeyboardBuilder.CreateMainMenu());
    }
    
    private async Task SendSnapshotAsync(UserContext context)
    {
        var subscriptions = _subscriptionService.GetSubscriptions(context.UserId);
        var report = MessageFormatter.FormatUserSnapshot(subscriptions);
        await SendMessage(context.UserId, report, KeyboardBuilder.CreateSnapshotActions());
    }
    
    private async Task ShowSubscriptionList(string userId, int? messageId = null)
    {
        var subscriptions = _subscriptionService.GetSubscriptions(userId);
        var message = MessageFormatter.FormatSubscriptionList(subscriptions);
        
        if (subscriptions.Count == 0)
        {
            await SendOrEditInlineAsync(userId, messageId, message, KeyboardBuilder.CreateMainMenu());
            return;
        }
        
        var subscriptionList = subscriptions.Select(s => (s.Id.ToString(), s.Title)).ToList();
        await SendOrEditInlineAsync(userId, messageId, message, KeyboardBuilder.CreateSubscriptionList(subscriptionList));
    }
    
    private Task SendQuickCommandPromptAsync(string userId)
    {
        return SendMessage(userId, "⌨️ Quick commands are ready below. Tap whenever you need me!", KeyboardBuilder.CreateQuickCommandKeyboard());
    }
    
    private Task SendOrEditInlineAsync(string userId, int? messageId, string message, InlineKeyboardMarkup keyboard)
    {
        return messageId.HasValue
            ? EditMessage(userId, messageId.Value, message, keyboard)
            : SendMessage(userId, message, keyboard);
    }
    
    private async Task HandleAddCallback(CallbackContext context)
    {
        if (context.Parts.Length < 2)
        {
            return;
        }
        
        var session = context.Session;
        var data = session.GetData<AddSubscriptionData>("add_data");
        
        if (data == null)
        {
            await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError("Session expired. Please start again."), KeyboardBuilder.CreateMainMenu());
            return;
        }
        
        switch (context.Parts[1])
        {
            case "skip":
                if (context.Parts.Length > 2 && context.Parts[2] == "include")
                {
                    data.Step = AddSubscriptionData.STEP_EXCLUDE_KEYWORDS;
                    session.SetData("add_data", data);
                    _sessionManager.Update(session);
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatAddExcludeKeywords(), KeyboardBuilder.CreateSkipCancelKeyboard("add:skip:exclude"));
                }
                else if (context.Parts.Length > 2 && context.Parts[2] == "exclude")
                {
                    data.Step = AddSubscriptionData.STEP_INITIAL_PULL;
                    session.SetData("add_data", data);
                    _sessionManager.Update(session);
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatAddInitialPull(), KeyboardBuilder.CreateInitialPullKeyboard());
                }
                break;
                
            case "initialpull":
                if (context.Parts.Length > 2)
                {
                    data.InitialPull = context.Parts[2] == "yes";
                    data.Step = AddSubscriptionData.STEP_TITLE;
                    session.SetData("add_data", data);
                    _sessionManager.Update(session);
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatAddTitle(), KeyboardBuilder.CreateCancelKeyboard());
                }
                break;
                
            case "back":
                if (data.Step > 0)
                {
                    data.Step--;
                    session.SetData("add_data", data);
                    _sessionManager.Update(session);
                    await ShowAddSubscriptionStep(context.UserId, context.MessageId, data);
                }
                break;
        }
    }
    
    private async Task ShowAddSubscriptionStep(string userId, int messageId, AddSubscriptionData data)
    {
        switch (data.Step)
        {
            case AddSubscriptionData.STEP_URL:
                await EditMessage(userId, messageId, MessageFormatter.FormatAddSubscriptionStart(), KeyboardBuilder.CreateCancelKeyboard());
                break;
            case AddSubscriptionData.STEP_INCLUDE_KEYWORDS:
                await EditMessage(userId, messageId, MessageFormatter.FormatAddIncludeKeywords(data.IncludeKeywords), KeyboardBuilder.CreateSkipCancelKeyboard("add:skip:include"));
                break;
            case AddSubscriptionData.STEP_EXCLUDE_KEYWORDS:
                await EditMessage(userId, messageId, MessageFormatter.FormatAddExcludeKeywords(data.ExcludeKeywords), KeyboardBuilder.CreateSkipCancelKeyboard("add:skip:exclude"));
                break;
            case AddSubscriptionData.STEP_INITIAL_PULL:
                await EditMessage(userId, messageId, MessageFormatter.FormatAddInitialPull(), KeyboardBuilder.CreateInitialPullKeyboard());
                break;
        }
    }
    
    private async Task HandleSubscriptionCallback(CallbackContext context)
    {
        if (context.Parts.Length < 3)
        {
            return;
        }
        
        var subAction = context.Parts[1];
        var subscriptionId = context.Parts[2];
        
        if (!Guid.TryParse(subscriptionId, out var guid))
        {
            await SendMessage(context.UserId, MessageFormatter.FormatError("Invalid subscription ID."), KeyboardBuilder.CreateMainMenu());
            return;
        }
        
        var subscription = _subscriptionService.GetSubscription(context.UserId, guid);
        if (subscription == null)
        {
            await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError("Subscription not found."), KeyboardBuilder.CreateMainMenu());
            return;
        }
        
        switch (subAction)
        {
            case "view":
                await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatSubscriptionDetail(subscription), KeyboardBuilder.CreateSubscriptionActions(subscriptionId, subscription.Enabled));
                break;
                
            case "edit":
                var session = context.Session;
                session.State = ConversationState.EditingSubscription;
                session.SetData("edit_subscription_id", subscriptionId);
                session.SetData("edit_step", "include");
                _sessionManager.Update(session);
                
                await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatAddIncludeKeywords(subscription.IncludeKeywords), KeyboardBuilder.CreateSkipCancelKeyboard("edit:skip:include"));
                break;
                
            case "enable":
                var enableResult = _subscriptionService.SetSubscriptionEnabled(context.UserId, guid, true);
                if (enableResult.IsSuccess)
                {
                    subscription = _subscriptionService.GetSubscription(context.UserId, guid);
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatSubscriptionDetail(subscription), KeyboardBuilder.CreateSubscriptionActions(subscriptionId, true));
                }
                else
                {
                    await SendMessage(context.UserId, MessageFormatter.FormatError(enableResult.ErrorMessage), KeyboardBuilder.CreateMainMenu());
                }
                break;
                
            case "disable":
                var disableResult = _subscriptionService.SetSubscriptionEnabled(context.UserId, guid, false);
                if (disableResult.IsSuccess)
                {
                    subscription = _subscriptionService.GetSubscription(context.UserId, guid);
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatSubscriptionDetail(subscription), KeyboardBuilder.CreateSubscriptionActions(subscriptionId, false));
                }
                else
                {
                    await SendMessage(context.UserId, MessageFormatter.FormatError(disableResult.ErrorMessage), KeyboardBuilder.CreateMainMenu());
                }
                break;
                
            case "delete":
                await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatDeleteConfirmation(subscription), KeyboardBuilder.CreateDeleteConfirmation(subscriptionId));
                break;
        }
    }

    private async Task HandleConfirmCallback(CallbackContext context)
    {
        if (context.Parts.Length < 2)
        {
            return;
        }
        
        var confirmAction = context.Parts[1];
        var session = context.Session;
        
        switch (confirmAction)
        {
            case "add":
                var data = session.GetData<AddSubscriptionData>("add_data");
                if (data == null)
                {
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError("Session expired. Please try again."), KeyboardBuilder.CreateMainMenu());
                    return;
                }
                
                var result = await _subscriptionService.CreateSubscriptionAsync(
                    context.UserId,
                    data.Url,
                    data.IncludeKeywords,
                    data.ExcludeKeywords,
                    data.InitialPull,
                    data.Title);
                
                if (result.IsSuccess)
                {
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatSuccess($"Subscription '{data.Title}' created successfully!\n\nYou'll receive notifications for new matches."), KeyboardBuilder.CreateMainMenu());
                }
                else
                {
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError(result.ErrorMessage), KeyboardBuilder.CreateMainMenu());
                }
                
                ResetSession(session);
                break;
                
            case "delete":
                if (context.Parts.Length < 3)
                {
                    return;
                }
                
                var subscriptionId = context.Parts[2];
                if (!Guid.TryParse(subscriptionId, out var guid))
                {
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError("Invalid subscription ID."), KeyboardBuilder.CreateMainMenu());
                    return;
                }
                
                var deleteResult = _subscriptionService.DeleteSubscription(context.UserId, guid);
                if (deleteResult.IsSuccess)
                {
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatSuccess("Subscription deleted successfully!"), KeyboardBuilder.CreateMainMenu());
                }
                else
                {
                    await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError(deleteResult.ErrorMessage), KeyboardBuilder.CreateMainMenu());
                }
                break;
        }
    }
    
    private async Task HandleEditCallback(CallbackContext context)
    {
        if (context.Parts.Length < 3 || context.Parts[1] != "skip")
        {
            return;
        }
        
        var session = context.Session;
        var subscriptionId = session.GetData<string>("edit_subscription_id");
        var editStep = session.GetData<string>("edit_step");
        
        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(editStep))
        {
            ResetSession(session);
            await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError("Session expired. Please try again."), KeyboardBuilder.CreateMainMenu());
            return;
        }
        
        if (editStep == "include")
        {
            var subscription = _subscriptionService.GetSubscription(context.UserId, Guid.Parse(subscriptionId));
            session.SetData("include_keywords", subscription.IncludeKeywords);
            session.SetData("edit_step", "exclude");
            _sessionManager.Update(session);
            
            await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatAddExcludeKeywords(subscription.ExcludeKeywords), KeyboardBuilder.CreateSkipCancelKeyboard("edit:skip:exclude"));
        }
        else if (editStep == "exclude")
        {
            var subscription = _subscriptionService.GetSubscription(context.UserId, Guid.Parse(subscriptionId));
            var includeKeywords = session.GetData<List<string>>("include_keywords");
            
            var result = _subscriptionService.UpdateSubscription(context.UserId, Guid.Parse(subscriptionId), includeKeywords, subscription.ExcludeKeywords);
            
            if (result.IsSuccess)
            {
                await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatSuccess("Subscription updated successfully!"), KeyboardBuilder.CreateMainMenu());
            }
            else
            {
                await EditMessage(context.UserId, context.MessageId, MessageFormatter.FormatError(result.ErrorMessage), KeyboardBuilder.CreateMainMenu());
            }
            
            ResetSession(session);
        }
    }
    
    private async Task<CallbackContext> CreateCallbackContextAsync(string userId, int messageId, string data)
    {
        var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }
        
        var userContext = await GetUserContextAsync(userId, text: null);
        if (userContext is null)
        {
            return null;
        }
        
        return new CallbackContext(userContext.UserId, messageId, parts, userContext.Session);
    }
    
    /// <param name="text">The text of the message, or null if the update is not a text message</param>
    /// <returns>The context of the user, or null if the update must not be handled any further</returns>
    private async Task<UserContext> GetUserContextAsync(string userId, string text)
    {
        var admission = _accessGate.Admit(userId, text);
        switch (admission.Status)
        {
            case AdmissionStatus.Ignored:
                return null;
            case AdmissionStatus.Refused:
                await SendMessage(userId, MessageFormatter.FormatNotice(admission.Reply));
                return null;
            case AdmissionStatus.Granted:
                await SendMessage(userId, MessageFormatter.FormatNotice(admission.Reply));
                await ShowMainMenuAsync(userId, _sessionManager.GetOrCreate(userId), includeWelcome: true);
                return null;
            case AdmissionStatus.Registered:
                // Send the quick command keyboard for new users so they have the button menu immediately
                await SendQuickCommandPromptAsync(userId);
                break;
        }
        
        return new UserContext(userId, _sessionManager.GetOrCreate(userId));
    }
    
    private void ResetSession(ConversationSession session)
    {
        session.State = ConversationState.Idle;
        session.ClearData();
        _sessionManager.Update(session);
    }
    
    private async Task SendMessage(string userId, string message, IReplyMarkup keyboard = null, bool enablePreview = false)
    {
        await TrySendAsync(userId, message, keyboard, enablePreview);
    }
    
    /// <returns>The reason why the message could not be delivered, or null if it was delivered</returns>
    private async Task<Exception> TrySendAsync(string userId, string message, IReplyMarkup keyboard = null, bool enablePreview = false)
    {
        try
        {
            await DeliveryRetry.ExecuteAsync(
                () => _botClient.SendMessage(
                    chatId: userId,
                    text: message,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = !enablePreview }
                ),
                SelectRetryDelay,
                MaxSendAttempts,
                (ex, delay, attempt) => _logger.Warning(
                    "Sending a message to {UserId} failed: {Reason} (retrying in {Delay}, attempt {Attempt} of {MaxAttempts})",
                    userId, ex.Message, delay, attempt + 1, MaxSendAttempts));
            return null;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            // The user blocked the bot or deleted the chat. Crawling for a subscriber that cannot be notified is pointless.
            _logger.Warning("Telegram user {UserId} cannot be reached: {Reason}", userId, ex.Message);
            _subscriptionService.DisableAllSubscriptions(userId);
            return ex;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error sending message to {userId}");
            return ex;
        }
    }
    
    // Rate limiting, server errors and network problems may go away. A message that Telegram rejected will be rejected again.
    // ApiRequestException derives from RequestException, so it has to be matched first.
    private static bool IsTransient(Exception exception) => exception switch
    {
        ApiRequestException { ErrorCode: 429 or >= 500 } => true,
        ApiRequestException => false,
        RequestException => true,
        _ => false
    };
    
    private static TimeSpan? SelectRetryDelay(Exception exception, int attempt) => exception switch
    {
        ApiRequestException { ErrorCode: 429, Parameters.RetryAfter: { } seconds } =>
            seconds <= MaxRetryAfterSeconds ? TimeSpan.FromSeconds(seconds) : null,
        _ when IsTransient(exception) => TimeSpan.FromSeconds(attempt),
        _ => null
    };
    
    private async Task EditMessage(string userId, int messageId, string message, Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup keyboard = null)
    {
        try
        {
            await _botClient.EditMessageText(
                chatId: userId,
                messageId: messageId,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }
            );
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error editing message {messageId} for {userId}");
            await SendMessage(userId, message, keyboard);
        }
    }
    
    private async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
            _ => exception.ToString()
        };
        
        _logger.Error(errorMessage);
        
        // Polling starts again right after the error handler returns, which floods the log while the token is invalid or the network is down
        await Task.Delay(PollingErrorDelay, cancellationToken);
    }
    
    private static class QuickButtons
    {
        public const string AddSubscription = "➕ Add Subscription";
        public const string MySubscriptions = "📋 My Subscriptions";
        public const string Report = "📊 Report";
        public const string Help = "❓ Help";
        public const string Cancel = "❌ Cancel";
    }
    
    private sealed record UserContext(string UserId, ConversationSession Session);
    
    private sealed record CallbackContext(string UserId, int MessageId, string[] Parts, ConversationSession Session);
    
    public async Task<DeliveryReport> NotifyAsync(Subscription subscription, Result newResult)
    {
        var recipients = _subscriptionService.GetRecipients(subscription);
        if (recipients.Count == 0)
        {
            _logger.Debug("No {Channel} recipients found for subscription {SubscriptionId}", Channel, subscription.Id);
            return DeliveryReport.NoRecipients;
        }
        
        var message = MessageFormatter.FormatNewResult(subscription, newResult);
        var delivered = 0;
        var transientFailures = new List<Exception>();
        foreach (var subscriber in recipients)
        {
            var failure = await TrySendAsync(subscriber.Id, message, enablePreview: true);
            _metricsService?.RecordNotification(Channel, failure is null);
            if (failure is null)
            {
                delivered++;
            }
            else if (IsTransient(failure))
            {
                transientFailures.Add(failure);
            }
        }
        
        return new DeliveryReport(delivered, transientFailures);
    }
    
    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _sessionManager?.Dispose();
    }
}
