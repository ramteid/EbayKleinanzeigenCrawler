using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;

namespace KleinanzeigenCrawler.Models;

/// <summary>
/// Holds the data collected during the "Add Subscription" workflow
/// </summary>
public class AddSubscriptionData
{
    public string Url { get; set; }
    public List<string> IncludeKeywords { get; set; } = new();
    public List<string> ExcludeKeywords { get; set; } = new();
    public bool InitialPull { get; set; }
    public string Title { get; set; }
    
    /// <summary>
    /// Current step in the workflow (0-based)
    /// </summary>
    public int Step { get; set; } = 0;
    
    /// <summary>
    /// Workflow steps
    /// </summary>
    public const int STEP_URL = 0;
    public const int STEP_INCLUDE_KEYWORDS = 1;
    public const int STEP_EXCLUDE_KEYWORDS = 2;
    public const int STEP_INITIAL_PULL = 3;
    public const int STEP_TITLE = 4;
    public const int STEP_CONFIRM = 5;
    
    public string GetSummary()
    {
        var encoder = HtmlEncoder.Default;
        var includeKeywords = IncludeKeywords?.Count > 0
            ? string.Join(", ", IncludeKeywords.Select(keyword => encoder.Encode(keyword)))
            : "None";
        var excludeKeywords = ExcludeKeywords?.Count > 0
            ? string.Join(", ", ExcludeKeywords.Select(keyword => encoder.Encode(keyword)))
            : "None";

        return $@"
<b>Subscription Summary</b>

<b>Title:</b> {encoder.Encode(Title ?? "Not set")}
<b>URL:</b> {encoder.Encode(Url ?? "Not set")}
<b>Include Keywords:</b> {includeKeywords}
<b>Exclude Keywords:</b> {excludeKeywords}
<b>Get Existing Results:</b> {(InitialPull ? "Yes" : "No")}
";
    }
}
