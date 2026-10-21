using System;
using System.Collections.Generic;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;

namespace EmbodiedNPC.Memory
{
    public sealed class GoalOutcomeExperienceAdapter
    {
        private readonly string observerId;
        private readonly Action<ExperienceDraft> sink;
        private readonly EntityRegistry entityRegistry;
        private readonly WorldStateService worldState;
        public GoalOutcomeExperienceAdapter(string observerId, Action<ExperienceDraft> sink,
            EntityRegistry entityRegistry = null, WorldStateService worldState = null)
        {
            this.observerId = observerId ?? string.Empty;
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
            this.entityRegistry = entityRegistry;
            this.worldState = worldState;
        }

        public void Handle(GoalOutcome outcome)
        {
            if (outcome.ActorEntityId != observerId) return;
            try { sink(Create(outcome, entityRegistry, worldState)); }
            catch (Exception) { /* Ledger failure must not affect Goal terminal flow. */ }
        }

        public static ExperienceDraft Create(GoalOutcome outcome, EntityRegistry entityRegistry = null,
            WorldStateService worldState = null)
        {
            string eventType = outcome.Status switch
            {
                PlanExecutionStatus.Succeeded => "goal_succeeded",
                PlanExecutionStatus.Failed => "goal_failed",
                PlanExecutionStatus.Cancelled => "goal_cancelled",
                PlanExecutionStatus.TimedOut => "goal_timed_out",
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
            var targets = new List<string>();
            // This action has no model-specified recipient binding. Only its committed effect certifies one.
            bool prepareDrink = outcome.GoalType == "prepare_drink";
            if (!prepareDrink && !string.IsNullOrWhiteSpace(outcome.TargetEntityId)) targets.Add(outcome.TargetEntityId);
            if (!prepareDrink && !string.IsNullOrWhiteSpace(outcome.DestinationEntityId) && !targets.Contains(outcome.DestinationEntityId))
                targets.Add(outcome.DestinationEntityId);
            var payload = new Dictionary<string, object>
            {
                ["goal_type"] = outcome.GoalType,
                ["status"] = outcome.Status.ToString(),
                ["failure_code"] = outcome.FailureCode,
                ["destination_entity_id"] = prepareDrink ? string.Empty : outcome.DestinationEntityId,
                ["proposal_id"] = outcome.ProposalId,
                ["proposal_revision"] = outcome.ProposalRevision,
            };
            IReadOnlyCollection<PersistentParticipantIdentity> participants =
                new List<PersistentParticipantIdentity>(PersistentParticipantIdentityResolver.ResolveParticipants(
                    entityRegistry, outcome.ActorEntityId, targets));
            if (prepareDrink && outcome.Status == PlanExecutionStatus.Succeeded &&
                worldState != null && worldState.isActiveAndEnabled &&
                !string.IsNullOrWhiteSpace(outcome.ProposalId) && !string.IsNullOrWhiteSpace(outcome.TraceId) &&
                worldState.TryGetDrinkReceipt(outcome.ActorEntityId, outcome.ProposalId, outcome.TraceId, out var receipt) &&
                !string.IsNullOrWhiteSpace(receipt.Id) && !string.IsNullOrWhiteSpace(receipt.Player) &&
                receipt.ActorIdentity.IsValid && receipt.PlayerIdentity.IsValid &&
                receipt.WorldRevision > 0 && receipt.WorldRevision <= outcome.WorldRevision &&
                outcome.WorldRevision <= worldState.WorldRevision)
            {
                targets.Add(receipt.Player);
                payload["destination_entity_id"] = receipt.Player;
                payload["drink_receipt_id"] = receipt.Id;
                payload["drink_type"] = receipt.DrinkType;
                participants = new[] { Identity(receipt.Actor, receipt.ActorIdentity), Identity(receipt.Player, receipt.PlayerIdentity) };
            }
            return new ExperienceDraft(outcome.ActorEntityId, eventType, "goal_outcome",
                ClientSessionIdentity.Current, "unity_realtime",
                ExperienceIds.ProtocolId(outcome.OutcomeId, "goal_outcome"), Array.Empty<string>(),
                outcome.ActorEntityId, targets, payload, null, outcome.OccurredAt, outcome.OccurredAt,
                outcome.WorldRevision, null, NullIfBlank(outcome.TraceId), null, null, "EXECUTED_BY_SELF",
                participants);
        }

        private static PersistentParticipantIdentity Identity(string entityId, PersistentIdentityKey key) =>
            new(entityId, key.Namespace, key.IdentityType, key.PersistentId);

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
