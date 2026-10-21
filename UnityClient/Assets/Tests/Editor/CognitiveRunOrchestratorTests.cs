using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests
{
    public sealed class CognitiveRunOrchestratorTests
    {
        private readonly List<GameObject> objects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject value in objects)
                if (value != null) Object.DestroyImmediate(value);
            objects.Clear();
        }

        [Test]
        public void Trigger_StartsRunningRun()
        {
            AgentOrchestrator orchestrator = CreateOrchestrator("npc_cafe_01");
            CognitiveTrigger trigger = Trigger("trigger_01", "npc_cafe_01");

            Assert.That(orchestrator.TryStartRun(trigger, out CognitiveRun run), Is.True);
            Assert.That(run, Is.SameAs(orchestrator.CurrentRun));
            Assert.That(run, Is.SameAs(orchestrator.LastRun));
            Assert.That(run.RunId, Is.Not.Empty);
            Assert.That(run.AgentEntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(run.TriggerId, Is.EqualTo(trigger.TriggerId));
            Assert.That(run.TriggerType, Is.EqualTo(CognitiveTriggerTypes.Event));
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(run.StartedAt, Is.GreaterThan(0d));
            Assert.That(run.EndedAt, Is.Null);
            Assert.That(orchestrator.HasActiveRun, Is.True);
        }

        [Test]
        public void SecondTriggerWhileRunning_IsRejectedWithoutCreatingAnotherRun()
        {
            AgentOrchestrator orchestrator = CreateOrchestrator("npc_cafe_01");
            Assert.That(orchestrator.TryStartRun(Trigger("trigger_01", "npc_cafe_01"),
                out CognitiveRun first), Is.True);

            Assert.That(orchestrator.TryStartRun(Trigger("trigger_02", "npc_cafe_01"),
                out CognitiveRun rejected), Is.False);
            Assert.That(rejected, Is.Null);
            Assert.That(orchestrator.CurrentRun, Is.SameAs(first));
            Assert.That(orchestrator.LastRun, Is.SameAs(first));
        }

        [Test]
        public void CompleteCurrentRun_AllowsNextTrigger()
        {
            AgentOrchestrator orchestrator = CreateOrchestrator("npc_cafe_01");
            Assert.That(orchestrator.TryStartRun(Trigger("trigger_01", "npc_cafe_01"),
                out CognitiveRun first), Is.True);

            Assert.That(orchestrator.CompleteCurrentRun(), Is.True);
            Assert.That(first.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(first.EndedAt, Is.Not.Null);
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(orchestrator.LastRun, Is.SameAs(first));

            Assert.That(orchestrator.TryStartRun(Trigger("trigger_02", "npc_cafe_01"),
                out CognitiveRun second), Is.True);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.RunId, Is.Not.EqualTo(first.RunId));
            Assert.That(second.State, Is.EqualTo(CognitiveRunState.Running));
            Assert.That(first.State, Is.EqualTo(CognitiveRunState.Completed));
        }

        [Test]
        public void TriggerForDifferentAgent_IsRejected()
        {
            AgentOrchestrator orchestrator = CreateOrchestrator("npc_cafe_01");

            Assert.That(orchestrator.TryStartRun(Trigger("trigger_other", "npc_other"),
                out CognitiveRun run), Is.False);
            Assert.That(run, Is.Null);
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(orchestrator.LastRun, Is.Null);
        }

        [Test]
        public void CancelAndFail_SetTerminalStateAndReleaseActiveSlot()
        {
            AgentOrchestrator orchestrator = CreateOrchestrator("npc_cafe_01");
            Assert.That(orchestrator.TryStartRun(Trigger("trigger_cancel", "npc_cafe_01"),
                out CognitiveRun cancelled), Is.True);
            Assert.That(orchestrator.CancelCurrentRun(), Is.True);
            Assert.That(cancelled.State, Is.EqualTo(CognitiveRunState.Cancelled));
            Assert.That(cancelled.EndedAt, Is.Not.Null);
            Assert.That(orchestrator.CurrentRun, Is.Null);

            Assert.That(orchestrator.TryStartRun(Trigger("trigger_fail", "npc_cafe_01"),
                out CognitiveRun failed), Is.True);
            Assert.That(orchestrator.FailCurrentRun(), Is.True);
            Assert.That(failed.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(failed.EndedAt, Is.Not.Null);
            Assert.That(orchestrator.CurrentRun, Is.Null);
            Assert.That(orchestrator.LastRun, Is.SameAs(failed));
            Assert.That(orchestrator.CompleteCurrentRun(), Is.False);
        }

        private AgentOrchestrator CreateOrchestrator(string entityId)
        {
            GameObject root = new("Agent");
            objects.Add(root);
            WorldEntity entity = root.AddComponent<WorldEntity>();
            entity.Configure(entityId, "character.npc.test", EntityKind.Agent, AgentRole.Npc);
            AgentEventReceiver receiver = root.AddComponent<AgentEventReceiver>();
            receiver.Configure(entity);
            WakeCognitiveTriggerBridge bridge = root.AddComponent<WakeCognitiveTriggerBridge>();
            bridge.Configure(receiver);
            AgentOrchestrator orchestrator = root.AddComponent<AgentOrchestrator>();
            orchestrator.Configure(entity, bridge);
            Assert.That(orchestrator.TryValidateConfiguration(out string error), Is.True, error);
            return orchestrator;
        }

        private static CognitiveTrigger Trigger(string triggerId, string agentEntityId) =>
            new(triggerId, CognitiveTriggerTypes.Event, agentEntityId, "event_01", 1d,
                AgentEventTypes.PlayerEnteredArea);
    }
}
