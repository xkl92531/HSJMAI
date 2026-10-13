using System;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    public static class StopCauseMonitoringRedisKeyBuilder
    {
        public const int StateTtlSeconds = 60 * 60 * 24;
        public const int SignalMirrorTtlSeconds = 60 * 60 * 24;
        public const int ChangesTtlSeconds = 60 * 60 * 6;
        public const int BundlesTtlSeconds = 60 * 60 * 24;
        public const int SnapshotTtlSeconds = 60 * 60 * 24 * 3;
        public const int RuntimeLeaseTtlSeconds = 60 * 5;

        public static string BuildStationStateKey(long projectId, string stationCode)
        {
            return $"sc:shadow:station:{projectId}:{NormalizeSegment(stationCode)}:state";
        }

        public static string BuildStationSignalsKey(long projectId, string stationCode)
        {
            return $"sc:shadow:station:{projectId}:{NormalizeSegment(stationCode)}:signals";
        }

        public static string BuildStationChangesKey(long projectId, string stationCode)
        {
            return $"sc:shadow:station:{projectId}:{NormalizeSegment(stationCode)}:changes";
        }

        public static string BuildStationBundlesKey(long projectId, string stationCode)
        {
            return $"sc:shadow:station:{projectId}:{NormalizeSegment(stationCode)}:bundles";
        }

        public static string BuildSnapshotKey(long projectId, string stationCode, string snapshotId)
        {
            return $"sc:shadow:station:{projectId}:{NormalizeSegment(stationCode)}:snapshot:{NormalizeSegment(snapshotId)}";
        }

        public static string BuildProjectStationsKey(long projectId)
        {
            return $"sc:monitor:project:{projectId}:stations";
        }

        public static string BuildRuntimeKey(long projectId, string stationCode)
        {
            return $"sc:monitor:station:{projectId}:{NormalizeSegment(stationCode)}:runtime";
        }

        private static string NormalizeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Redis key segment cannot be empty.", nameof(value));
            }

            return value.Trim();
        }
    }
}