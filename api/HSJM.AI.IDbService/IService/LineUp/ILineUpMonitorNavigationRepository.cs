#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.IDbService.LineUp;

namespace HSJM.AI.IDbService;

public interface ILineUpMonitorNavigationRepository
{
    Task<IReadOnlyList<LineUpMonitorNavigationRow>> LoadAsync(
        bool includeUnconfigured,
        string? keyword,
        CancellationToken cancellationToken);
}
