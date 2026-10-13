using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

/// <summary>Read current persisted rules using an explicit model version; no inference or model client dependency.</summary>
public sealed class IndependentRuleReader(PostgresPipelineStore store) : IIndependentRuleReader, IScopedAutoInject
{
    public Task<IReadOnlyList<PipelineUnitResult>> ReadAsync(long projectId, string modelVersion, CancellationToken token) =>
        store.GetResultAsync(projectId, modelVersion, token);
}
