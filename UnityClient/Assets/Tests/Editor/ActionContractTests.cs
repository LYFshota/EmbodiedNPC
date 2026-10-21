using System;
using EmbodiedNPC.Actions;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ActionContractTests
    {
        [Test]
        public void Registration_ContainsOnlyBoundedDescriptorAtThisPhase()
        {
            ActionDescriptor descriptor = BringDescriptor();
            ActionRegistration registration = new(descriptor);

            Assert.That(registration.Descriptor.ActionId, Is.EqualTo("bring_object"));
            Assert.That(registration.Descriptor.BindingSchema.Count, Is.EqualTo(2));
        }

        [Test]
        public void Descriptor_RejectsDuplicateAndUnboundedBindings()
        {
            ActionBindingDescriptor target = new("target", ActionBindingKind.EntityRef);
            Assert.Throws<ArgumentException>(() => new ActionDescriptor(
                "bring_object", "1.0", "Bring an object.", new[] { target, target },
                new[] { "item" }, true, 30f, ActionInterruptionPolicy.Cancel,
                ActionConfirmationPolicy.OnRebind));
            Assert.Throws<ArgumentException>(() =>
                new ActionBindingDescriptor("callback", ActionBindingKind.String));
            Assert.Throws<ArgumentException>(() =>
                new ActionBindingDescriptor("choice", ActionBindingKind.Enum));
            Assert.Throws<ArgumentException>(() =>
                new ActionBindingDescriptor("count", ActionBindingKind.Number));
        }

        [Test]
        public void Authorization_IsScopedToExactActionAndEntitySet()
        {
            ExecutionAuthorization authorization = new(
                "bring_object", ActionSelectionMode.Any, new[] { "cup_01", "cup_02" },
                ActionConfirmationPolicy.BeforeExecution, 2, 9);

            Assert.That(authorization.Authorizes("bring_object", "cup_02"), Is.True);
            Assert.That(authorization.Authorizes("move_to_entity", "cup_02"), Is.False);
            Assert.That(authorization.Authorizes("bring_object", "book_01"), Is.False);
        }

        [Test]
        public void Authorization_RejectsExactMultiTargetAndDuplicateScope()
        {
            Assert.Throws<ArgumentException>(() => new ExecutionAuthorization(
                "bring_object", ActionSelectionMode.Exact, new[] { "cup_01", "cup_02" },
                ActionConfirmationPolicy.BeforeExecution, 0, 0));
            Assert.Throws<ArgumentException>(() => new ExecutionAuthorization(
                "bring_object", ActionSelectionMode.Any, new[] { "cup_01", "cup_01" },
                ActionConfirmationPolicy.BeforeExecution, 0, 0));
        }

        private static ActionDescriptor BringDescriptor() => new(
            "bring_object", "1.0", "Bring one validated object to a validated destination.",
            new[]
            {
                new ActionBindingDescriptor("target", ActionBindingKind.EntityRef),
                new ActionBindingDescriptor("destination", ActionBindingKind.EntityRef),
            },
            new[] { "item", "pick_up" }, true, 30f, ActionInterruptionPolicy.Cancel,
            ActionConfirmationPolicy.OnRebind);
    }
}
