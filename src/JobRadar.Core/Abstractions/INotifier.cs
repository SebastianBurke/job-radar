using JobRadar.Core.Models;

namespace JobRadar.Core.Abstractions;

public interface INotifier
{
    Task SendDigestAsync(IReadOnlyList<DigestEntry> entries, CancellationToken ct = default);
}
