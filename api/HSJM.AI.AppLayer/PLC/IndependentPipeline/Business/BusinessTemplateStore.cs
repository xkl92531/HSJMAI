#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Npgsql;
using NpgsqlTypes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed record BusinessTemplateAnalysis(BusinessDescription Template,string ModelVersion,string Summary,
    IReadOnlyList<BusinessDecision> Decisions,string Status);

/// <summary>Immutable, test-only block analyses. These records never activate plant constraints.</summary>
public sealed class BusinessTemplateStore(PostgresPipelineStore sourceStore):ISelfScopedAutoInject
{
    public async Task<BusinessTemplateAnalysis?> LoadAsync(long project,string fingerprint,string version,CancellationToken token) {
        await using var connection=new NpgsqlConnection(sourceStore.ConnectionString);await connection.OpenAsync(token);
        await EnsureAsync(connection,token);
        await using var cmd=new NpgsqlCommand("SELECT payload::text FROM plc_independent.business_template WHERE project_id=@p AND fingerprint=@f AND model_version=@v",connection);
        cmd.Parameters.AddWithValue("p",project);cmd.Parameters.AddWithValue("f",fingerprint);cmd.Parameters.AddWithValue("v",version);
        return await cmd.ExecuteScalarAsync(token) is string json?JsonSerializer.Deserialize<BusinessTemplateAnalysis>(json):null;
    }
    public async Task SaveAsync(long project,BusinessTemplateAnalysis analysis,CancellationToken token) {
        await using var connection=new NpgsqlConnection(sourceStore.ConnectionString);await connection.OpenAsync(token);
        await EnsureAsync(connection,token);
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO plc_independent.business_template(project_id,block_id,fingerprint,model_version,payload)
            VALUES(@p,@b,@f,@v,@j) ON CONFLICT DO NOTHING
            """,connection);
        cmd.Parameters.AddWithValue("p",project);cmd.Parameters.AddWithValue("b",analysis.Template.BlockId);
        cmd.Parameters.AddWithValue("f",analysis.Template.Fingerprint);cmd.Parameters.AddWithValue("v",analysis.ModelVersion);
        cmd.Parameters.AddWithValue("j",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(analysis));await cmd.ExecuteNonQueryAsync(token);
    }
    static async Task EnsureAsync(NpgsqlConnection c,CancellationToken token) {
        await using var cmd=new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS plc_independent;
            CREATE TABLE IF NOT EXISTS plc_independent.business_template(
                project_id bigint NOT NULL,block_id bigint NOT NULL,fingerprint text NOT NULL,model_version text NOT NULL,payload jsonb NOT NULL,
                PRIMARY KEY(project_id,fingerprint,model_version));
            """,c);await cmd.ExecuteNonQueryAsync(token);
    }
}
