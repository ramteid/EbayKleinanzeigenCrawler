using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Persistence;
using Moq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace KleinanzeigenCrawler.Tests.Persistence;

public class JsonStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kc-json-storage-{Guid.NewGuid():N}");
    private readonly JsonStorage _storage = new(Mock.Of<ILogger>());

    public JsonStorageTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Save_ShouldReplaceExistingDataWithoutLeavingTemporaryFiles()
    {
        // Arrange
        var fileName = Path.Combine(_directory, "data.json");
        _storage.Save(new List<string> { "old" }, fileName);

        // Act
        _storage.Save(new List<string> { "new" }, fileName);

        // Assert
        _storage.Load(fileName, out List<string> loaded);
        loaded.Should().Equal("new");
        Directory.GetFiles(_directory).Should().Equal(fileName);
    }

    [Fact]
    public void Save_ShouldKeepExistingDataWhenWritingFails()
    {
        // Arrange
        var fileName = Path.Combine(_directory, "data.json");
        _storage.Save(new List<string> { "old" }, fileName);
        // A directory in place of the temporary file makes writing the new data fail
        Directory.CreateDirectory(fileName + ".tmp");

        // Act
        var act = () => _storage.Save(new List<string> { "new" }, fileName);

        // Assert
        act.Should().Throw<Exception>();
        _storage.Load(fileName, out List<string> loaded);
        loaded.Should().Equal("old");
    }
}
