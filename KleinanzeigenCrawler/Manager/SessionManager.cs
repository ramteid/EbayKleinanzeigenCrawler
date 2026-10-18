using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using KleinanzeigenCrawler.Models;
using Serilog;

namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Manages Telegram conversation sessions with automatic cleanup of expired sessions
/// </summary>
public class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, TelegramSession> _sessions = new();
    private readonly ILogger _logger;
    private readonly TimeSpan _sessionTimeout;
    private readonly Timer _cleanupTimer;
    
    public SessionManager(ILogger logger, TimeSpan? sessionTimeout = null)
    {
        _logger = logger;
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(15);
        
        // Run cleanup every 5 minutes
        _cleanupTimer = new Timer(
            callback: _ => CleanupExpiredSessions(),
            state: null,
            dueTime: TimeSpan.FromMinutes(5),
            period: TimeSpan.FromMinutes(5)
        );
    }
    
    /// <summary>
    /// Gets an existing session or creates a new one for the user
    /// </summary>
    public TelegramSession GetOrCreate(string userId)
    {
        var session = _sessions.GetOrAdd(userId, id => new TelegramSession(id));
        session.UpdateActivity();
        return session;
    }
    
    /// <summary>
    /// Updates an existing session
    /// </summary>
    public void Update(TelegramSession session)
    {
        if (session == null) return;
        
        session.UpdateActivity();
        _sessions.AddOrUpdate(session.UserId, session, (_, _) => session);
    }
    
    /// <summary>
    /// Ends a session and clears all data
    /// </summary>
    public void EndSession(string userId)
    {
        if (_sessions.TryRemove(userId, out var session))
        {
            _logger.Information($"Session ended for user {userId}");
        }
    }
    
    /// <summary>
    /// Removes sessions that have been inactive for too long
    /// </summary>
    public void CleanupExpiredSessions()
    {
        var cutoffTime = DateTime.Now - _sessionTimeout;
        var expiredSessions = _sessions
            .Where(kvp => kvp.Value.LastActivity < cutoffTime)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var userId in expiredSessions)
        {
            if (_sessions.TryRemove(userId, out _))
            {
                _logger.Information($"Cleaned up expired session for user {userId}");
            }
        }
        
        if (expiredSessions.Count > 0)
        {
            _logger.Information($"Cleaned up {expiredSessions.Count} expired session(s)");
        }
    }
    
    /// <summary>
    /// Gets the total number of active sessions
    /// </summary>
    public int GetActiveSessionCount() => _sessions.Count;
    
    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}
