using System;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    public enum GameplayEventType
    {
        Unknown,
        SkillSucceeded,
        SkillFailed,
        SkillCancelled,
        SkillTimedOut,
        Manual,
        DoorOpened,
        WeaponDrawn,
        ObjectDropped,
        Speech,
        Attack,
        GoalSucceeded,
        GoalFailed,
        GoalCancelled,
        GoalTimedOut
    }

    /// <summary>Immutable world-event projection. It records facts but never changes World State.</summary>
    public sealed class GameplayEvent
    {
        public GameplayEvent(string eventId, GameplayEventType eventType, string actorEntityId, string targetEntityId,
            string traceId, Vector3 position, int worldRevision, double occurredAt)
        {
            if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId is required.", nameof(eventId));
            EventId = eventId; EventType = eventType; ActorEntityId = actorEntityId ?? string.Empty;
            TargetEntityId = targetEntityId ?? string.Empty; TraceId = traceId ?? string.Empty;
            Position = position; WorldRevision = worldRevision; OccurredAt = occurredAt;
        }

        public string EventId { get; }
        public GameplayEventType EventType { get; }
        public string ActorEntityId { get; }
        public string TargetEntityId { get; }
        public string TraceId { get; }
        public Vector3 Position { get; }
        public int WorldRevision { get; }
        public double OccurredAt { get; }

        public static GameplayEvent FromSkillResult(SkillResult result, Vector3 position, double occurredAt) =>
            new($"evt_skill_{result.EventSequence:D6}", ToEventType(result.Status), result.ActorEntityId,
                result.TargetEntityId, result.TraceId, position, result.WorldRevisionAfter, occurredAt);

        private static GameplayEventType ToEventType(SkillStatus status) => status switch
        {
            SkillStatus.Succeeded => GameplayEventType.SkillSucceeded,
            SkillStatus.Failed => GameplayEventType.SkillFailed,
            SkillStatus.Cancelled => GameplayEventType.SkillCancelled,
            SkillStatus.TimedOut => GameplayEventType.SkillTimedOut,
            _ => GameplayEventType.Unknown,
        };
    }
}
