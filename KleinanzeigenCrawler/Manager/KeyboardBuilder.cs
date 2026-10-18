using System;
using System.Collections.Generic;
using Telegram.Bot.Types.ReplyMarkups;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Builder class for creating Telegram keyboards.
/// The quick command keyboard (ReplyKeyboardMarkup) provides primary navigation.
/// Inline keyboards are used only for context-specific actions within a workflow.
/// </summary>
public class KeyboardBuilder
{
    /// <summary>
    /// Creates the persistent reply keyboard with quick actions.
    /// This is the primary navigation method - inline buttons should be used
    /// only for context-specific actions, not general navigation.
    /// </summary>
    public static ReplyKeyboardMarkup CreateQuickCommandKeyboard()
    {
        return new ReplyKeyboardMarkup(
        [
            new[]
            {
                new KeyboardButton("➕ Add Subscription"),
                new KeyboardButton("📋 My Subscriptions")
            },
            new[]
            {
                new KeyboardButton("📊 Report"),
                new KeyboardButton("❓ Help")
            },
            new[]
            {
                new KeyboardButton("❌ Cancel")
            }
        ])
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = false,
            InputFieldPlaceholder = "Choose an action or type a message..."
        };
    }

    /// <summary>
    /// Creates an empty inline keyboard for the main menu.
    /// Navigation is handled by the persistent quick command keyboard.
    /// </summary>
    public static InlineKeyboardMarkup CreateMainMenu()
    {
        // No inline buttons needed - quick menu provides navigation
        return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());
    }
    
    /// <summary>
    /// Creates a keyboard for a subscription item in the list
    /// </summary>
    public static InlineKeyboardMarkup CreateSubscriptionActions(string subscriptionId, bool isEnabled)
    {
        var toggleButton = isEnabled
            ? InlineKeyboardButton.WithCallbackData("🔕 Disable", $"sub:disable:{subscriptionId}")
            : InlineKeyboardButton.WithCallbackData("🔔 Enable", $"sub:enable:{subscriptionId}");

        InlineKeyboardButton[][] rows =
        [
            [
                InlineKeyboardButton.WithCallbackData("✏️ Edit", $"sub:edit:{subscriptionId}"),
                toggleButton
            ],
            [
                InlineKeyboardButton.WithCallbackData("🗑️ Delete", $"sub:delete:{subscriptionId}"),
                InlineKeyboardButton.WithCallbackData("« Back", "action:list")
            ]
        ];

        return new InlineKeyboardMarkup(rows);
    }
    
    /// <summary>
    /// Creates a keyboard for the subscription list
    /// </summary>
    public static InlineKeyboardMarkup CreateSubscriptionList(List<(string Id, string Title)> subscriptions)
    {
        var buttons = new List<InlineKeyboardButton[]>();
        
        foreach (var subscription in subscriptions)
        {
            buttons.Add(
            [
                InlineKeyboardButton.WithCallbackData(
                    $"📑 {subscription.Title}",
                    $"sub:view:{subscription.Id}")
            ]);
        }

        return new InlineKeyboardMarkup(buttons);
    }
    
    /// <summary>
    /// Creates a keyboard for confirming or canceling an action
    /// </summary>
    public static InlineKeyboardMarkup CreateConfirmationKeyboard(string action, string data = "")
    {
        var confirmCallback = string.IsNullOrEmpty(data) 
            ? $"confirm:{action}" 
            : $"confirm:{action}:{data}";

        InlineKeyboardButton[][] rows =
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Confirm", confirmCallback)
            ]
        ];

        return new InlineKeyboardMarkup(rows);
    }
    
    /// <summary>
    /// Creates a skip keyboard for optional steps (cancel via quick menu)
    /// </summary>
    public static InlineKeyboardMarkup CreateSkipCancelKeyboard(string skipCallback)
    {
        InlineKeyboardButton[][] rows =
        [
            [
                InlineKeyboardButton.WithCallbackData("⏭️ Skip", skipCallback)
            ]
        ];

        return new InlineKeyboardMarkup(rows);
    }
    
    /// <summary>
    /// Creates an empty keyboard - cancel is available via quick menu
    /// </summary>
    public static InlineKeyboardMarkup CreateCancelKeyboard()
    {
        // Cancel is available via quick menu, no inline button needed
        return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());
    }
    
    /// <summary>
    /// Creates a keyboard for delete confirmation
    /// </summary>
    public static InlineKeyboardMarkup CreateDeleteConfirmation(string subscriptionId)
    {
        InlineKeyboardButton[][] rows =
        [
            [
                InlineKeyboardButton.WithCallbackData("⚠️ Yes, Delete", $"confirm:delete:{subscriptionId}"),
            ],
            [
                InlineKeyboardButton.WithCallbackData("« No, Go Back", $"sub:view:{subscriptionId}")
            ]
        ];

        return new InlineKeyboardMarkup(rows);
    }
    
    /// <summary>
    /// Creates an initial pull selection keyboard
    /// </summary>
    public static InlineKeyboardMarkup CreateInitialPullKeyboard()
    {
        InlineKeyboardButton[][] rows =
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Yes, show all existing", "add:initialpull:yes"),
                InlineKeyboardButton.WithCallbackData("❌ No, only new ones", "add:initialpull:no")
            ],
            [
                InlineKeyboardButton.WithCallbackData("« Back", "add:back")
            ]
        ];

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Creates inline actions shown together with the report overview
    /// </summary>
    public static InlineKeyboardMarkup CreateSnapshotActions()
    {
        // Report actions are available via quick menu, minimal inline buttons
        return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());
    }
}
