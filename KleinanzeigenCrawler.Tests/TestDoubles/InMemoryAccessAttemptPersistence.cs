using System.Collections.Generic;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Tests.TestDoubles;

/// <summary>
/// Keeps the failed access attempts in memory so that tests can observe them
/// </summary>
public class InMemoryAccessAttemptPersistence : IAccessAttemptPersistence
{
    private readonly Dictionary<(MessengerChannel, string), int> _failedAttempts = new();
    private readonly object _lock = new();

    public int? GetFailedAttempts(MessengerChannel channel, string userId)
    {
        lock (_lock)
        {
            return _failedAttempts.TryGetValue((channel, userId), out var failedAttempts) ? failedAttempts : null;
        }
    }

    public void SetFailedAttempts(MessengerChannel channel, string userId, int failedAttempts)
    {
        lock (_lock)
        {
            _failedAttempts[(channel, userId)] = failedAttempts;
        }
    }

    public void Remove(MessengerChannel channel, string userId)
    {
        lock (_lock)
        {
            _failedAttempts.Remove((channel, userId));
        }
    }
}
