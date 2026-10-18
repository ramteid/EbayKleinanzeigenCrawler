using System.IO;
using System.Text;
using KleinanzeigenCrawler.Interfaces;
using Newtonsoft.Json;
using Serilog;

namespace KleinanzeigenCrawler.Persistence;

public class JsonStorage : IDataStorage
{
    private static readonly object _lockObject = new();
    private readonly ILogger _logger;

    public JsonStorage(ILogger logger)
    {
        _logger = logger;
    }

    public void Save<T>(T data, string fileName)
    {
        _logger.Verbose($"Saving data to file {fileName}");
        lock (_lockObject)
        {
            var jsonString = JsonConvert.SerializeObject(data, new JsonSerializerSettings { Formatting = Formatting.Indented });
            // Use UTF8 without BOM to prevent parsing issues
            File.WriteAllText(fileName, jsonString, new UTF8Encoding(false));
        }
    }

    public void Load<T>(string fileName, out T data)
    {
        _logger.Verbose($"Loading data from file {fileName}");
        lock (_lockObject)
        {
            var jsonString = File.ReadAllText(fileName);
            data = JsonConvert.DeserializeObject<T>(jsonString);
        }
    }
}
