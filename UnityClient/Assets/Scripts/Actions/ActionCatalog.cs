using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbodiedNPC.Actions
{
    /// <summary>Immutable internal registry. B phase intentionally has no scanning or hot loading.</summary>
    public sealed class ActionCatalog
    {
        private readonly ActionRegistration[] registrations;
        private readonly Dictionary<string, ActionRegistration> byActionId;

        public ActionCatalog(IEnumerable<ActionRegistration> registrations)
        {
            this.registrations = (registrations ?? throw new ArgumentNullException(nameof(registrations))).ToArray();
            byActionId = new Dictionary<string, ActionRegistration>(StringComparer.Ordinal);
            foreach (ActionRegistration registration in this.registrations)
            {
                if (registration == null)
                    throw new ArgumentException("Registrations cannot contain null entries.", nameof(registrations));
                string actionId = registration.Descriptor.ActionId;
                if (!byActionId.TryAdd(actionId, registration))
                    throw new ArgumentException($"ACTION_ALREADY_REGISTERED:{actionId}", nameof(registrations));
            }

            Registrations = Array.AsReadOnly(this.registrations);
            AvailableActionIds = Array.AsReadOnly(this.registrations
                .Where(registration => registration.Descriptor.Available)
                .Select(registration => registration.Descriptor.ActionId)
                .ToArray());
        }

        public IReadOnlyList<ActionRegistration> Registrations { get; }
        public IReadOnlyList<string> AvailableActionIds { get; }

        public bool TryGet(string actionId, out ActionRegistration registration) =>
            byActionId.TryGetValue(actionId ?? string.Empty, out registration);
    }

    /// <summary>
    /// Static declarations for existing actions. Their handlers remain the current GoalValidator/PlanExecutor paths.
    /// </summary>
    public static class BuiltInActionCatalog
    {
        public static ActionCatalog Instance { get; } = Create();

        private static ActionCatalog Create()
        {
            EntityTargetBinder entityBinder = new();
            AuthorizedReselectionRecoveryPolicy recoveryPolicy = new();
            return new ActionCatalog(new[]
            {
            new ActionRegistration(new ActionDescriptor(
                "bring_object", "1.0",
                "Pick up or deliver one validated item to a validated destination after an explicit transfer request.",
                new[]
                {
                    new ActionBindingDescriptor("target", ActionBindingKind.EntityRef),
                    new ActionBindingDescriptor("destination", ActionBindingKind.EntityRef),
                },
                new[] { "item", "pick_up", "delivery_destination" }, true, 30f,
                ActionInterruptionPolicy.Cancel, ActionConfirmationPolicy.OnRebind),
                entityBinder, new BringObjectAdmissionPolicy(), new BringObjectPlanFactory(),
                new BringObjectCandidateProvider(), recoveryPolicy, new BringObjectPresentationPolicy(),
                new BringObjectExecutionPreparationPolicy(), reportsTaskFailures: true),
            new ActionRegistration(new ActionDescriptor(
                "move_to_entity", "1.0",
                "Move the NPC near one validated entity without picking it up or delivering it.",
                new[] { new ActionBindingDescriptor("target", ActionBindingKind.EntityRef) },
                new[] { "entity", "reachable_approach" }, true, 30f,
                ActionInterruptionPolicy.Cancel, ActionConfirmationPolicy.OnRebind),
                entityBinder, new MoveToEntityAdmissionPolicy(), new MoveToEntityPlanFactory(),
                new MoveToEntityCandidateProvider(), recoveryPolicy, new MoveToEntityPresentationPolicy(),
                new MoveToEntityExecutionPreparationPolicy()),
            new ActionRegistration(new ActionDescriptor(
                "open_door", "1.0",
                "Walk to and open one validated, closed and unlocked door using Unity-authoritative state.",
                new[] { new ActionBindingDescriptor("target", ActionBindingKind.EntityRef) },
                new[] { "door", "open", "reachable_approach" }, true, 30f,
                ActionInterruptionPolicy.Cancel, ActionConfirmationPolicy.OnRebind),
                entityBinder, new OpenDoorAdmissionPolicy(), new OpenDoorPlanFactory(),
                new OpenDoorCandidateProvider(), recoveryPolicy, new OpenDoorPresentationPolicy(),
                new OpenDoorExecutionPreparationPolicy()),
            new ActionRegistration(new ActionDescriptor(
                "prepare_drink", "1.0", "Prepare one latte, cappuccino or americano for the requesting player at the authorized station.",
                new[] { new ActionBindingDescriptor("drink_type", ActionBindingKind.Enum,
                    enumValues: new[] { "latte", "cappuccino", "americano" }) },
                new[] { "authorized_drink_station", "requesting_player" }, true, 30f,
                ActionInterruptionPolicy.Cancel, ActionConfirmationPolicy.OnRebind),
                new PrepareDrinkBinder(), new PrepareDrinkAdmissionPolicy(), new PrepareDrinkPlanFactory(),
                presentationPolicy: new PrepareDrinkPresentationPolicy(),
                executionPreparationPolicy: new PrepareDrinkPreparationPolicy()),
            new ActionRegistration(new ActionDescriptor(
                "clean_counter", "1.0", "Wipe the single authorized spare counter surface when it needs cleaning.",
                new[] { new ActionBindingDescriptor("target", ActionBindingKind.EntityRef) },
                new[] { "authorized_counter", "needs_cleaning" }, true, 30f,
                ActionInterruptionPolicy.Cancel, ActionConfirmationPolicy.OnRebind),
                new CleanCounterBinder(), new CleanCounterAdmissionPolicy(), new CleanCounterPlanFactory(),
                presentationPolicy: new CleanCounterPresentationPolicy(), executionPreparationPolicy: new CleanCounterPreparationPolicy()),
            });
        }
    }
}
