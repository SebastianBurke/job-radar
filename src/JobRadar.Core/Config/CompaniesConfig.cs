namespace JobRadar.Core.Config;

public sealed class CompaniesConfig
{
    public List<CompanyEntry> Companies { get; set; } = new();
}

public sealed class CompanyEntry
{
    public string Name { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Ats { get; set; } = "unknown";
    public string Token { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
