namespace EbayKleinanzeigenCrawler.Interfaces
{
    public interface IDataStorage
    {
        void Save<T>(T data, string fileName);
        void Load<T>(string fileName, out T data);
    }
}