using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    public readonly struct SpatialMemoryDiagnostics
    {
        public SpatialMemoryDiagnostics(string reason, int affectedCount, int activeCount, double timestamp)
        {
            Reason = reason;
            AffectedCount = affectedCount;
            ActiveCount = activeCount;
            Timestamp = timestamp;
        }

        public string Reason { get; }
        public int AffectedCount { get; }
        public int ActiveCount { get; }
        public double Timestamp { get; }
    }

    /// <summary>A copied visual fact, never a live reference to an unseen scene object.</summary>
    public sealed class SpatialMemoryRecord
    {
        internal SpatialMemoryRecord(string entityId, Vector3 position, double seenAt, int revision,
            double expiresAt, IReadOnlyDictionary<string, string> fields)
        {
            EntityId = entityId;
            LastKnownPosition = position;
            LastSeenAt = seenAt;
            LastWorldRevision = revision;
            ExpiresAt = expiresAt;
            var copiedFields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in SemanticKeys)
                if (fields != null && fields.TryGetValue(key, out string value)) copiedFields[key] = value;
            SemanticFields = new ReadOnlyDictionary<string, string>(copiedFields);
        }

        private static readonly string[] SemanticKeys =
            { "entity_kind", "agent_role", "display_name", "aliases", "semantic_tags", "capabilities" };

        public string EntityId { get; }
        public Vector3 LastKnownPosition { get; }
        public double LastSeenAt { get; }
        public int LastWorldRevision { get; }
        public double ExpiresAt { get; }
        public IReadOnlyDictionary<string, string> SemanticFields { get; }
        public bool IsValidAt(double now) => !double.IsNaN(now) && now >= LastSeenAt && now < ExpiresAt;
        public float ConfidenceAt(double now) => IsValidAt(now)
            ? Mathf.Clamp01((float)((ExpiresAt - now) / (ExpiresAt - LastSeenAt))) : 0f;
    }

    /// <summary>
    /// Bounded, per-observer short-term spatial evidence. Only successful visual scans may Observe;
    /// losing sight does not erase the last known position, and reading cannot refresh its age.
    /// </summary>
    public sealed class SpatialWorkingMemory
    {
        private readonly Dictionary<string, SpatialMemoryRecord> records = new(StringComparer.Ordinal);
        private readonly List<string> expiredIds = new();
        private readonly double lifetimeSeconds;
        private readonly int capacity;
        private SpatialMemoryDiagnostics diagnostics;

        public SpatialWorkingMemory(double lifetimeSeconds = 120d, int capacity = 64)
        {
            if (double.IsNaN(lifetimeSeconds) || double.IsInfinity(lifetimeSeconds) || lifetimeSeconds <= 0d)
                throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.lifetimeSeconds = lifetimeSeconds;
            this.capacity = capacity;
        }

        public int Count => records.Count;
        public SpatialMemoryDiagnostics Diagnostics => diagnostics;

        public int ActiveCount(double now)
        {
            if (!IsFinite(now)) return 0;
            PruneExpired(now);
            return records.Count;
        }

        public void Observe(string entityId, Vector3 position, double observedAt, int worldRevision,
            IReadOnlyDictionary<string, string> semanticFields)
        {
            if (string.IsNullOrWhiteSpace(entityId) || !IsFinite(observedAt) || worldRevision < 0 ||
                !IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z)) return;
            if (records.TryGetValue(entityId, out SpatialMemoryRecord previous) && observedAt < previous.LastSeenAt) return;
            PruneExpired(observedAt);
            if (!records.ContainsKey(entityId) && records.Count >= capacity) EvictOldest();
            records[entityId] = new SpatialMemoryRecord(entityId, position, observedAt, worldRevision,
                observedAt + lifetimeSeconds, semanticFields);
            diagnostics = new SpatialMemoryDiagnostics("observed", 1, records.Count, observedAt);
        }

        public bool TryGet(string entityId, double now, out SpatialMemoryRecord record)
        {
            record = null;
            if (string.IsNullOrWhiteSpace(entityId) || !IsFinite(now)) return false;
            if (!records.TryGetValue(entityId, out SpatialMemoryRecord candidate)) return false;
            if (!candidate.IsValidAt(now))
            {
                if (now >= candidate.ExpiresAt) records.Remove(entityId);
                return false;
            }
            record = candidate;
            return true;
        }

        public bool Invalidate(string entityId)
        {
            bool removed = !string.IsNullOrWhiteSpace(entityId) && records.Remove(entityId);
            if (removed) diagnostics = new SpatialMemoryDiagnostics("invalidated", 1, records.Count, double.NaN);
            return removed;
        }

        public int Clear(string reason, double now)
        {
            int count = records.Count;
            records.Clear();
            diagnostics = new SpatialMemoryDiagnostics(
                string.IsNullOrWhiteSpace(reason) ? "explicit_reset" : reason,
                count,
                0,
                now);
            return count;
        }

        public void RecordLifecycle(string reason, double now)
        {
            diagnostics = new SpatialMemoryDiagnostics(reason, 0, ActiveCount(now), now);
        }

        public IReadOnlyList<SpatialMemoryRecord> GetRecords(double now)
        {
            if (!IsFinite(now)) return Array.Empty<SpatialMemoryRecord>();
            PruneExpired(now);
            var result = new List<SpatialMemoryRecord>();
            foreach (SpatialMemoryRecord record in records.Values)
                if (record.IsValidAt(now)) result.Add(record);
            result.Sort((left, right) =>
            {
                int freshness = right.LastSeenAt.CompareTo(left.LastSeenAt);
                return freshness != 0 ? freshness : string.CompareOrdinal(left.EntityId, right.EntityId);
            });
            return result.AsReadOnly();
        }

        private void PruneExpired(double now)
        {
            expiredIds.Clear();
            foreach (SpatialMemoryRecord record in records.Values)
                if (now >= record.ExpiresAt) expiredIds.Add(record.EntityId);
            foreach (string entityId in expiredIds) records.Remove(entityId);
            if (expiredIds.Count > 0)
                diagnostics = new SpatialMemoryDiagnostics("ttl_expired", expiredIds.Count, records.Count, now);
        }

        private void EvictOldest()
        {
            SpatialMemoryRecord oldest = null;
            foreach (SpatialMemoryRecord candidate in records.Values)
                if (oldest == null || candidate.LastSeenAt < oldest.LastSeenAt ||
                    (candidate.LastSeenAt == oldest.LastSeenAt &&
                     string.CompareOrdinal(candidate.EntityId, oldest.EntityId) < 0)) oldest = candidate;
            if (oldest != null) records.Remove(oldest.EntityId);
            if (oldest != null)
                diagnostics = new SpatialMemoryDiagnostics("capacity_evicted", 1, records.Count, oldest.LastSeenAt);
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
