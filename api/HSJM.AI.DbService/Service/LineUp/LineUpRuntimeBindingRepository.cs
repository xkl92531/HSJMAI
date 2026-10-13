using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.IDbService.LineUp;
using SqlSugar;

namespace HSJM.AI.DbService.Service.LineUp;

public sealed class LineUpRuntimeBindingRepository(ISqlSugarClient db)
    : ILineUpRuntimeBindingRepository, IAutoInject
{
    public async Task<LineUpRuntimeBindingData?> LoadBindingAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0) return null;
        var bindings = await db.Queryable<LineUpSubStationGraphBindingEntity>()
            .Where(row => !row.IsDeleted && row.Id == bindingId)
            .ToListAsync(cancellationToken);
        if (bindings.Count != 1) return null;
        var binding = bindings[0];
        return new(
            binding.Id,
            binding.LineId,
            binding.DeviceId,
            binding.SubStationId,
            binding.PlcProjectId,
            binding.SequenceId,
            binding.GraphBlockId,
            binding.InstanceDbBlockId,
            binding.RuntimeBindingJson,
            binding.BindingStatus,
            binding.MonitoringEnabled);
    }

    public async Task<LineUpRuntimeBindingContext?> LoadAsync(
        long bindingId,
        CancellationToken cancellationToken)
    {
        var binding = await LoadBindingAsync(bindingId, cancellationToken);
        if (binding is null) return null;
        if (binding.SequenceId <= 0 || binding.PlcProjectId <= 0 ||
            binding.InstanceDbBlockId is null or <= 0)
        {
            return null;
        }

        var sequences = await db.Queryable<LineUpGraphSequenceEntity>()
            .Where(row => !row.IsDeleted && row.Id == binding.SequenceId &&
                          row.PlcProjectId == binding.PlcProjectId &&
                          row.GraphBlockId == binding.GraphBlockId &&
                          row.InstanceDbBlockId == binding.InstanceDbBlockId)
            .ToListAsync(cancellationToken);
        if (sequences.Count != 1) return null;

        var blocks = await db.Queryable<PLC_BlockEntity>()
            .Where(row => !row.IsDeleted && row.Id == binding.InstanceDbBlockId &&
                          row.ProjectId == binding.PlcProjectId &&
                          row.ProgrammingLanguage == "DB" && row.BlockType == "InstanceDB")
            .ToListAsync(cancellationToken);
        if (blocks.Count != 1 || string.IsNullOrWhiteSpace(blocks[0].BlockName)) return null;

        var steps = await db.Queryable<LineUpGraphStepEntity>()
            .Where(row => !row.IsDeleted && row.SequenceId == binding.SequenceId)
            .OrderBy(row => row.StepNumber)
            .OrderBy(row => row.SourceKey)
            .ToListAsync(cancellationToken);
        if (steps.Count == 0) return null;

        var sequence = sequences[0];
        var block = blocks[0];
        return new(
            new(
                binding.Id,
                binding.LineId,
                binding.DeviceId,
                binding.SubStationId,
                binding.PlcProjectId,
                binding.SequenceId,
                binding.GraphBlockId,
                binding.InstanceDbBlockId,
                binding.RuntimeBindingJson,
                binding.BindingStatus,
                binding.MonitoringEnabled),
            new(sequence.Id, sequence.SourceKey, sequence.SourceHash),
            new(
                block.Id,
                block.ProjectId ?? 0,
                block.BlockName,
                block.BlockType,
                block.BlockNumber,
                block.ProgrammingLanguage),
            steps.Select(step => new LineUpRuntimeStepData(
                    step.Id,
                    step.SourceKey,
                    step.DisplayName,
                    step.StepNumber))
                .ToArray());
    }

    public async Task<bool> SavePreparedAsync(
        long bindingId,
        string runtimeBindingJson,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || string.IsNullOrWhiteSpace(runtimeBindingJson)) return false;
        var now = DateTime.UtcNow;
        var affected = await db.Updateable<LineUpSubStationGraphBindingEntity>()
            .SetColumns(row => new LineUpSubStationGraphBindingEntity
            {
                RuntimeBindingJson = runtimeBindingJson,
                BindingStatus = "NeedsReview",
                MonitoringEnabled = false,
                LastValidatedAt = null,
                ModifyTime = now
            })
            .Where(row => !row.IsDeleted && row.Id == bindingId &&
                          row.InstanceDbBlockId != null && row.InstanceDbBlockId > 0)
            .ExecuteCommandAsync()
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> SaveValidatedAsync(
        long bindingId,
        string expectedRuntimeBindingJson,
        DateTime validatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0 || string.IsNullOrWhiteSpace(expectedRuntimeBindingJson)) return false;
        var affected = await db.Updateable<LineUpSubStationGraphBindingEntity>()
            .SetColumns(row => new LineUpSubStationGraphBindingEntity
            {
                BindingStatus = "Resolved",
                MonitoringEnabled = false,
                LastValidatedAt = validatedAtUtc,
                ModifyTime = validatedAtUtc
            })
            .Where(row => !row.IsDeleted && row.Id == bindingId &&
                          row.BindingStatus == "NeedsReview" && !row.MonitoringEnabled &&
                          row.RuntimeBindingJson == expectedRuntimeBindingJson)
            .ExecuteCommandAsync()
            .WaitAsync(cancellationToken);
        return affected == 1;
    }

    public async Task<bool> SetMonitoringAsync(
        long bindingId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (bindingId <= 0) return false;
        var update = db.Updateable<LineUpSubStationGraphBindingEntity>()
            .SetColumns(row => new LineUpSubStationGraphBindingEntity
            {
                MonitoringEnabled = enabled,
                ModifyTime = DateTime.UtcNow
            })
            .Where(row => !row.IsDeleted && row.Id == bindingId);
        if (enabled)
        {
            update = update.Where(row => row.BindingStatus == "Resolved" &&
                                         row.LastValidatedAt != null &&
                                         row.RuntimeBindingJson != null);
        }

        var affected = await update.ExecuteCommandAsync().WaitAsync(cancellationToken);
        return affected == 1;
    }
}
