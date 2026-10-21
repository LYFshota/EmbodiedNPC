using EmbodiedNPC.Actions;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Planning;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionAdmissionBoundaryTests
    {
        [Test]
        public void BuiltInRegistrations_ComposeSharedBinderAndSpecificAdmissionPolicies()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration bring), Is.True);
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);

            Assert.That(bring.Binder, Is.TypeOf<EntityTargetBinder>());
            Assert.That(move.Binder, Is.SameAs(bring.Binder));
            Assert.That(bring.AdmissionPolicy, Is.TypeOf<BringObjectAdmissionPolicy>());
            Assert.That(move.AdmissionPolicy, Is.TypeOf<MoveToEntityAdmissionPolicy>());
        }

        [Test]
        public void MoveBinder_RejectsDestinationNotDeclaredByItsDescriptor()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);
            ActionBindingResult result = move.Binder.Bind(new GoalProposal
            {
                type = "move_to_entity",
                target_entity_id = "book_01",
                destination_entity_id = "player_01",
            }, move.Descriptor, new ActionAdmissionContext(null, null, null, null, null));

            Assert.That(result.IsBound, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(GoalValidationFailureCode.InvalidEntityId));
        }

        [Test]
        public void BringBinder_RejectsMissingDestinationBeforeWorldLookup()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration bring), Is.True);
            ActionBindingResult result = bring.Binder.Bind(new GoalProposal
            {
                type = "bring_object",
                target_entity_id = "cup_01",
                destination_entity_id = null,
            }, bring.Descriptor, new ActionAdmissionContext(null, null, null, null, null));

            Assert.That(result.IsBound, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(GoalValidationFailureCode.InvalidEntityId));
        }

        [Test]
        public void Binder_RejectsInvalidTargetWithoutEnumeratingCandidates()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);
            ActionBindingResult result = move.Binder.Bind(new GoalProposal
            {
                type = "move_to_entity",
                target_entity_id = "book_01_extra!",
            }, move.Descriptor, new ActionAdmissionContext(null, null, null, null, null));

            Assert.That(result.IsBound, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo(GoalValidationFailureCode.InvalidEntityId));
        }
    }
}
