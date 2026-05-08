namespace JobRadar.Core.Models;

public enum LiveCheckVerdict
{
    /// <summary>Source confirmed the posting is still listed.</summary>
    Live,

    /// <summary>Source returned 404 / "no longer available" / login wall — posting is gone.</summary>
    Dead,

    /// <summary>Probe failed for reasons other than a definite 404 (timeout, 5xx, parse error).</summary>
    Unknown,
}
