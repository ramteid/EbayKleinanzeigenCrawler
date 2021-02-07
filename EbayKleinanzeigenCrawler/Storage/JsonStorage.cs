using System.IO;
using System.Text;
using EbayKleinanzeigenCrawler.Interfaces;
using Newtonsoft.Json;

namespace EbayKleinanzeigenCrawler.Storage
{
    public class JsonStorage : IDataStorage
    {
        private readonly object _lockObject = new object();

        public void Save<T>(T data, string fileName)
        {
            lock (_lockObject)
            {
                string jsonString = JsonConvert.SerializeObject(data, new JsonSerializerSettings { Formatting = Formatting.Indented });
                File.WriteAllText(fileName, jsonString, Encoding.UTF8);
            }
        }

        /// <summary>
        /// Deserializes the JSON file
        /// </summary>
        /// <exception cref="JsonException">Is thrown if the file doesn't exist</exception>
        public void Load<T>(string fileName, out T data)
        {
            lock (_lockObject)
            {
                string jsonString = File.ReadAllText(fileName);
                data = JsonConvert.DeserializeObject<T>(jsonString);
            }
        }
    }

}
