using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;
using HSJM.AI.Core;
using HSJM.AI.Utility;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public class StationShadowStateCacheService : ISelfScopedAutoInject
    {
        public Task SaveStationStateAsync(long projectId, string stationCode, StationShadowStateCacheModel state, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.StateTtlSeconds)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationStateKey(projectId, stationCode);
            var payload = ToHashPayload(state);
            CommonRedisHelper.HashSet(key, payload, ttlSeconds);
            CommonRedisHelper.SetAdd(StopCauseMonitoringRedisKeyBuilder.BuildProjectStationsKey(projectId), ttlSeconds, stationCode);
            return Task.CompletedTask;
        }

        public Task<StationShadowStateCacheModel> GetStationStateAsync(long projectId, string stationCode)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationStateKey(projectId, stationCode);
            var payload = CommonRedisHelper.GetHashAll<string>(key);
            if (payload == null || payload.Count == 0)
            {
                return Task.FromResult<StationShadowStateCacheModel>(null);
            }

            return Task.FromResult(new StationShadowStateCacheModel
            {
                Status = GetValue(payload, "status"),
                StationMode = GetValue(payload, "stationMode"),
                LastSampleTime = ParseDateTime(GetValue(payload, "lastSampleTime")),
                FirstSampleTime = ParseDateTime(GetValue(payload, "firstSampleTime")),
                LastActionTime = ParseDateTime(GetValue(payload, "lastActionTime")),
                LastBundleTime = ParseDateTime(GetValue(payload, "lastBundleTime")),
                IsSilent = ParseBool(GetValue(payload, "isSilent")),
                SilentSeconds = ParseInt(GetValue(payload, "silentSeconds")),
                LastEventId = GetValue(payload, "lastEventId"),
                LastSnapshotId = GetValue(payload, "lastSnapshotId"),
                CoreSignalCount = ParseInt(GetValue(payload, "coreSignalCount")),
                ContextSignalCount = ParseInt(GetValue(payload, "contextSignalCount")),
                RuntimeVersion = GetValue(payload, "runtimeVersion"),
                SamplerNode = GetValue(payload, "samplerNode")
            });
        }

        public Task SaveSignalMirrorAsync(long projectId, string stationCode, StationShadowSignalCacheModel signal, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.SignalMirrorTtlSeconds)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationSignalsKey(projectId, stationCode);
            var field = NormalizeField(signal.SignalKey, nameof(signal.SignalKey));
            CommonRedisHelper.HashSet(key, field, JsonHelper.SerializeObject(signal), ttlSeconds);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 批量写入多个信号镜像（单次 Redis HSET，N 个信号只需 1 次网络往返）。
        /// </summary>
        public Task SaveSignalMirrorsAsync(long projectId, string stationCode, IReadOnlyCollection<StationShadowSignalCacheModel> signals, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.SignalMirrorTtlSeconds)
        {
            if (signals == null || signals.Count == 0)
            {
                return Task.CompletedTask;
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationSignalsKey(projectId, stationCode);
            var payload = new object[signals.Count * 2];
            var i = 0;
            foreach (var signal in signals)
            {
                payload[i++] = NormalizeField(signal.SignalKey, nameof(signal.SignalKey));
                payload[i++] = JsonHelper.SerializeObject(signal);
            }

            CommonRedisHelper.HashSet(key, payload, ttlSeconds);
            CommonRedisHelper.SetAdd(StopCauseMonitoringRedisKeyBuilder.BuildProjectStationsKey(projectId), ttlSeconds, stationCode);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, StationShadowSignalCacheModel>> GetSignalMirrorsAsync(long projectId, string stationCode)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationSignalsKey(projectId, stationCode);
            var payload = CommonRedisHelper.GetHashAll<string>(key) ?? new Dictionary<string, string>();
            var result = payload
                .Where(it => !string.IsNullOrWhiteSpace(it.Key) && !string.IsNullOrWhiteSpace(it.Value))
                .ToDictionary(
                    it => it.Key,
                    it => JsonHelper.DeserializeObject<StationShadowSignalCacheModel>(it.Value),
                    StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(result);
        }

        public Task AppendChangeAsync(long projectId, string stationCode, StationShadowChangeCacheModel change, int keepLatest = 2000, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.ChangesTtlSeconds)
        {
            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationChangesKey(projectId, stationCode);
            CommonRedisHelper.ListPush(key, ttlSeconds, JsonHelper.SerializeObject(change));
            CommonRedisHelper.ListTrim(key, 0, Math.Max(keepLatest - 1, 0), ttlSeconds);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 批量追加变化记录，所有记录推入后只 Trim 一次。
        /// </summary>
        public Task AppendChangesAsync(long projectId, string stationCode, IReadOnlyCollection<StationShadowChangeCacheModel> changes, int keepLatest = 2000, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.ChangesTtlSeconds)
        {
            if (changes == null || changes.Count == 0)
            {
                return Task.CompletedTask;
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationChangesKey(projectId, stationCode);
            foreach (var change in changes)
            {
                CommonRedisHelper.ListPush(key, ttlSeconds, JsonHelper.SerializeObject(change));
            }

            CommonRedisHelper.ListTrim(key, 0, Math.Max(keepLatest - 1, 0), ttlSeconds);
            return Task.CompletedTask;
        }

        public Task<List<StationShadowChangeCacheModel>> GetRecentChangesAsync(long projectId, string stationCode, int take = 200)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationChangesKey(projectId, stationCode);
            var payload = CommonRedisHelper.GetListRange<string>(key, 0, Math.Max(take - 1, 0));
            var result = DeserializeList<StationShadowChangeCacheModel>(payload);
            return Task.FromResult(result);
        }

        public Task AppendBundleAsync(long projectId, string stationCode, StationShadowBundleCacheModel bundle, int keepLatest = 100, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.BundlesTtlSeconds)
        {
            if (bundle == null)
            {
                throw new ArgumentNullException(nameof(bundle));
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationBundlesKey(projectId, stationCode);
            CommonRedisHelper.ListPush(key, ttlSeconds, JsonHelper.SerializeObject(bundle));
            CommonRedisHelper.ListTrim(key, 0, Math.Max(keepLatest - 1, 0), ttlSeconds);
            return Task.CompletedTask;
        }

        public Task<List<StationShadowBundleCacheModel>> GetRecentBundlesAsync(long projectId, string stationCode, int take = 50)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildStationBundlesKey(projectId, stationCode);
            var payload = CommonRedisHelper.GetListRange<string>(key, 0, Math.Max(take - 1, 0));
            var result = DeserializeList<StationShadowBundleCacheModel>(payload);
            return Task.FromResult(result);
        }

        public Task SaveSnapshotAsync(long projectId, string stationCode, StationShadowSnapshotCacheModel snapshot, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.SnapshotTtlSeconds)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildSnapshotKey(projectId, stationCode, snapshot.SnapshotId);
            CommonRedisHelper.StringSet(key, snapshot, ttlSeconds);
            return Task.CompletedTask;
        }

        public Task<StationShadowSnapshotCacheModel> GetSnapshotAsync(long projectId, string stationCode, string snapshotId)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildSnapshotKey(projectId, stationCode, snapshotId);
            var result = CommonRedisHelper.GetString<StationShadowSnapshotCacheModel>(key);
            return Task.FromResult(result);
        }

        public Task SaveRuntimeLeaseAsync(long projectId, string stationCode, StationShadowRuntimeLeaseCacheModel lease, int ttlSeconds = StopCauseMonitoringRedisKeyBuilder.RuntimeLeaseTtlSeconds)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            var key = StopCauseMonitoringRedisKeyBuilder.BuildRuntimeKey(projectId, stationCode);
            CommonRedisHelper.HashSet(key, new object[]
            {
                "nodeName", lease.NodeName ?? string.Empty,
                "startedAt", lease.StartedAt?.ToString("O") ?? string.Empty,
                "lastHeartbeat", lease.LastHeartbeat?.ToString("O") ?? string.Empty,
                "jobSource", lease.JobSource ?? string.Empty,
                "status", lease.Status ?? string.Empty
            }, ttlSeconds);
            return Task.CompletedTask;
        }

        public Task<StationShadowRuntimeLeaseCacheModel> GetRuntimeLeaseAsync(long projectId, string stationCode)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildRuntimeKey(projectId, stationCode);
            var payload = CommonRedisHelper.GetHashAll<string>(key);
            if (payload == null || payload.Count == 0)
            {
                return Task.FromResult<StationShadowRuntimeLeaseCacheModel>(null);
            }

            return Task.FromResult(new StationShadowRuntimeLeaseCacheModel
            {
                NodeName = GetValue(payload, "nodeName"),
                StartedAt = ParseDateTime(GetValue(payload, "startedAt")),
                LastHeartbeat = ParseDateTime(GetValue(payload, "lastHeartbeat")),
                JobSource = GetValue(payload, "jobSource"),
                Status = GetValue(payload, "status")
            });
        }

        public Task<List<string>> GetProjectStationsAsync(long projectId)
        {
            var key = StopCauseMonitoringRedisKeyBuilder.BuildProjectStationsKey(projectId);
            var stations = CommonRedisHelper.SetGet<string>(key)
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(it => it)
                .ToList();
            return Task.FromResult(stations);
        }

        private static object[] ToHashPayload(StationShadowStateCacheModel state)
        {
            return new object[]
            {
                "status", state.Status ?? string.Empty,
                "stationMode", state.StationMode ?? string.Empty,
                "lastSampleTime", state.LastSampleTime?.ToString("O") ?? string.Empty,
                "firstSampleTime", state.FirstSampleTime?.ToString("O") ?? string.Empty,
                "lastActionTime", state.LastActionTime?.ToString("O") ?? string.Empty,
                "lastBundleTime", state.LastBundleTime?.ToString("O") ?? string.Empty,
                "isSilent", state.IsSilent ? "true" : "false",
                "silentSeconds", state.SilentSeconds,
                "lastEventId", state.LastEventId ?? string.Empty,
                "lastSnapshotId", state.LastSnapshotId ?? string.Empty,
                "coreSignalCount", state.CoreSignalCount,
                "contextSignalCount", state.ContextSignalCount,
                "runtimeVersion", state.RuntimeVersion ?? string.Empty,
                "samplerNode", state.SamplerNode ?? string.Empty
            };
        }

        private static string NormalizeField(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Redis hash field cannot be empty.", name);
            }

            return value.Trim();
        }

        private static string GetValue(IReadOnlyDictionary<string, string> payload, string key)
        {
            return payload.TryGetValue(key, out var value) ? value : null;
        }

        private static DateTime? ParseDateTime(string value)
        {
            return DateTime.TryParse(value, out var result) ? result : null;
        }

        private static int ParseInt(string value)
        {
            return int.TryParse(value, out var result) ? result : 0;
        }

        private static bool ParseBool(string value)
        {
            if (bool.TryParse(value, out var result))
            {
                return result;
            }

            // csRedis 对 bool 值序列化为 "1"/"0"，bool.TryParse 不认——必须显式兼容，
            // 否则 IsSilent 状态永远读回 false，静默判定每轮重复触发"转入静默"（事件风暴）。
            return string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
        }

        private static List<T> DeserializeList<T>(IEnumerable<string> payload)
        {
            return (payload ?? Enumerable.Empty<string>())
                .Where(it => !string.IsNullOrWhiteSpace(it))
                .Select(it => JsonHelper.DeserializeObject<T>(it))
                .Where(it => it != null)
                .ToList();
        }

        // ── 未知 Bundle 签名追踪（诊断时模板未命中 → 累计计数 → 触发自动重建）──

        private const int UnknownSignatureTtlSeconds = 60 * 30; // 30分钟内累计

        private static string BuildUnknownSignatureKey(long projectId, string stationCode)
        {
            return $"sc:shadow:station:{projectId}:{stationCode.Trim()}:unknown_sig";
        }

        /// <summary>
        /// 累计未知签名计数。如果签名与上次不同则重置计数（说明模式在变化，不算"连续"）。
        /// 返回当前累计次数。
        /// </summary>
        public int IncrementUnknownSignatureCount(long projectId, string stationCode, string bundleSignature)
        {
            var key = BuildUnknownSignatureKey(projectId, stationCode);
            var existing = CommonRedisHelper.GetString(key);

            int count = 1;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                // 格式: "count|signature"
                var parts = existing.Split('|', 2);
                if (parts.Length == 2 && string.Equals(parts[1], bundleSignature, StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(parts[0], out count);
                    count++;
                }
            }

            CommonRedisHelper.StringSet(key, $"{count}|{bundleSignature}", UnknownSignatureTtlSeconds);
            return count;
        }

        /// <summary>
        /// 模板命中时清除未知签名计数。
        /// </summary>
        public void ResetUnknownSignatureCount(long projectId, string stationCode)
        {
            var key = BuildUnknownSignatureKey(projectId, stationCode);
            CommonRedisHelper.StringSet(key, "", 1); // TTL 1秒自动过期
        }
    }
}