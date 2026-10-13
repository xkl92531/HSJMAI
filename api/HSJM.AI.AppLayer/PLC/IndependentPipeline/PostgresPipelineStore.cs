#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline;

/// <summary>Explicit test-only connection. Original PLC tables are SELECT-only; all writes use a separate schema.</summary>
public sealed class PostgresPipelineStore(IConfiguration configuration) : ISelfScopedAutoInject, IPipelineStore, IPipelineBatchStore
{
    public const string MigrationSql = """
        CREATE SCHEMA IF NOT EXISTS plc_independent;
        CREATE TABLE IF NOT EXISTS plc_independent.run (
          id uuid PRIMARY KEY, project_id bigint NOT NULL, payload jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS plc_independent.description (
          project_id bigint NOT NULL, lu_id bigint NOT NULL, fingerprint text NOT NULL, payload jsonb NOT NULL,
          PRIMARY KEY(project_id, lu_id, fingerprint));
        CREATE TABLE IF NOT EXISTS plc_independent.rule (
          run_id uuid NOT NULL, project_id bigint NOT NULL, lu_id bigint NOT NULL, rule_key text NOT NULL,
          operator text NOT NULL, description text NOT NULL, evaluation_point text NOT NULL,
          PRIMARY KEY(run_id, lu_id, rule_key));
        CREATE TABLE IF NOT EXISTS plc_independent.member (
          run_id uuid NOT NULL, lu_id bigint NOT NULL, rule_key text NOT NULL, name text NOT NULL, role text NOT NULL,
          PRIMARY KEY(run_id, lu_id, rule_key, name, role),
          FOREIGN KEY(run_id, lu_id, rule_key) REFERENCES plc_independent.rule(run_id, lu_id, rule_key));
        CREATE TABLE IF NOT EXISTS plc_independent.active (
          project_id bigint NOT NULL, lu_id bigint NOT NULL, generation_key text NOT NULL,
          description_key text NOT NULL, run_id uuid NOT NULL, rule_count integer NOT NULL,
          PRIMARY KEY(project_id, lu_id));
        CREATE TABLE IF NOT EXISTS plc_independent.commit_record (
          project_id bigint NOT NULL,lu_id bigint NOT NULL,run_id uuid NOT NULL,generation_key text NOT NULL,
          PRIMARY KEY(project_id,lu_id,run_id));
        INSERT INTO plc_independent.commit_record(project_id,lu_id,run_id,generation_key)
          SELECT project_id,lu_id,run_id,generation_key FROM plc_independent.active ON CONFLICT DO NOTHING;
        """;

