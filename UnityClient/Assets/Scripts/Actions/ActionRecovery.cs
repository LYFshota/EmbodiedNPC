using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Actions
{
    /// <summary>
    /// Recovery candidate boundary. Observer-local IDs are supplied by one observer's current perception and
    /// still-valid spatial memory; the registry may only resolve those already authorized stable IDs.
    /// </summary>
    public sealed class ActionCandidateContext
    {
        private readonly HashSet<string> observerLocalEntityIds;

        public ActionCandidateContext(ExecutionAuthorization authorization,
            IEnumerable<string> observerLocalEntityIds, EntityRegistry entityRegistry,
            WorldEntity actor, int deterministicSeed)
        {
            Authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
            this.observerLocalEntityIds = new HashSet<string>(
                observerLocalEntityIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            EntityRegistry = entityRegistry;
            Actor = actor;
            DeterministicSeed = deterministicSeed;
        }

        public ExecutionAuthorization Authorization { get; }
        public EntityRegistry EntityRegistry { get; }
        public WorldEntity Actor { get; }
        public int DeterministicSeed { get; }
        public bool IsObserverLocal(string entityId) => observerLocalEntityIds.Contains(entityId);
    }

    public interface IActionCandidateProvider
    {
        IReadOnlyList<WorldEntity> GetCandidates(ActionDescriptor descriptor, ActionCandidateContext context);
    }

    public enum ActionRecoveryDisposition
    {
        RetryOriginal,
        OfferAlternatives,
        RequireConfirmation,
    }

    public sealed class ActionRecoveryDecision
    {
        public ActionRecoveryDecision(ActionRecoveryDisposition disposition,
            IEnumerable<WorldEntity> candidates, string reasonCode)
        {
            Disposition = disposition;
            Candidates = Array.AsReadOnly((candidates ?? Array.Empty<WorldEntity>()).ToArray());
            ReasonCode = reasonCode ?? string.Empty;
        }

        public ActionRecoveryDisposition Disposition { get; }
        public IReadOnlyList<WorldEntity> Candidates { get; }
        public string ReasonCode { get; }
    }

    public interface IActionRecoveryPolicy
    {
        ActionRecoveryDecision Decide(string failedTargetId, ActionDescriptor descriptor,
            IActionCandidateProvider candidateProvider, ActionCandidateContext context);
    }

    public sealed class BringObjectCandidateProvider : IActionCandidateProvider
    {
        public IReadOnlyList<WorldEntity> GetCandidates(ActionDescriptor descriptor,
            ActionCandidateContext context) => AuthorizedObserverLocalCandidates.Filter(
            descriptor, context, entity =>
                entity.EntityKind == EntityKind.Object &&
                entity.TryGetComponent(out HeldItemState held) && !held.IsHeld &&
                entity.TryGetComponent(out InteractableEntity interactable) && interactable.SupportsPickUp &&
                entity.TryGetComponent(out InteractionAnchorProvider anchors) && anchors.HasPickupAnchors);
    }

    public sealed class MoveToEntityCandidateProvider : IActionCandidateProvider
    {
        public IReadOnlyList<WorldEntity> GetCandidates(ActionDescriptor descriptor,
            ActionCandidateContext context) => AuthorizedObserverLocalCandidates.Filter(
            descriptor, context, entity => entity != context.Actor);
    }

    public sealed class OpenDoorCandidateProvider : IActionCandidateProvider
    {
        public IReadOnlyList<WorldEntity> GetCandidates(ActionDescriptor descriptor,
            ActionCandidateContext context) => AuthorizedObserverLocalCandidates.Filter(
            descriptor, context, entity =>
                entity.EntityKind == EntityKind.Environment &&
                entity.TryGetComponent(out DoorState door) && door.IsConfigured &&
                door.Door == entity && door.ApproachPoint != null && !door.IsOpen && !door.IsLocked);
    }

    /// <summary>Selection only; existing recovery runtime still owns retries, replanning, timeout and termination.</summary>
    public sealed class AuthorizedReselectionRecoveryPolicy : IActionRecoveryPolicy
    {
        public ActionRecoveryDecision Decide(string failedTargetId, ActionDescriptor descriptor,
            IActionCandidateProvider candidateProvider, ActionCandidateContext context)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (candidateProvider == null) throw new ArgumentNullException(nameof(candidateProvider));
            if (context == null) throw new ArgumentNullException(nameof(context));

            IReadOnlyList<WorldEntity> eligible = candidateProvider.GetCandidates(descriptor, context);
            if (context.Authorization.SelectionMode == ActionSelectionMode.Exact)
            {
                bool originalRemainsValid = eligible.Any(candidate =>
                    string.Equals(candidate.EntityId, failedTargetId, StringComparison.Ordinal));
                return new ActionRecoveryDecision(
                    originalRemainsValid ? ActionRecoveryDisposition.RetryOriginal :
                        ActionRecoveryDisposition.RequireConfirmation,
                    Array.Empty<WorldEntity>(),
                    originalRemainsValid ? "EXACT_TARGET_REMAINS_VALID" : "EXACT_TARGET_REQUIRES_CONFIRMATION");
            }

            WorldEntity[] alternatives = eligible.Where(candidate =>
                    !string.Equals(candidate.EntityId, failedTargetId, StringComparison.Ordinal))
                .OrderBy(candidate => candidate.EntityId, StringComparer.Ordinal)
                .ToArray();
            if (alternatives.Length == 0)
                return new ActionRecoveryDecision(ActionRecoveryDisposition.RequireConfirmation,
                    alternatives, "NO_AUTHORIZED_OBSERVER_LOCAL_ALTERNATIVE");

            return context.Authorization.SelectionMode switch
            {
                ActionSelectionMode.Any => Offer(alternatives, "AUTHORIZED_ALTERNATIVES_AVAILABLE"),
                ActionSelectionMode.Nearest => context.Actor == null
                    ? new ActionRecoveryDecision(ActionRecoveryDisposition.RequireConfirmation,
                        Array.Empty<WorldEntity>(), "NEAREST_ACTOR_UNAVAILABLE")
                    : Offer(new[] { alternatives.OrderBy(candidate =>
                            (candidate.transform.position - context.Actor.transform.position).sqrMagnitude)
                        .ThenBy(candidate => candidate.EntityId, StringComparer.Ordinal).First() },
                        "NEAREST_AUTHORIZED_ALTERNATIVE"),
                ActionSelectionMode.Random => Offer(
                    new[] { alternatives[StableIndex(alternatives, context.DeterministicSeed)] },
                    $"SEEDED_AUTHORIZED_ALTERNATIVE:{context.DeterministicSeed}"),
                _ => new ActionRecoveryDecision(ActionRecoveryDisposition.RequireConfirmation,
                    Array.Empty<WorldEntity>(), "SELECTION_MODE_UNSUPPORTED"),
            };
        }

        private static ActionRecoveryDecision Offer(IEnumerable<WorldEntity> candidates, string reasonCode) =>
            new(ActionRecoveryDisposition.OfferAlternatives, candidates, reasonCode);

        private static int StableIndex(IReadOnlyList<WorldEntity> candidates, int seed)
        {
            unchecked
            {
                uint hash = 2166136261u ^ (uint)seed;
                foreach (WorldEntity candidate in candidates)
                {
                    foreach (char value in candidate.EntityId)
                    {
                        hash ^= value;
                        hash *= 16777619u;
                    }
                    hash ^= 0xffu;
                    hash *= 16777619u;
                }
                return (int)(hash % (uint)candidates.Count);
            }
        }
    }

    internal static class AuthorizedObserverLocalCandidates
    {
        public static IReadOnlyList<WorldEntity> Filter(ActionDescriptor descriptor,
            ActionCandidateContext context, Func<WorldEntity, bool> actionEligibility)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (actionEligibility == null) throw new ArgumentNullException(nameof(actionEligibility));
            if (!string.Equals(context.Authorization.ActionId, descriptor.ActionId, StringComparison.Ordinal) ||
                context.EntityRegistry == null || !context.EntityRegistry.IsReady)
                return Array.Empty<WorldEntity>();

            List<WorldEntity> result = new();
            foreach (string entityId in context.Authorization.AuthorizedEntityIds)
            {
                if (!context.IsObserverLocal(entityId) ||
                    !context.EntityRegistry.TryGetEntity(entityId, out WorldEntity entity) ||
                    entity == null || !entity.isActiveAndEnabled || !entity.gameObject.activeInHierarchy ||
                    !actionEligibility(entity)) continue;
                result.Add(entity);
            }
            result.Sort((left, right) => string.CompareOrdinal(left.EntityId, right.EntityId));
            return result.AsReadOnly();
        }
    }
}
