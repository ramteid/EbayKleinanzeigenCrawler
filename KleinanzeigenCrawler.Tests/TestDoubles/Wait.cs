using System;
using System.Threading.Tasks;

namespace KleinanzeigenCrawler.Tests.TestDoubles;

public static class Wait
{
    /// <summary>
    /// Polls the condition until it holds. Returns whether it held before the timeout elapsed.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }
}
