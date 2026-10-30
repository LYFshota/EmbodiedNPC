using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class R4CBRejectingPresenter : MonoBehaviour, ICognitiveResponsePresenter
    {
        public int Attempts { get; private set; }

        public bool TryPresentCognitiveUtterance(string utterance)
        {
            Attempts++;
            return false;
        }
    }

    public sealed class CafeWorldActionBoundaryTests
    {
        private CognitiveDialogueSubmissionHandler merchant;
        private AgentOrchestrator orchestrator;
        private CognitiveRunDriver driver;
        private CognitiveContextComposer composer;
        private AgentEventReceiver receiver;
        private GoalPlanRunner runner;
        private WorldStateService world;
        private HeldItemState cup;
        private DialogueBubbleManager bubbles;
        private EventInterestProfile passiveWakeProfile;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            yield return new EnterPlayMode();
            merchant = Object.FindObjectsByType<CognitiveDialogueSubmissionHandler>(FindObjectsInactive.Exclude)
                .Single(value => value.GetComponent<WorldEntity>().EntityId == "npc_cafe_01");
            orchestrator = merchant.GetComponent<AgentOrchestrator>();
            driver = merchant.GetComponent<CognitiveRunDriver>();
            composer = merchant.GetComponent<CognitiveContextComposer>();
            receiver = merchant.GetComponent<AgentEventReceiver>();
            runner = merchant.GetComponent<GoalPlanRunner>();
            world = Object.FindAnyObjectByType<WorldStateService>();
            bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            cup = Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Exclude)
                .Single(value => value.EntityId == "cup_01").GetComponent<HeldItemState>();
            float deadline = Time.realtimeSinceStartup + 8f;
            while (orchestrator.HasActiveRun && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False, "Cafe setup run must settle first.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (passiveWakeProfile != null) Object.DestroyImmediate(passiveWakeProfile);
        }

        [UnityTest]
        public IEnumerator PassiveVisibleAction_IsBlockedBeforeWorldHandoffOrCommitment()
        {
            int revision = world.WorldRevision;
            int bubbleCount = bubbles.GetMessages("npc_cafe_01").Count;
            int presentations = 0;
            driver.UtterancePresented += _ => presentations++;
            CognitiveRequest request = StartOwnedEvent(AgentEventTypes.EntityBecameVisible);

            Assert.That(driver.TryConsumeResponse(ActionResponse(request, "cup_01")), Is.False);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(cup.IsHeld, Is.False);
            Assert.That(presentations, Is.Zero);
            Assert.That(bubbles.GetMessages("npc_cafe_01").Count, Is.EqualTo(bubbleCount));
            yield return null;
        }

        [UnityTest]
        public IEnumerator RejectedHandoff_MakesNoCommitmentAndKeepsWorldUnchanged()
        {
            int revision = world.WorldRevision;
            int bubbleCount = bubbles.GetMessages("npc_cafe_01").Count;
            int presentations = 0;
            driver.UtterancePresented += _ => presentations++;
            CognitiveRequest request = StartOwnedEvent(AgentEventTypes.PlayerUtterance);
            LogAssert.Expect(LogType.Warning, new Regex("\\[GoalPlan\\] REJECTED"));

            Assert.That(driver.TryConsumeResponse(ActionResponse(request, "missing_cup")), Is.False);
            Assert.That(orchestrator.LastRun.State, Is.EqualTo(CognitiveRunState.Failed));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(cup.IsHeld, Is.False);
            Assert.That(presentations, Is.Zero);
            Assert.That(bubbles.GetMessages("npc_cafe_01").Count, Is.EqualTo(bubbleCount));
            yield return null;
        }

        [UnityTest]
        public IEnumerator AcceptedAction_PresentationFailure_PreservesWorldOwnershipUntilOutcome()
        {
            WorldEntity cupEntity = cup.GetComponent<WorldEntity>();
            Vector3 toCup = Vector3.ProjectOnPlane(
                cupEntity.GetComponent<EmbodiedNPC.Perception.PerceivableEntity>().VisualAnchor.position -
                merchant.transform.position, Vector3.up);
            merchant.transform.rotation = Quaternion.LookRotation(toCup);
            Physics.SyncTransforms();
            merchant.GetComponent<EmbodiedNPC.Perception.VisionSensor>().ScanNow();
            Assert.That(merchant.GetComponent<EmbodiedNPC.Perception.VisionSensor>().CurrentVisibleEntityIds,
                Does.Contain("cup_01"));
            var presenter = merchant.gameObject.AddComponent<R4CBRejectingPresenter>();
            driver.enabled = false;
            driver.Configure(merchant.GetComponent<WorldEntity>(), orchestrator, composer,
                Object.FindAnyObjectByType<BackendWebSocketClient>(), presenter);
            int revision = world.WorldRevision;
            int bubbleCount = bubbles.GetMessages("npc_cafe_01").Count;
            int presentations = 0;
            driver.UtterancePresented += _ => presentations++;
            CognitiveRequest request = StartOwnedEvent(AgentEventTypes.PlayerUtterance);
            GoalOutcome? outcome = null;
            runner.GoalOutcomePublished += value => outcome = value;
            CognitiveRun run = orchestrator.CurrentRun;
            LogAssert.Expect(LogType.Warning, new Regex("Accepted action utterance was not presented"));

            Assert.That(driver.TryConsumeResponse(ActionResponse(request, "cup_01")), Is.True);
            Assert.That(driver.LastValidatedGoalProposal?.type, Is.EqualTo("bring_object"));
            Assert.That(presenter.Attempts, Is.EqualTo(1));
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(driver.HasPendingActionOutcome, Is.True);
            Assert.That(runner.HasActiveTask, Is.True);
            Assert.That(presentations, Is.Zero);
            Assert.That(bubbles.GetMessages("npc_cafe_01").Count, Is.EqualTo(bubbleCount));

            float deadline = Time.realtimeSinceStartup + 30f;
            while ((!outcome.HasValue || runner.HasActiveTask) && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(outcome.HasValue, Is.True);
            Assert.That(outcome.Value.Status, Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(cup.IsHeld, Is.True);
            Assert.That(cup.HolderEntityId, Is.EqualTo("player_01"));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            Assert.That(orchestrator.LastRun, Is.SameAs(run));
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(presentations, Is.Zero);
            Assert.That(bubbles.GetMessages("npc_cafe_01").Count, Is.EqualTo(bubbleCount));
        }

        private CognitiveRequest StartOwnedEvent(string eventType)
        {
            driver.enabled = false;
            if (eventType == AgentEventTypes.EntityBecameVisible)
            {
                // Exercise the shared authority guard even though CafeV2 now records visual presence only.
                // This test-only profile must never change the authored asset's Record policy.
                passiveWakeProfile = Object.Instantiate(receiver.InterestProfile);
                passiveWakeProfile.Configure(passiveWakeProfile.FallbackDecision,
                    passiveWakeProfile.Rules.Select(rule => rule.EventType == eventType
                        ? new EventInterestRule(eventType, EventInterestDecision.Wake, "", EventInterestRelevance.TargetSelf)
                        : rule).ToArray());
                var fields = new UnityEditor.SerializedObject(receiver);
                fields.FindProperty("interestProfile").objectReferenceValue = passiveWakeProfile;
                fields.ApplyModifiedPropertiesWithoutUndo();
            }
            var payload = eventType == AgentEventTypes.PlayerUtterance
                ? new Dictionary<string, string> { [AgentEventPayloadKeys.Text] = "把这个杯子拿给我" }
                : null;
            var agentEvent = new AgentEvent($"r4cb-{Guid.NewGuid():N}", eventType,
                "player_01", "npc_cafe_01", string.Empty, payload,
                Time.realtimeSinceStartupAsDouble, world.WorldRevision);
            Assert.That(receiver.Receive(agentEvent), Is.True);
            Assert.That(receiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Wake));
            CognitiveRequest request = composer.LastRequest;
            Assert.That(request, Is.Not.Null);
            Assert.That(request.context.current_event.event_id, Is.EqualTo(agentEvent.EventId));
            driver.enabled = true;
            Assert.That(driver.TryOwnRequest(request), Is.True);
            return request;
        }

        private static CognitiveResponse ActionResponse(CognitiveRequest request, string target) => new()
        {
            message_type = "cognitive_response",
            schema_version = "1.0",
            request_id = request.request_id,
            trace_id = request.trace_id,
            run_id = request.run.run_id,
            agent_entity_id = request.run.agent_entity_id,
            decision = new CognitiveDecision
            {
                decision_kind = CognitiveDecisionKinds.ActionProposal,
                utterance = "好的，我去拿杯子。",
                emotion = "neutral",
                gesture = "none",
                look_at = "player_01",
                action_proposal = new CognitiveActionProposal
                {
                    action_id = "bring_object",
                    bindings = new Dictionary<string, string>
                    {
                        ["target"] = target,
                        ["destination"] = "player_01",
                    },
                },
            },
            validated_goal = new GoalProposal
            {
                type = "bring_object", target_entity_id = target,
                destination_entity_id = "player_01",
            },
        };
    }
}
