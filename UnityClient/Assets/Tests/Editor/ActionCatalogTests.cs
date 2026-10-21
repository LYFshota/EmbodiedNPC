using System;
using System.Linq;
using EmbodiedNPC.Actions;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionCatalogTests
    {
        [Test]
        public void BringObjectRegistration_ExposesExistingActionMetadata()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration registration),
                Is.True);
            Assert.That(registration.Descriptor.BindingSchema.Count, Is.EqualTo(2));
            Assert.That(registration.Descriptor.TargetRequirements,
                Does.Contain("delivery_destination"));
        }

        [Test]
        public void MoveToEntityRegistration_ExposesExistingActionMetadata()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration registration),
                Is.True);
            Assert.That(registration.Descriptor.BindingSchema.Count, Is.EqualTo(1));
            Assert.That(registration.Descriptor.TargetRequirements,
                Does.Contain("reachable_approach"));
        }

        [Test]
        public void OpenDoorRegistration_ExposesBoundedDoorMetadataAndAllPolicies()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("open_door", out ActionRegistration registration),
                Is.True);
            Assert.That(registration.Descriptor.BindingSchema.Select(binding => binding.Name),
                Is.EqualTo(new[] { "target" }));
            Assert.That(registration.Descriptor.TargetRequirements,
                Is.EqualTo(new[] { "door", "open", "reachable_approach" }));
            Assert.That(registration.Binder, Is.TypeOf<EntityTargetBinder>());
            Assert.That(registration.AdmissionPolicy, Is.TypeOf<OpenDoorAdmissionPolicy>());
            Assert.That(registration.PlanFactory, Is.TypeOf<OpenDoorPlanFactory>());
            Assert.That(registration.CandidateProvider, Is.TypeOf<OpenDoorCandidateProvider>());
            Assert.That(registration.RecoveryPolicy, Is.TypeOf<AuthorizedReselectionRecoveryPolicy>());
            Assert.That(registration.PresentationPolicy, Is.TypeOf<OpenDoorPresentationPolicy>());
            Assert.That(registration.ExecutionPreparationPolicy,
                Is.TypeOf<OpenDoorExecutionPreparationPolicy>());
        }

        [Test]
        public void UnknownAction_DoesNotFallBackToNearestRegistration()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_objects", out _), Is.False);
            Assert.That(BuiltInActionCatalog.Instance.TryGet("open_doors", out _), Is.False);
        }

        [Test]
        public void DuplicateRegistration_IsRejected()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration registration),
                Is.True);
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                new ActionCatalog(new[] { registration, registration }));
            Assert.That(exception.Message, Does.Contain("ACTION_ALREADY_REGISTERED:bring_object"));
        }

        [Test]
        public void AvailableActionIds_PreserveRegistrationOrder()
        {
            Assert.That(BuiltInActionCatalog.Instance.AvailableActionIds,
                Is.EqualTo(new[] { "bring_object", "move_to_entity", "open_door", "prepare_drink", "clean_counter" }));
        }
    }
}
