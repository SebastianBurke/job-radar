namespace JobRadar.Core.Models;

public enum LocationConfidence
{
    /// <summary>
    /// Aggregator's location tag is the only signal. Treat conservatively — a tag
    /// like "Remote" or "Worldwide" without a country qualifier may be wrong.
    /// </summary>
    AggregatorOnly,

    /// <summary>
    /// The location came directly from the ATS's structured location field.
    /// Trust it as the source of truth.
    /// </summary>
    Authoritative,
}