    public string ConnectionString {
        get {
            var section = configuration.GetSection("IndependentPlcPipeline");
            // The isolated test instance may reuse its already configured business database
            // connection. PipelineSettings still rejects every non-_test database.
            var connection = section["ConnectionString"]
                ?? configuration["WebConfig:DBList:0:ConnectionString"] ?? "";
            PipelineSettings.Validate(section.GetValue<bool>("Enabled"), connection, section["ExpectedDatabase"] ?? "");
            return connection;
        }
    }
    /// <summary>
    /// 不抛异常的取连接串：管道未启用 / 连接配置不合法（PipelineSettings 拒绝、连接串格式错误）时返回 false 与原因代码。
    /// 这是配置状态，不是读库失败——调用方可据此判断「没有独立管道的数据」。
    /// </summary>
    public bool TryGetConnectionString(out string connectionString, out string? reason) {
        try { connectionString = ConnectionString; reason = null; return true; }
        catch (PipelineException ex) { connectionString = ""; reason = ex.Code; return false; }
        catch (ArgumentException ex) { connectionString = ""; reason = "InvalidConnectionString:" + ex.GetType().Name; return false; }
    }
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken token) {
        var connection = new NpgsqlConnection(ConnectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = new NpgsqlCommand(MigrationSql, connection, transaction);
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
    }
    public async Task<PipelineSnapshot> LoadSourceAsync(long project, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(token);
        var result = await ReadSourceAsync(connection, transaction, project, token);
        await transaction.CommitAsync(token); return result;
    }
    internal static async Task<PipelineSnapshot> ReadSourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long project, CancellationToken token)
    {
        // Do not SELECT *: independent reads must not require columns added by the other implementation.
        await using var columns=new NpgsqlCommand("select exists(select 1 from pg_attribute where attrelid=to_regclass('ai_plc_logicunit') and attname='interfacexml' and not attisdropped)",connection,transaction);
        var hasMetadata=(bool)(await columns.ExecuteScalarAsync(token))!;
        // Interface declarations are repeated on every LU in imported snapshots. Transfer each distinct
        // declaration once, then bind back by checked content hash, rather than sending ~180M chars/run.
        var metadata=hasMetadata?"md5(coalesce(lu.interfacexml,'')),coalesce(lu.blockkind,''),coalesce(lu.sourcefile,''),coalesce(lu.unitkind,'Network')":"''::text,coalesce(b.blocktype,''),''::text,coalesce(lu.unittype,'Network')";
        var interfaceSql=hasMetadata ? """
          SELECT DISTINCT md5(coalesce(lu.interfacexml,'')),coalesce(lu.interfacexml,'')
          FROM ai_plc_logicunit lu JOIN ai_plc_block_new b ON b.id=lu.blockid JOIN ai_plc_project_new p ON p.id=b.projectid
          WHERE b.projectid=@project AND NOT b.isdeleted AND NOT lu.isdeleted AND NOT p.isdeleted;
          """ : "";
        var sql = $$"""
          SELECT lu.id, b.id, coalesce(b.blockname,''), coalesce(lu.programminglanguage,''),
                 coalesce(lu.rawcontent,''), coalesce(lu.callpinbindingsjson,''), lu.networkid, {{metadata}}
          FROM ai_plc_logicunit lu JOIN ai_plc_block_new b ON b.id=lu.blockid
          JOIN ai_plc_project_new p ON p.id=b.projectid
          WHERE b.projectid=@project AND NOT b.isdeleted AND NOT lu.isdeleted AND NOT p.isdeleted ORDER BY lu.id;
          SELECT coalesce(tagname,''),coalesce(address,''),coalesce(datatype,'') FROM ai_plc_iotag
          WHERE projectid=@project AND NOT isdeleted ORDER BY id;
          SELECT logicunitid, calleeblockname FROM ai_plc_blockcall
          WHERE projectid=@project AND NOT isdeleted AND logicunitid IS NOT NULL AND calleeblockname IS NOT NULL;
          {{interfaceSql}}
          """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("project", project);
        await using var reader = await command.ExecuteReaderAsync(token);
        var units = new List<PipelineUnit>(); var tags = new List<PipelineTag>();
        while (await reader.ReadAsync(token))
            units.Add(new(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(0), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), [],reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetString(10)));
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token)) tags.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        await reader.NextResultAsync(token);
        var calls = new Dictionary<long, List<string>>();
        while (await reader.ReadAsync(token)) {
            var id = reader.GetInt64(0);
            if (!calls.TryGetValue(id, out var names)) calls[id] = names = [];
            names.Add(reader.GetString(1));
        }
        if(hasMetadata) {
            await reader.NextResultAsync(token);
            var interfaces=new Dictionary<string,string>(StringComparer.Ordinal);
            while(await reader.ReadAsync(token)) {
                var hash=reader.GetString(0);var content=reader.GetString(1);
                if(interfaces.TryGetValue(hash,out var previous)&&previous!=content)throw new PipelineException("AmbiguousInterfaceContentHash");
                interfaces[hash]=content;
            }
            units=units.Select(u=>u with {InterfaceXml=interfaces.TryGetValue(u.InterfaceXml,out var content)?content:throw new PipelineException("MissingInterfaceContent")}).ToList();
        }
        else {
            // Current upload schema stores block interface declarations separately from LU.
            // Top-level and one-level nested declarations retain typed pins and state fields;
            // selecting expanded array elements would transfer millions of unrelated rows.
            await reader.CloseAsync();
            const string declarationsSql="""
                SELECT bi.blockid,bi.variablepath,bi.datatype,bi.scope
                FROM ai_plc_blockinterface bi JOIN ai_plc_block_new b ON b.id=bi.blockid
                WHERE b.projectid=@project AND NOT b.isdeleted AND NOT bi.isdeleted
                  AND bi.variablepath IS NOT NULL
                  AND position('[' in bi.variablepath)=0
                  AND length(bi.variablepath)-length(replace(bi.variablepath,'.',''))<=1
                ORDER BY bi.blockid,bi.id;
                """;
            await using var declarationsCommand=new NpgsqlCommand(declarationsSql,connection,transaction);
            declarationsCommand.Parameters.AddWithValue("project",project);
            await using var declarations=await declarationsCommand.ExecuteReaderAsync(token);
            var byBlock=new Dictionary<long,List<PipelineInterfaceMember>>();
            while(await declarations.ReadAsync(token)) {
                var id=declarations.GetInt64(0);
                if(!byBlock.TryGetValue(id,out var members))byBlock[id]=members=[];
                members.Add(new(declarations.GetString(1),declarations.IsDBNull(2)?"":declarations.GetString(2),
                    declarations.IsDBNull(3)?"":declarations.GetString(3)));
            }
            var xmlByBlock=byBlock.ToDictionary(entry=>entry.Key,entry=>PipelineInterfaceXmlBuilder.Build(entry.Value));
            units=units.Select(unit=>unit with {InterfaceXml=xmlByBlock.GetValueOrDefault(unit.BlockId,"")}).ToList();
        }
        if (units.Count == 0) throw new PipelineException("ProjectSourceNotFound");
        return new(project, units.Select(u => u with { Callees = calls.GetValueOrDefault(u.Id, []).Distinct(StringComparer.Ordinal).ToArray() }).ToArray(), tags);
    }
    public async Task<IReadOnlyDictionary<string, PipelineDescription>> LoadDescriptionsAsync(long project, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT fingerprint,payload::text FROM plc_independent.description WHERE project_id=@project", connection);
        command.Parameters.AddWithValue("project", project);
        await using var reader = await command.ExecuteReaderAsync(token);
        var descriptions = new Dictionary<string,PipelineDescription>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token)) {
            var item = JsonSerializer.Deserialize<PipelineDescription>(reader.GetString(1));
            if (item != null) descriptions[reader.GetString(0)] = item;
        }
        return descriptions;
    }
    public async Task SaveDescriptionsAsync(long project, IReadOnlyList<PipelineDescription> descriptions, CancellationToken token) {
        if (descriptions.Count == 0) return;
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE description_batch (LIKE plc_independent.description INCLUDING DEFAULTS) ON COMMIT DROP", connection, transaction))
            await create.ExecuteNonQueryAsync(token);
        using (var copy = connection.BeginBinaryImport("COPY description_batch (project_id,lu_id,fingerprint,payload) FROM STDIN (FORMAT BINARY)")) {
            foreach (var d in descriptions) {
                token.ThrowIfCancellationRequested(); copy.StartRow(); copy.Write(project, NpgsqlDbType.Bigint);
                copy.Write(d.LogicUnitId, NpgsqlDbType.Bigint); copy.Write(d.Fingerprint, NpgsqlDbType.Text);
                copy.Write(JsonSerializer.Serialize(d), NpgsqlDbType.Jsonb);
            }
            copy.Complete();
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO plc_independent.description SELECT * FROM description_batch ON CONFLICT DO NOTHING", connection, transaction))
            await insert.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }
    public async Task<bool> IsCompleteAsync(long project, long lu, string key, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM plc_independent.active WHERE project_id=@p AND lu_id=@l AND generation_key=@k)", connection);
        command.Parameters.AddWithValue("p",project); command.Parameters.AddWithValue("l",lu); command.Parameters.AddWithValue("k",key);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }
    public async Task<IReadOnlyDictionary<long,string>> LoadCompletionsAsync(long project,CancellationToken token) {
        await using var connection=await OpenAsync(token);
        await using var command=new NpgsqlCommand("SELECT lu_id,generation_key FROM plc_independent.active WHERE project_id=@p",connection);
        command.Parameters.AddWithValue("p",project);
        await using var reader=await command.ExecuteReaderAsync(token);
        var result=new Dictionary<long,string>();while(await reader.ReadAsync(token))result.Add(reader.GetInt64(0),reader.GetString(1));
        return result;
    }
    public Task CommitAsync(long project,Guid run,PipelineDescription description,string key,IReadOnlyList<PipelineRule> rules,CancellationToken token)
        => CommitBatchAsync(project,run,[new(description,key,rules)],token);
    public async Task CommitBatchAsync(long project, Guid run, IReadOnlyList<PipelineCommit> batch, CancellationToken token)
    {
        if(batch.Count==0)return;
        if(batch.Select(b=>b.Description.LogicUnitId).Distinct().Count()!=batch.Count)throw new PipelineException("DuplicateCommitUnit");
        // The current rule/member schema persists only operator, members and description. Never
        // strip guards, scope or settling requirements and turn a diagnostic expectation into
        // an immediately active IMPLIES rule.
        if(batch.Any(b=>b.Rules.Any(r=>r.Applicability!=null||r.RequiresSettlingEvidence||r.BypassSignals.Count>0)))
            throw new PipelineException("ConditionalRulePersistenceUnsupported");
        await using var connection = await OpenAsync(token);
        // Source SHARE locks freeze all source relations, while a project transaction lock serializes
        // activations. READ COMMITTED avoids unrelated projects causing PostgreSQL SSI false conflicts.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await using(var projectLock=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",connection,transaction)) {
            projectLock.Parameters.AddWithValue("key","plc-independent-commit:"+project);
            await projectLock.ExecuteNonQueryAsync(token);
        }
        await using (var sourceLock = new NpgsqlCommand("""
            LOCK TABLE ai_plc_project_new,ai_plc_block_new,ai_plc_logicunit,ai_plc_blockcall,ai_plc_iotag IN SHARE MODE
            """, connection, transaction)) await sourceLock.ExecuteNonQueryAsync(token);
        var source = await ReadSourceAsync(connection, transaction, project, token);
        var fingerprint=source.Fingerprint();var units=source.Units.ToDictionary(u=>u.Id);
        foreach(var item in batch)
            if(!units.TryGetValue(item.Description.LogicUnitId,out var unit)||PipelineEvidenceBuilder.Key(fingerprint,unit)!=item.Description.Fingerprint)
                throw new PipelineException("SourceChangedBeforeCommit");
        await using(var history=new NpgsqlCommand("""
            SELECT h.lu_id,h.generation_key,a.run_id FROM plc_independent.commit_record h
            LEFT JOIN plc_independent.active a ON a.project_id=h.project_id AND a.lu_id=h.lu_id
            WHERE h.project_id=@p AND h.run_id=@r
            """,connection,transaction)) {
            history.Parameters.AddWithValue("p",project);history.Parameters.AddWithValue("r",run);
            await using var reader=await history.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token)) {
                var requested=batch.FirstOrDefault(b=>b.Description.LogicUnitId==reader.GetInt64(0));
                if(requested is null)continue;
                if(reader.IsDBNull(2)||reader.GetGuid(2)!=run)throw new PipelineException("CommitSuperseded");
                if(requested.GenerationKey!=reader.GetString(1))throw new PipelineException("RunGenerationMismatch");
            }
        }
        var existingKeys=new Dictionary<long,string>();
        await using(var existing=new NpgsqlCommand("SELECT lu_id,generation_key FROM plc_independent.active WHERE project_id=@p AND run_id=@r",connection,transaction)) {
            existing.Parameters.AddWithValue("p",project);existing.Parameters.AddWithValue("r",run);
            await using var reader=await existing.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))existingKeys.Add(reader.GetInt64(0),reader.GetString(1));
        }
        var pending=batch.Where(b=>!existingKeys.TryGetValue(b.Description.LogicUnitId,out var k)||k!=b.GenerationKey).ToArray();
        using (var copy = connection.BeginBinaryImport("COPY plc_independent.rule (run_id,project_id,lu_id,rule_key,operator,description,evaluation_point) FROM STDIN (FORMAT BINARY)")) {
            foreach(var item in pending) foreach (var rule in item.Rules) {
                token.ThrowIfCancellationRequested(); copy.StartRow(); copy.Write(run,NpgsqlDbType.Uuid); copy.Write(project,NpgsqlDbType.Bigint);
                copy.Write(item.Description.LogicUnitId,NpgsqlDbType.Bigint); copy.Write(rule.Key,NpgsqlDbType.Text);
                copy.Write(rule.Operator,NpgsqlDbType.Text); copy.Write(rule.Description,NpgsqlDbType.Text); copy.Write("AfterLogicUnit",NpgsqlDbType.Text);
            } copy.Complete();
        }
        using (var copy = connection.BeginBinaryImport("COPY plc_independent.member (run_id,lu_id,rule_key,name,role) FROM STDIN (FORMAT BINARY)")) {
            foreach(var item in pending) foreach (var rule in item.Rules) foreach (var signal in rule.Signals) {
                token.ThrowIfCancellationRequested(); copy.StartRow(); copy.Write(run,NpgsqlDbType.Uuid); copy.Write(item.Description.LogicUnitId,NpgsqlDbType.Bigint);
                copy.Write(rule.Key,NpgsqlDbType.Text); copy.Write(signal.Name,NpgsqlDbType.Text); copy.Write(signal.Role,NpgsqlDbType.Text);
            } copy.Complete();
        }
        await using(var create=new NpgsqlCommand("CREATE TEMP TABLE active_batch (LIKE plc_independent.active INCLUDING DEFAULTS) ON COMMIT DROP",connection,transaction))
            await create.ExecuteNonQueryAsync(token);
        using(var copy=connection.BeginBinaryImport("COPY active_batch(project_id,lu_id,generation_key,description_key,run_id,rule_count) FROM STDIN (FORMAT BINARY)")) {
            foreach(var item in pending) {
                token.ThrowIfCancellationRequested();copy.StartRow();copy.Write(project,NpgsqlDbType.Bigint);copy.Write(item.Description.LogicUnitId,NpgsqlDbType.Bigint);
                copy.Write(item.GenerationKey,NpgsqlDbType.Text);copy.Write(item.Description.Fingerprint,NpgsqlDbType.Text);copy.Write(run,NpgsqlDbType.Uuid);copy.Write(item.Rules.Count,NpgsqlDbType.Integer);
            }copy.Complete();
        }
        await using var activate = new NpgsqlCommand("""
            INSERT INTO plc_independent.commit_record(project_id,lu_id,run_id,generation_key)
            SELECT a.project_id,a.lu_id,a.run_id,a.generation_key FROM plc_independent.active a
            JOIN active_batch b ON b.project_id=a.project_id AND b.lu_id=a.lu_id ON CONFLICT DO NOTHING;
            INSERT INTO plc_independent.active SELECT * FROM active_batch ON CONFLICT(project_id,lu_id) DO UPDATE SET
            generation_key=excluded.generation_key,description_key=excluded.description_key,run_id=excluded.run_id,rule_count=excluded.rule_count
            ;INSERT INTO plc_independent.commit_record(project_id,lu_id,run_id,generation_key)
            SELECT project_id,lu_id,run_id,generation_key FROM active_batch ON CONFLICT DO NOTHING
            """, connection, transaction);
        await activate.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
    }
    public async Task SaveRunAsync(PipelineRun run, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            INSERT INTO plc_independent.run(id,project_id,payload) VALUES(@r,@p,@j)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,updated_at=now()
            """, connection);
        command.Parameters.AddWithValue("r",run.RunId); command.Parameters.AddWithValue("p",run.ProjectId);
        command.Parameters.AddWithValue("j",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(run)); await command.ExecuteNonQueryAsync(token);
    }
    public async Task<PipelineRun?> GetRunAsync(Guid run, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT payload::text FROM plc_independent.run WHERE id=@r",connection);
        command.Parameters.AddWithValue("r",run); var result = await command.ExecuteScalarAsync(token) as string;
        return result is null ? null : JsonSerializer.Deserialize<PipelineRun>(result);
    }
    public async Task<IReadOnlyList<PipelineUnitResult>> GetResultAsync(long project, string modelVersion, CancellationToken token) {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);
        var source = await ReadSourceAsync(connection,transaction,project,token);
        var sourceFingerprint = source.Fingerprint();
        await using var command = new NpgsqlCommand("""
            SELECT a.lu_id,a.description_key,a.generation_key,d.payload::text,
                   r.rule_key,r.operator,r.description,m.name,m.role
            FROM plc_independent.active a
            LEFT JOIN plc_independent.description d ON d.project_id=a.project_id AND d.lu_id=a.lu_id AND d.fingerprint=a.description_key
            LEFT JOIN plc_independent.rule r ON r.run_id=a.run_id AND r.project_id=a.project_id AND r.lu_id=a.lu_id
            LEFT JOIN plc_independent.member m ON m.run_id=r.run_id AND m.lu_id=r.lu_id AND m.rule_key=r.rule_key
            WHERE a.project_id=@p ORDER BY a.lu_id,r.rule_key,m.name
            """,connection,transaction);
        command.Parameters.AddWithValue("p",project);
        var rows = new Dictionary<long,(string DescriptionKey,string GenerationKey,PipelineDescription? Description,Dictionary<string,PipelineRule> Rules)>();
        await using (var reader = await command.ExecuteReaderAsync(token)) {
            while(await reader.ReadAsync(token)) {
                var lu = reader.GetInt64(0);
                if (!rows.TryGetValue(lu,out var row)) {
                    row = (reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<PipelineDescription>(reader.GetString(3)), new());
                    rows[lu] = row;
                }
                if (reader.IsDBNull(4)) continue;
                var ruleKey = reader.GetString(4);
                if (!row.Rules.TryGetValue(ruleKey,out var rule))
                    row.Rules[ruleKey] = rule = new(reader.GetString(5),new List<PipelineSignal>(),reader.GetString(6));
                if (!reader.IsDBNull(7)) ((List<PipelineSignal>)rule.Signals).Add(new(reader.GetString(7),reader.GetString(8)));
            }
        }
        var result = new List<PipelineUnitResult>();
        foreach (var lu in rows.Keys.Union(source.Units.Select(u => u.Id)).OrderBy(id => id)) {
            var unit = source.Units.SingleOrDefault(u => u.Id == lu);
            if (!rows.TryGetValue(lu,out var row)) { result.Add(new(lu,"NotGenerated","AfterLogicUnit",null,[])); continue; }
            var expected = unit is null ? "" : PipelineEvidenceBuilder.Key(sourceFingerprint,unit);
            bool current = expected == row.DescriptionKey && row.Description is not null &&
                row.GenerationKey == PipelineRuleVerifier.GenerationKey(expected,modelVersion);
            result.Add(new(lu,current ? "Current" : "Stale","AfterLogicUnit",current ? row.Description!.Markdown : null,
                current ? row.Rules.Values.ToArray() : []));
        }
        await transaction.CommitAsync(token); return result;
    }
}
