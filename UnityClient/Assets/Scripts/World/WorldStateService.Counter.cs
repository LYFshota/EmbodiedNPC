using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;
using UnityEngine;

namespace EmbodiedNPC.World
{
    public enum CounterCleanliness { Clean, NeedsCleaning }
    public sealed class CounterConditionChange
    {
        internal CounterConditionChange(WorldStateService owner, CounterCleaningStation station, int version, int revision, string reason)
        { Owner = owner; Station = station; Version = version; WorldRevision = revision; Reason = reason; Token = Guid.NewGuid().ToString("N"); }
        public WorldStateService Owner { get; }
        public CounterCleaningStation Station { get; }
        public int Version { get; }
        public int WorldRevision { get; }
        public string Reason { get; }
        public string Token { get; }
    }
    public readonly struct CounterCleaningReceipt
    {
        internal CounterCleaningReceipt(string id, string target, int condition, int revision)
        { Id = id; Target = target; ConditionVersion = condition; WorldRevision = revision; }
        public string Id { get; }
        public string Target { get; }
        public int ConditionVersion { get; }
        public int WorldRevision { get; }
    }
    public sealed partial class WorldStateService
    {
        private readonly Dictionary<string, CounterConditionChange> dirtyCounters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> counterVersions = new(StringComparer.Ordinal);
        private readonly Dictionary<(string actor, string proposal, string trace), CounterCleaningReceipt> cleaningReceipts = new();
        public event Action<CounterConditionChange> CounterNeedsCleaning;
        public CounterCleanliness GetCounterState(string id) => id != null && dirtyCounters.ContainsKey(id) ? CounterCleanliness.NeedsCleaning : CounterCleanliness.Clean;
        public int GetCounterConditionRevision(string id) => id != null && counterVersions.TryGetValue(id, out var v) ? v : 0;
        public bool IsCurrentCounterChange(CounterConditionChange change) => change != null && change.Owner == this &&
            change.Station != null && dirtyCounters.TryGetValue(change.Station.Entity.EntityId, out var current) && ReferenceEquals(current, change);
        public bool TryMarkCounterNeedsCleaning(EntityRegistry registry, CounterCleaningStation station, string reason)
        {
            if (!isActiveAndEnabled || station == null || station.World != this || !station.IsAvailable ||
                !DrinkTypes.IsCurrent(registry, station.Entity) || string.IsNullOrWhiteSpace(reason) || reason.Length > 120 ||
                GetCounterState(station.Entity.EntityId) != CounterCleanliness.Clean) return false;
            string id = station.Entity.EntityId;
            int version = checked(GetCounterConditionRevision(id) + 1);
            counterVersions[id] = version;
            var change = new CounterConditionChange(this, station, version, worldRevision + 1, reason);
            dirtyCounters.Add(id, change);
            Commit("counter-condition:" + change.Token, change.Token, "CounterNeedsCleaning", null, station.Entity, null, SkillStatus.Succeeded, "");
            if (CounterNeedsCleaning != null) foreach (Action<CounterConditionChange> observer in CounterNeedsCleaning.GetInvocationList())
                try { observer(change); } catch (Exception e) { Debug.LogError("[WorldState] Counter observer failed: " + e.GetType().Name, this); }
            return true;
        }
        public bool TryGetCounterCleaningReceipt(string actor, string proposal, string trace, out CounterCleaningReceipt receipt) =>
            cleaningReceipts.TryGetValue((actor, proposal, trace), out receipt);
        internal SkillResult TryCommitCounterCleaning(string proposal, string trace, EntityRegistry registry,
            WorldEntity actor, WorldEntity target, int expectedCondition, ResourceReservationService reservations, string leaseId)
        {
            string skill = "clean_counter:" + proposal;
            if (actor != null && TryGetCounterCleaningReceipt(actor.EntityId, proposal, trace, out var existing))
                return RecordAttempt(skill, trace, "CleanCounter", actor, target, null,
                    target != null && existing.Target == target.EntityId && existing.ConditionVersion == expectedCondition ? SkillStatus.Succeeded : SkillStatus.Failed,
                    "COUNTER_ALREADY_COMMITTED");
            if (!isActiveAndEnabled || string.IsNullOrEmpty(proposal) || string.IsNullOrEmpty(trace) ||
                !CleanCounterRules.ValidateRuntime(registry, actor, target, expectedCondition) || reservations == null || !reservations.isActiveAndEnabled ||
                !reservations.TryGetSnapshot(leaseId, out var lease) || lease.OwnerEntityId != actor.EntityId ||
                lease.TargetEntityId != target.EntityId || lease.TraceId != trace || lease.IsExpired ||
                target.GetComponent<CounterCleaningStation>().World != this ||
                Vector3.Distance(actor.transform.position, target.GetComponent<CounterCleaningStation>().ApproachPoint.position) > .5f)
                return RecordAttempt(skill, trace, "CleanCounter", actor, target, null, SkillStatus.Failed, "COUNTER_COMMIT_INVALID");
            if(!TaskEffectAllowed(actor.EntityId,proposal,trace,"clean_counter",target.EntityId,null,null,expectedCondition))
                return RecordAttempt(skill,trace,"CleanCounter",actor,target,null,SkillStatus.Failed,"TASK_GUARD_EXPIRED");
            var receipt = new CounterCleaningReceipt("clean_" + Guid.NewGuid().ToString("N"), target.EntityId, expectedCondition, worldRevision + 1);
            dirtyCounters.Remove(target.EntityId);
            cleaningReceipts.Add((actor.EntityId, proposal, trace), receipt);
            return Commit(skill, trace, "CleanCounter", actor, target, null, SkillStatus.Succeeded, "");
        }
    }
}
