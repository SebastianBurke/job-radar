using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JobRadar.App;

public sealed class AppliedYamlEntry
{
    public string Url { get; set; } = string.Empty;
    // ISO 8601 string. We store it as text (rather than DateTimeOffset) because YamlDotNet
    // expands DateTimeOffset into its component properties, which produces unreadable YAML.
    public string At { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public sealed class AppliedYamlDocument
{
    public List<AppliedYamlEntry> Applied { get; set; } = new();
    public List<AppliedYamlEntry> Dismissed { get; set; } = new();
}

public static class AppliedYamlStore
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static AppliedYamlDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppliedYamlDocument();
        }
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AppliedYamlDocument();
        }
        return Deserializer.Deserialize<AppliedYamlDocument>(text) ?? new AppliedYamlDocument();
    }

    // Append a new entry under the matching list (applied or dismissed). Idempotent on URL —
    // if the URL is already present in the same list, the existing entry is kept untouched.
    public static void Append(string path, string list, string url, string? note, DateTimeOffset at)
    {
        var doc = Load(path);
        var bucket = list switch
        {
            "applied" => doc.Applied,
            "dismissed" => doc.Dismissed,
            _ => throw new ArgumentException($"Unknown list '{list}' (expected 'applied' or 'dismissed')."),
        };

        if (bucket.Any(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        bucket.Add(new AppliedYamlEntry
        {
            Url = url,
            At = at.ToString("O"),
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
        });

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var yaml = Serializer.Serialize(doc);
        var header = "# Postings the user has acted on. Edit this file (or use the CLI helpers)\n" +
                     "# and commit before the next scheduled run.\n" +
                     "#\n" +
                     "# `url` is the unique key. `at` is informational. `note` is optional.\n";
        File.WriteAllText(path, header + yaml);
    }
}
