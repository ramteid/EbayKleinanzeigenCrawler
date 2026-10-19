using System.Collections.Generic;
using System.Linq;

namespace KleinanzeigenCrawler.Manager;

public static class KeywordParser
{
    /// <summary>
    /// Splits a comma separated list into trimmed, non-empty keywords
    /// </summary>
    public static List<string> Parse(string input)
    {
        return input.Split(',')
            .Select(keyword => keyword.Trim())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .ToList();
    }
}
