namespace JobRadar.Core.Config;

public sealed class SourcesConfig
{
    public RemotiveSourceConfig Remotive { get; set; } = new();
    public WeWorkRemotelySourceConfig WeWorkRemotely { get; set; } = new();
}

public sealed class RemotiveSourceConfig
{
    public List<string> SearchTerms { get; set; } = new();
}

public sealed class WeWorkRemotelySourceConfig
{
    public List<string> Feeds { get; set; } = new();
}
