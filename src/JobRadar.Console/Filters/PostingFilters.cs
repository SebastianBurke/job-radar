using System.Text.RegularExpressions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;

namespace JobRadar.App.Filters;

public sealed class PostingFilters
{
    private readonly Regex _keywordRegex;
    private readonly Regex? _locationAllowRegex;
    private readonly Regex? _locationDenyRegex;

    public PostingFilters(FiltersConfig config)
    {
        _keywordRegex = BuildKeywordRegex(config.KeywordsRequired);
        _locationAllowRegex = BuildContainsRegex(config.LocationAllow);
        _locationDenyRegex = BuildContainsRegex(config.LocationDenyPhrases);
    }

    public bool PassesKeyword(JobPosting posting)
    {
        if (_keywordRegex.IsMatch(posting.Title)) return true;
        if (!string.IsNullOrEmpty(posting.Description) && _keywordRegex.IsMatch(posting.Description)) return true;
        return false;
    }

    public bool PassesLocation(JobPosting posting)
    {
        var haystack = $"{posting.Location} \n {posting.Description}";

        if (_locationDenyRegex is not null && _locationDenyRegex.IsMatch(haystack))
        {
            return false;
        }
        if (_locationAllowRegex is null) return true;
        return _locationAllowRegex.IsMatch(haystack);
    }

    private static Regex BuildKeywordRegex(IEnumerable<string> keywords)
    {
        var alternation = string.Join("|", keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Regex.Escape(k.Trim())
                .Replace("\\ ", "[\\- ]?")));
        if (string.IsNullOrEmpty(alternation))
        {
            // Default fallback — match anything
            return new Regex(".", RegexOptions.Compiled);
        }
        return new Regex($@"(?i)(?<![A-Za-z]){alternation}(?![A-Za-z])", RegexOptions.Compiled);
    }

    private static Regex? BuildContainsRegex(IEnumerable<string> phrases)
    {
        var pattern = string.Join("|", phrases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Regex.Escape(p.Trim())));
        return string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
