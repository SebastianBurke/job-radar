using System.Net;
using System.Text.RegularExpressions;

namespace JobRadar.Sources.Internal;

public static class HtmlText
{
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string Strip(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var decoded = WebUtility.HtmlDecode(html);
        var stripped = TagRegex.Replace(decoded, " ");
        return WhitespaceRegex.Replace(stripped, " ").Trim();
    }
}
