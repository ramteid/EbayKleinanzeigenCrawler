using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KleinanzeigenCrawler.Configuration;
using KleinanzeigenCrawler.Interfaces;
using KleinanzeigenCrawler.Models;
using Serilog;

namespace KleinanzeigenCrawler.Persistence;

/// <summary>
/// Keeps the failed access password attempts in memory and in a JSON file, so that a block survives restarts.
/// To unblock a user, remove its entry from the file while the application is stopped.
/// </summary>
public class AccessAttemptPersistence : IAccessAttemptPersistence
{
    private readonly ILogger _logger;
    private readonly IDataStorage _dataStorage;
    private readonly string _filePath;
    private readonly Dictionary<(MessengerChannel Channel, string Id), int> _failedAttempts = new();
    private readonly object _lock = new();

    public AccessAttemptPersistence(ILogger logger, IDataStorage dataStorage, AppConfiguration configuration)
    {
        _logger = logger;
        _dataStorage = dataStorage;
        var dataDirectory = Path.GetFullPath(configuration.Crawler.DataPath);
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Join(dataDirectory, "AccessAttempts.json");
        RestoreData();
    }

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
            // The memory is updated first, so that an attempt counts even if the file cannot be written
            _failedAttempts[(channel, userId)] = failedAttempts;
            SaveData();
        }
    }

    public void Remove(MessengerChannel channel, string userId)
    {
        lock (_lock)
        {
            if (_failedAttempts.Remove((channel, userId)))
            {
                SaveData();
            }
        }
    }

    private void RestoreData()
    {
        try
        {
            _dataStorage.Load(_filePath, out List<AccessAttempt> records);
            foreach (var record in (records ?? new List<AccessAttempt>()).Where(record => !string.IsNullOrWhiteSpace(record?.Id)))
            {
                _failedAttempts[(record.Channel, record.Id)] = record.FailedAttempts;
            }

            _logger.Information("Restored the access attempts of {Count} users", _failedAttempts.Count);
        }
        catch (FileNotFoundException)
        {
            _logger.Information("No access attempts to restore");
        }
        catch (Exception e)
        {
            // Continuing with an empty list would unblock all blocked users and overwrite the unreadable file with the next attempt
            throw new InvalidOperationException(
                $"The access attempt file {_filePath} exists but cannot be loaded. Repair or remove it before starting the application.", e);
        }
    }

    private void SaveData()
    {
        var records = _failedAttempts
            .Select(entry => new AccessAttempt { Channel = entry.Key.Channel, Id = entry.Key.Id, FailedAttempts = entry.Value })
            .ToList();
        _dataStorage.Save(records, _filePath);
    }
}
