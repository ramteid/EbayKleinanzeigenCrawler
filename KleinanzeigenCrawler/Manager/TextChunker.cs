using System.Collections.Generic;

namespace KleinanzeigenCrawler.Manager;

public static class TextChunker
{
    /// <summary>
    /// Splits a text into chunks of at most the given length, preferably at line breaks
    /// </summary>
    public static IEnumerable<string> Split(string text, int maxLength)
    {
        var remaining = text.Trim();
        while (remaining.Length > maxLength)
        {
            var splitAt = remaining.LastIndexOf('\n', maxLength - 1, maxLength);
            if (splitAt <= 0)
            {
                splitAt = maxLength;
                if (splitAt > 1 && char.IsHighSurrogate(remaining[splitAt - 1]))
                {
                    splitAt--;
                }
            }

            var chunk = remaining[..splitAt].TrimEnd();
            remaining = remaining[splitAt..].TrimStart('\n');
            if (chunk.Length > 0)
            {
                yield return chunk;
            }
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }
}
