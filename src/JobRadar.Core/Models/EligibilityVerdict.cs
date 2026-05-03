using System.Text.Json.Serialization;

namespace JobRadar.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EligibilityVerdict
{
    Eligible,
    Ineligible,
    Ambiguous,
}
