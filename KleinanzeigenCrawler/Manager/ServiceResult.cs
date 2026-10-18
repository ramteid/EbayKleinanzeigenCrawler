namespace KleinanzeigenCrawler.Manager;

/// <summary>
/// Result wrapper for service operations
/// </summary>
public class ServiceResult<T>
{
    public bool IsSuccess { get; private set; }
    public T Data { get; private set; }
    public string ErrorMessage { get; private set; }
    
    private ServiceResult() { }
    
    public static ServiceResult<T> Ok(T data)
    {
        return new ServiceResult<T>
        {
            IsSuccess = true,
            Data = data
        };
    }
    
    public static ServiceResult<T> Fail(string errorMessage)
    {
        return new ServiceResult<T>
        {
            IsSuccess = false,
            ErrorMessage = errorMessage
        };
    }
}
