using System;
using System.Collections.Generic;

namespace KleinanzeigenCrawler.Models;

/// <summary>
/// Represents a user's conversation session with one of the messenger bots.
/// Sessions automatically expire after a period of inactivity.
/// </summary>
public class ConversationSession
{
    public string UserId { get; set; }
    public DateTime LastActivity { get; set; }
    public ConversationState State { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    
    public ConversationSession(string userId)
    {
        UserId = userId;
        LastActivity = DateTime.UtcNow;
        State = ConversationState.Idle;
    }
    
    public void UpdateActivity()
    {
        LastActivity = DateTime.UtcNow;
    }
    
    public T GetData<T>(string key) where T : class
    {
        if (!Data.TryGetValue(key, out var value))
        {
            return null;
        }
        
        if (value is T typedValue)
        {
            return typedValue;
        }
        
        // Log type mismatch - this indicates a programming error
        throw new InvalidCastException($"Session data for key '{key}' is of type {value?.GetType().Name ?? "null"}, but {typeof(T).Name} was requested");
    }
    
    public void SetData(string key, object value)
    {
        Data[key] = value;
    }
    
    public void ClearData()
    {
        Data.Clear();
    }
}

/// <summary>
/// Conversation states shared by the button-driven and the text-driven messengers
/// </summary>
public enum ConversationState
{
    /// <summary>
    /// User is at the main menu, no active conversation
    /// </summary>
    Idle,
    
    /// <summary>
    /// User is in the process of adding a new subscription
    /// </summary>
    AddingSubscription,
    
    /// <summary>
    /// User is editing an existing subscription
    /// </summary>
    EditingSubscription,
    
    /// <summary>
    /// User is asked to pick one of their subscriptions for a pending action (text-driven messengers)
    /// </summary>
    SelectingSubscription,
    
    /// <summary>
    /// User is asked to confirm the deletion of a subscription (text-driven messengers)
    /// </summary>
    ConfirmingDelete
}
