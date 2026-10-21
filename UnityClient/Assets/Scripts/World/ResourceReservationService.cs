using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.World
{
    public enum ReservationLeaseEventType { Created, Renewed, Released, Expired }

    public readonly struct ReservationLeaseSnapshot
    {
        public ReservationLeaseSnapshot(string reservationId, string targetEntityId, string ownerEntityId,
            string traceId, float createdAt, float expiresAt)
        { ReservationId = reservationId; TargetEntityId = targetEntityId; OwnerEntityId = ownerEntityId; TraceId = traceId; CreatedAt = createdAt; ExpiresAt = expiresAt; }
        public string ReservationId { get; }
        public string TargetEntityId { get; }
        public string OwnerEntityId { get; }
        public string TraceId { get; }
        public float CreatedAt { get; }
        public float ExpiresAt { get; }
        public bool IsExpired => Time.time >= ExpiresAt;
    }

    public readonly struct ReservationLeaseEvent
    {
        public ReservationLeaseEvent(long eventSequence, ReservationLeaseEventType eventType,
            ReservationLeaseSnapshot lease, float occurredAt)
        { EventSequence = eventSequence; EventType = eventType; Lease = lease; OccurredAt = occurredAt; }
        public long EventSequence { get; }
        public ReservationLeaseEventType EventType { get; }
        public ReservationLeaseSnapshot Lease { get; }
        public float OccurredAt { get; }
    }

    /// <summary>Unity 主线程内的确定性资源租约表；业务链只能凭 reservation_id 使用和释放。</summary>
    public sealed class ResourceReservationService : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float leaseDurationSeconds = 30f;
        private readonly Dictionary<string, LeaseRecord> leaseByTarget = new();
        private readonly Dictionary<string, LeaseRecord> leaseById = new();
        private readonly List<ReservationLeaseEvent> eventHistory = new();
        private long nextEventSequence;

        public IReadOnlyList<ReservationLeaseEvent> EventHistory => eventHistory;

        private void Update() { ExpireLeases(Time.time); }

        public bool IsReservedByOther(string entityId, string requesterEntityId)
        {
            ExpireLeases(Time.time);
            return leaseByTarget.TryGetValue(entityId, out LeaseRecord lease) && lease.OwnerEntityId != requesterEntityId;
        }

        public bool TryAcquire(string entityId, string ownerEntityId, string traceId, out ReservationLeaseSnapshot snapshot)
        {
            ExpireLeases(Time.time);
            snapshot = default;
            if (string.IsNullOrWhiteSpace(entityId) || string.IsNullOrWhiteSpace(ownerEntityId) ||
                string.IsNullOrWhiteSpace(traceId) || leaseByTarget.ContainsKey(entityId)) return false;

            string reservationId = $"lease-{Guid.NewGuid():N}";
            LeaseRecord lease = new(reservationId, entityId, ownerEntityId, traceId, Time.time, Time.time + leaseDurationSeconds);
            leaseByTarget.Add(entityId, lease);
            leaseById.Add(reservationId, lease);
            snapshot = lease.ToSnapshot();
            Publish(ReservationLeaseEventType.Created, snapshot);
            return true;
        }

        public bool IsLeaseValid(string reservationId, string ownerEntityId)
        {
            ExpireLeases(Time.time);
            return leaseById.TryGetValue(reservationId, out LeaseRecord lease) &&
                   lease.OwnerEntityId == ownerEntityId && Time.time < lease.ExpiresAt;
        }

        public bool TryRenew(string reservationId, string ownerEntityId, out ReservationLeaseSnapshot snapshot)
        {
            ExpireLeases(Time.time);
            snapshot = default;
            if (!leaseById.TryGetValue(reservationId, out LeaseRecord lease) ||
                lease.OwnerEntityId != ownerEntityId || Time.time >= lease.ExpiresAt) return false;
            lease.ExpiresAt = Time.time + leaseDurationSeconds;
            snapshot = lease.ToSnapshot();
            Publish(ReservationLeaseEventType.Renewed, snapshot);
            return true;
        }

        public bool Release(string reservationId, string ownerEntityId)
        {
            if (!leaseById.TryGetValue(reservationId, out LeaseRecord lease) || lease.OwnerEntityId != ownerEntityId) return false;
            leaseById.Remove(reservationId);
            leaseByTarget.Remove(lease.TargetEntityId);
            Publish(ReservationLeaseEventType.Released, lease.ToSnapshot());
            return true;
        }

        public bool TryGetSnapshot(string reservationId, out ReservationLeaseSnapshot snapshot)
        {
            ExpireLeases(Time.time);
            if (leaseById.TryGetValue(reservationId, out LeaseRecord lease)) { snapshot = lease.ToSnapshot(); return true; }
            snapshot = default;
            return false;
        }

        public void ResetLeases()
        {
            foreach (LeaseRecord lease in new List<LeaseRecord>(leaseById.Values)) Publish(ReservationLeaseEventType.Released, lease.ToSnapshot());
            leaseByTarget.Clear();
            leaseById.Clear();
        }

        private void ExpireLeases(float now)
        {
            foreach (LeaseRecord lease in new List<LeaseRecord>(leaseById.Values))
            {
                if (now < lease.ExpiresAt) continue;
                leaseById.Remove(lease.ReservationId);
                leaseByTarget.Remove(lease.TargetEntityId);
                Publish(ReservationLeaseEventType.Expired, lease.ToSnapshot());
            }
        }

        private void Publish(ReservationLeaseEventType eventType, ReservationLeaseSnapshot snapshot)
        {
            ReservationLeaseEvent leaseEvent = new(++nextEventSequence, eventType, snapshot, Time.time);
            eventHistory.Add(leaseEvent);
            Debug.Log($"[ResourceReservation] {eventType.ToString().ToUpperInvariant()} reservation_id={snapshot.ReservationId}; " +
                      $"target={snapshot.TargetEntityId}; owner={snapshot.OwnerEntityId}; trace_id={snapshot.TraceId}; " +
                      $"expires_at={snapshot.ExpiresAt:0.00}; event_sequence={leaseEvent.EventSequence}", this);
        }

        private sealed class LeaseRecord
        {
            public LeaseRecord(string reservationId, string targetEntityId, string ownerEntityId, string traceId, float createdAt, float expiresAt)
            { ReservationId = reservationId; TargetEntityId = targetEntityId; OwnerEntityId = ownerEntityId; TraceId = traceId; CreatedAt = createdAt; ExpiresAt = expiresAt; }
            public string ReservationId { get; }
            public string TargetEntityId { get; }
            public string OwnerEntityId { get; }
            public string TraceId { get; }
            public float CreatedAt { get; }
            public float ExpiresAt { get; set; }
            public ReservationLeaseSnapshot ToSnapshot() => new(ReservationId, TargetEntityId, OwnerEntityId, TraceId, CreatedAt, ExpiresAt);
        }
    }
}
