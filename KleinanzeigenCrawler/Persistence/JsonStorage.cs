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
            
            // The data is written to a temporary file that replaces the target file only once it is complete,
            // so that a crash or a full disk cannot leave a truncated file behind
            var temporaryFileName = fileName + ".tmp";
            using (var stream = new FileStream(temporaryFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Use UTF8 without BOM to prevent parsing issues
                stream.Write(new UTF8Encoding(false).GetBytes(jsonString));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryFileName, fileName, overwrite: true);
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
