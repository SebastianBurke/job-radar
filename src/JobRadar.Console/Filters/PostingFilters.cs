using System.Text.RegularExpressions;
using JobRadar.Core.Config;
using JobRadar.Core.Models;

namespace JobRadar.App.Filters;

public sealed class PostingFilters
{
    private readonly Regex? _coreRegex;
    private readonly Regex? _broadRegex;
    private readonly Regex? _techHintRegex;
    private readonly Regex? _locationAllowRegex;
    private readonly Regex? _locationDenyRegex;

    public PostingFilters(FiltersConfig config)
    {
        _coreRegex = BuildKeywordRegex(config.KeywordsCore);
        _broadRegex = BuildKeywordRegex(config.KeywordsBroad);
        _techHintRegex = BuildKeywordRegex(config.TechContextHints);
        _locationAllowRegex = BuildContainsRegex(config.LocationAllow);
        _locationDenyRegex = BuildContainsRegex(config.LocationDenyPhrases);
    }

    public bool PassesKeyword(JobPosting posting)
    {
        var haystack = $"{posting.Title}\n{posting.Description}";

        if (_coreRegex is not null && _coreRegex.IsMatch(haystack)) return true;

        if (_broadRegex is not null && _broadRegex.IsMatch(haystack)
            && _techHintRegex is not null && _techHintRegex.IsMatch(haystack))
        {
            return true;
        }

        // If both lists are empty, fail open (don't drop everything).
        return _coreRegex is null && _broadRegex is null;
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

    private static Regex? BuildKeywordRegex(IEnumerable<string> keywords)
    {
        var alternation = string.Join("|", keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Regex.Escape(k.Trim())
                .Replace("\\ ", "[\\- ]?")));
        return string.IsNullOrEmpty(alternation)
            ? null
            : new Regex($@"(?i)(?<![A-Za-z]){alternation}(?![A-Za-z])", RegexOptions.Compiled);
    }

    private static Regex? BuildContainsRegex(IEnumerable<string> phrases)
    {
        var pattern = string.Join("|", phrases
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Regex.Escape(p.Trim())));
        return string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
