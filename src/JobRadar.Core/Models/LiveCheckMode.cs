namespace JobRadar.Core.Models;

public enum LiveCheckMode
{
    /// <summary>Skip the live check entirely; behave like the legacy pipeline.</summary>
    None,

    /// <summary>Probe the source; on a non-live verdict, log and let scoring proceed.</summary>
    BestEffort,

    /// <summary>Probe the source; on a non-live verdict, drop the posting before scoring.</summary>
    RequireOk,
}
