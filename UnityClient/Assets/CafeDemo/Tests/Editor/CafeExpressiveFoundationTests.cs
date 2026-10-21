using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeExpressiveFoundationTests
    {
        private WorldEntity merchant, customer, player;
        private DialogueController dialogue;
        [UnitySetUp] public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();
            yield return new EnterPlayMode();
            AnimationSceneTestIsolation.Ready();
            var entities = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
            merchant = entities.Single(e => e.EntityId == "npc_cafe_01");
            customer = entities.Single(e => e.EntityId == "npc_cafe_regular_01");
            player = entities.Single(e => e.EntityId == "player_01");
            dialogue = Object.FindAnyObjectByType<DialogueController>();
            float until = Time.realtimeSinceStartup + 10;
            while ((merchant.GetComponent<AgentOrchestrator>().HasActiveRun || customer.GetComponent<AgentOrchestrator>().HasActiveRun) &&
                   Time.realtimeSinceStartup < until) yield return null;
            Assert.That(merchant.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
            Assert.That(customer.GetComponent<AgentOrchestrator>().HasActiveRun, Is.False);
            merchant.GetComponent<ExpressiveController>().Cleanup(); customer.GetComponent<ExpressiveController>().Cleanup();
        }
        [UnityTearDown] public IEnumerator TearDown() { if (Application.isPlaying) yield return new ExitPlayMode(); }

        [UnityTest] public IEnumerator RealSceneOwners_SpeechTimerBubbleRetentionAndDisableAreIndependent()
        {
            var a = merchant.GetComponent<ExpressiveController>(); var b = customer.GetComponent<ExpressiveController>();
            Assert.That(a.MotionChannels, Is.Not.SameAs(b.MotionChannels)); Assert.That(a.Speech, Is.Not.SameAs(b.Speech));
            Assert.That(customer.GetComponent<GoalPlanRunner>(), Is.Null); Assert.That(customer.GetComponent<NavMeshAgent>(), Is.Null);
            Assert.That(customer.GetComponent<CognitiveActionHandoff>(), Is.Null);
            Assert.That(merchant.GetComponent<GestureController>().CanPlay("wave"), Is.True);
            Assert.That(customer.GetComponent<GestureController>().CanPlay("nod"), Is.True);
            var bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            var rootRotation = merchant.transform.rotation;
            var response = Decision(); var context = Context(merchant);
            int starts = 0, ends = 0; a.Speech.SpeechStarted += _ => starts++; a.Speech.SpeechEnded += _ => ends++;
            Assert.That(merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(response, context), Is.True);
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Talking)); Assert.That(a.ExpressionHint, Is.EqualTo("happy"));
            Assert.That(a.AttentionHint, Is.EqualTo("player_01")); Assert.That(b.State, Is.EqualTo(DialoguePresentationState.Idle));
            Assert.That(a.MotionChannels.ActiveGestureId, Is.Not.Zero);
            a.Speech.Tick(Time.unscaledTimeAsDouble + 7);
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Idle));
            Assert.That(bubbles.GetMessages(merchant.EntityId).Any(m => m.Text == response.utterance), Is.True);
            Assert.That(starts, Is.EqualTo(1)); Assert.That(ends, Is.EqualTo(1));
            Assert.That(merchant.transform.rotation, Is.EqualTo(rootRotation));
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(response, context);
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().enabled = false;
            Assert.That(ends, Is.EqualTo(2)); Assert.That(a.Speech.IsSpeaking, Is.False);
            a.Speech.BeginPlayback("disable NPC"); merchant.gameObject.SetActive(false);
            Assert.That(ends, Is.EqualTo(3)); Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Idle));
            yield return null;
        }
        [UnityTest] public IEnumerator DialogueSessionSwitchHideAndEmergency_ClearOnlyOwningNpc()
        {
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = merchant.transform.position + Vector3.forward; cc.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            var a = merchant.GetComponent<ExpressiveController>(); var b = customer.GetComponent<ExpressiveController>();
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Listening));
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(Decision(), Context(merchant));
            customer.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(Decision(), Context(customer));
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Talking)); Assert.That(b.State, Is.EqualTo(DialoguePresentationState.Talking));
            dialogue.HideDialoguePresentation();
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Idle)); Assert.That(b.Speech.IsSpeaking, Is.True);
            dialogue.ShowDialoguePresentation();
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(Decision(), Context(merchant));
            dialogue.CancelForEmergency(); yield return null;
            Assert.That(a.Speech.IsSpeaking, Is.False); Assert.That(a.ExpressionHint, Is.EqualTo("neutral"));
            dialogue.ClearSession(); Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Idle));
        }
        [UnityTest] public IEnumerator AttentionRemovalAndAcceptedActionMetadata_PreserveWorldOutcomeAuthority()
        {
            var a = merchant.GetComponent<ExpressiveController>();
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(Decision(), Context(merchant));
            player.GetComponent<WorldEntity>().enabled = false; yield return null;
            Assert.That(a.AttentionHint, Is.Null); player.GetComponent<WorldEntity>().enabled = true; a.Cleanup();
            var cup = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(e => e.EntityId == "cup_01");
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(cup.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            Physics.SyncTransforms(); merchant.GetComponent<VisionSensor>().ScanNow();
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = false;
            var world = Object.FindAnyObjectByType<WorldStateService>(); int revision = world.WorldRevision;
            var ev = new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerUtterance, "player_01", merchant.EntityId, "",
                new Dictionary<string,string> { [AgentEventPayloadKeys.Text] = "把这个杯子拿给我" }, Time.realtimeSinceStartupAsDouble, revision);
            Assert.That(merchant.GetComponent<AgentEventReceiver>().Receive(ev), Is.True);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            driver.enabled = true; Assert.That(driver.TryOwnRequest(request), Is.True);
            var response = new CognitiveResponse { message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = merchant.EntityId, decision = Decision(),
                validated_goal = new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" } };
            response.decision.decision_kind = CognitiveDecisionKinds.ActionProposal;
            // Wave is available: accepted world actions must still deny expressive gestures.
            Assert.That(merchant.GetComponent<GestureController>().CanPlay("wave"), Is.True);
            response.decision.gesture = "wave";
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                bindings = new Dictionary<string,string> { ["target"]="cup_01", ["destination"]="player_01" } };
            var run = merchant.GetComponent<AgentOrchestrator>().CurrentRun;
            Assert.That(driver.TryConsumeResponse(response), Is.True);
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.WaitingActionResult));
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Talking)); Assert.That(a.ExpressionHint, Is.EqualTo("happy"));
            Assert.That(a.PresentationContext.RequestId, Is.EqualTo(request.request_id));
            Assert.That(a.MotionChannels.ActiveGestureId, Is.Zero);
            a.Speech.Cancel(); Assert.That(driver.HasPendingActionOutcome, Is.True);
            float until = Time.realtimeSinceStartup + 30;
            while (driver.HasPendingActionOutcome && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(run.State, Is.EqualTo(CognitiveRunState.Completed));
            Assert.That(cup.GetComponent<HeldItemState>().HolderEntityId, Is.EqualTo("player_01"));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
        }
        [UnityTest] public IEnumerator NaturalTimerSessionSwitchAndMotionDisable_HaveNoResidualState()
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                Assert.That(root.GetComponentsInChildren<Component>(true), Has.None.Null, "No missing scripts in Cafe");
            var a = merchant.GetComponent<ExpressiveController>(); var b = customer.GetComponent<ExpressiveController>();
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false;
            player.transform.position = merchant.transform.position + Vector3.forward; cc.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            var shortText = Decision(); shortText.utterance = "Hi";
            Assert.That(merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(shortText, Context(merchant)), Is.True);
            float until = Time.unscaledTime + 2;
            while (a.Speech.IsSpeaking && Time.unscaledTime < until) yield return null;
            Assert.That(a.Speech.IsSpeaking, Is.False);
            Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Listening));
            merchant.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(Decision(), Context(merchant));
            cc.enabled = false; player.transform.position = customer.transform.position + Vector3.forward; cc.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(a.Speech.IsSpeaking, Is.False); Assert.That(a.State, Is.EqualTo(DialoguePresentationState.Idle));
            Assert.That(b.State, Is.EqualTo(DialoguePresentationState.Listening));
            customer.GetComponent<DialogueBubbleCognitivePresenter>().TryPresentCognitiveDecision(Decision(), Context(customer));
            dialogue.ClearSession(); Assert.That(b.Speech.IsSpeaking, Is.False);
            Assert.That(a.MotionChannels.TryBeginGesture("wave", false, Time.unscaledTimeAsDouble, 3, out _), Is.True);
            merchant.gameObject.SetActive(false);
            Assert.That(a.MotionChannels.ActiveGestureId, Is.Zero);
            Assert.That(b.MotionChannels.ActiveGestureId, Is.Zero);
        }
        private CognitivePresentationContext Context(WorldEntity owner) => new(new CognitiveRequest {
            request_id="presentation-test", trace_id="presentation-trace", run=new CognitiveRunDto {run_id="presentation-run",agent_entity_id=owner.EntityId},
            context=new CognitiveContextSnapshot { relevant_player=new CognitivePlayerDto { player_entity_id="player_01" } } }, "player_01", dialogue.CurrentConversationId);
        private static CognitiveDecision Decision() => new() { decision_kind=CognitiveDecisionKinds.DirectResponse,
            utterance="Hello from foundation", emotion="happy", gesture="wave", look_at="player_01" };
    }
}
