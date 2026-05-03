using System.Security.Cryptography;
using System.Text;

namespace JobRadar.Core.Models;

public sealed record JobPosting(
    string Source,
    string Company,
    string Title,
    string Location,
    string Url,
    string Description,
    DateTimeOffset? PostedAt = null,
    string? Department = null,
    string? RawPayload = null)
{
    public string Hash => ComputeHash(Company, Title, Description);

    public static string ComputeHash(string company, string title, string description)
    {
        var snippet = description.Length > 200 ? description[..200] : description;
        var input = $"{company}|{title}|{snippet}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
