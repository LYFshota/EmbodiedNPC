using System;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;

namespace EmbodiedNPC.Tests
{
    public sealed class ExecutionBoundaryClosureTests
    {
        [Test]
        public void ConcreteResolvedGoals_ImplementMinimalSharedBoundary()
        {
            Assert.That(typeof(IResolvedActionGoal).IsAssignableFrom(typeof(ResolvedBringObjectGoal)), Is.True);
            Assert.That(typeof(IResolvedActionGoal).IsAssignableFrom(typeof(ResolvedMoveToEntityGoal)), Is.True);
            Assert.That(typeof(IResolvedActionGoal).IsAssignableFrom(typeof(ResolvedOpenDoorGoal)), Is.True);
            Assert.That(new ResolvedBringObjectGoal(null, null, null, null, null, null, 4).ActionId,
                Is.EqualTo("bring_object"));
            Assert.That(new ResolvedMoveToEntityGoal(null, null, default, 5).ActionId,
                Is.EqualTo("move_to_entity"));
            Assert.That(new ResolvedOpenDoorGoal(null, null, 6).ActionId, Is.EqualTo("open_door"));
        }

        [Test]
        public void GoalValidationResult_ExposesUnifiedResolvedGoal()
        {
            PropertyInfo property = typeof(GoalValidationResult).GetProperty(nameof(GoalValidationResult.ResolvedGoal));

            Assert.That(property, Is.Not.Null);
            Assert.That(property.PropertyType, Is.EqualTo(typeof(IResolvedActionGoal)));
        }

        [Test]
        public void BuiltInRegistrations_ComposeExecutionPreparationPolicies()
        {
            Assert.That(BuiltInActionCatalog.Instance.TryGet("bring_object", out ActionRegistration bring), Is.True);
            Assert.That(BuiltInActionCatalog.Instance.TryGet("move_to_entity", out ActionRegistration move), Is.True);
            Assert.That(bring.ExecutionPreparationPolicy, Is.TypeOf<BringObjectExecutionPreparationPolicy>());
            Assert.That(move.ExecutionPreparationPolicy, Is.TypeOf<MoveToEntityExecutionPreparationPolicy>());
            Assert.That(BuiltInActionCatalog.Instance.TryGet("open_door", out ActionRegistration open), Is.True);
            Assert.That(open.ExecutionPreparationPolicy, Is.TypeOf<OpenDoorExecutionPreparationPolicy>());
        }

        [Test]
        public void PlanExecutor_RejectsUnknownAndIncompleteRegistrationsBeforeRuntimeServices()
        {
            PlanExecutor unknownExecutor = new(null, null, null, null, null, null, null, null,
                actionCatalog: new ActionCatalog(Array.Empty<ActionRegistration>()));
            Assert.That(unknownExecutor.TryBegin(new StubResolvedGoal("test_action"), "trace", out string unknown),
                Is.False);
            Assert.That(unknown, Is.EqualTo("ACTION_NOT_REGISTERED"));

            ActionRegistration incomplete = new(Descriptor("test_action"));
            PlanExecutor incompleteExecutor = new(null, null, null, null, null, null, null, null,
                actionCatalog: new ActionCatalog(new[] { incomplete }));
            Assert.That(incompleteExecutor.TryBegin(new StubResolvedGoal("test_action"), "trace", out string missing),
                Is.False);
            Assert.That(missing, Is.EqualTo("ACTION_REGISTRATION_INCOMPLETE"));
        }

        [Test]
        public void SharedExecutionTypes_DoNotExposeConcreteResolvedGoals()
        {
            MethodInfo[] begins = typeof(PlanExecutor).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == nameof(PlanExecutor.TryBegin)).ToArray();
            Assert.That(begins, Has.Length.EqualTo(1));
            Assert.That(begins[0].GetParameters()[0].ParameterType, Is.EqualTo(typeof(IResolvedActionGoal)));

            Type[] forbidden =
                { typeof(ResolvedBringObjectGoal), typeof(ResolvedMoveToEntityGoal), typeof(ResolvedOpenDoorGoal) };
            Assert.That(ExposedTypes(typeof(PlanExecutor)).Intersect(forbidden), Is.Empty);
            Assert.That(ExposedTypes(typeof(PlanActionExecutionContext)).Intersect(forbidden), Is.Empty);
            Assert.That(ExposedTypes(typeof(GoalPlanRunner)).Intersect(forbidden), Is.Empty);
        }

        private static Type[] ExposedTypes(Type type) => type
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(member => member switch
            {
                FieldInfo field => new[] { field.FieldType },
                PropertyInfo property => new[] { property.PropertyType },
                MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType),
                ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
                _ => Array.Empty<Type>(),
            }).ToArray();

        private static ActionDescriptor Descriptor(string actionId) => new(actionId, "1.0", "Test action.",
            Array.Empty<ActionBindingDescriptor>(), Array.Empty<string>(), true, 5f,
            ActionInterruptionPolicy.Cancel, ActionConfirmationPolicy.None);

        private sealed class StubResolvedGoal : IResolvedActionGoal
        {
            public StubResolvedGoal(string actionId) => ActionId = actionId;
            public string ActionId { get; }
            public WorldEntity Actor => null;
            public WorldEntity Target => null;
            public int ValidatedWorldRevision => 0;
            public bool HasProprioceptiveTargetEvidence => false;
        }
    }
}
