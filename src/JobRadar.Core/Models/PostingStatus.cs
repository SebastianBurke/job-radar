namespace JobRadar.Core.Models;

public enum PostingStatus
{
    Pending,
    Applied,
    Dismissed,
    Expired,

    /// <summary>ATS live-check returned 404 / login wall; posting is no longer reachable.</summary>
    Dead,
}
