using System.Collections.Generic;
using System.Reflection;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Memory;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class DialogueContinuationContractTests
    {
        [TestCase(null, "CONTINUE", true)]
        [TestCase("WAIT_FOR_PLAYER", "END", true)]
        [TestCase("MAY_CONTINUE", "CONTINUE", true)]
        [TestCase("MAY_CONTINUE", "END", false)]
        [TestCase("CONTINUE", null, false)]
        public void PolicyIsOptionalAndDoesNotReplaceEpisode(string policy, string episode, bool valid)
        {
            Assert.That(new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", utterance = "你好。",
                dialogue_turn_policy = policy, episode_directive = episode }.IsValid, Is.EqualTo(valid));
        }

        [Test]
        public void AcknowledgementMustMatchAppendEnvelopeAndObserverThenSettleOnce()
        {
            var go = new GameObject("isolated-ack-test");
            try
            {
                var coordinator = new ExperienceWriteCoordinator(go.AddComponent<BackendWebSocketClient>());
                ExperienceDraft draft = DialogueExperienceAdapter.Create(new AcceptedDialogueTurn(true,
                    "cognitive_test", "trace_test", "conversation_test", "player_01", "npc_cafe_01",
                    "你好。", 1, 1, 1, "event_test"));
                coordinator.Enqueue(draft);
                int settled = 0;
                coordinator.AppendSettled += (value, ack, status) => { settled++; Assert.That(value.request_id, Is.EqualTo(draft.RequestId)); };
                var pending = (System.Collections.IList)typeof(ExperienceWriteCoordinator).GetField("pending",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(coordinator);
                var envelope = (ExperienceAppendRequest)pending[0].GetType().GetProperty("Request").GetValue(pending[0]);
                ValidatedExperienceAppendResult Ack(string requestId, string observer)
                {
                    string json = "{\"message_type\":\"experience_append_result\",\"schema_version\":\"1.0\"," +
                        "\"request_id\":\"" + requestId + "\",\"trace_id\":\"" + envelope.trace_id +
                        "\",\"event_id\":\"exp_test\",\"observer_id\":\"" + observer +
                        "\",\"experience_seq\":2,\"status\":\"inserted\",\"reason\":\"STORED\"}";
                    Assert.That(ExperienceAppendResultValidator.TryParse(json, out var result, out _), Is.True);
                    return result;
                }
                coordinator.HandleResult(Ack(draft.RequestId, draft.ObserverId));
                coordinator.HandleResult(Ack(envelope.request_id, "npc_other"));
                Assert.That(settled, Is.Zero);
                coordinator.HandleResult(Ack(envelope.request_id, draft.ObserverId));
                coordinator.HandleResult(Ack(envelope.request_id, draft.ObserverId));
                Assert.That(settled, Is.EqualTo(1));
                Assert.That(coordinator.PendingCount, Is.Zero);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ContinuationPresentationKeepsIndependentRequestAndParentMetadata()
        {
            var evt = new CognitiveEventDto { event_type = "npc.dialogue_continue", payload = new[] {
                new CognitiveKeyValueDto { key = "parent_request_id", value = "parent_request" },
                new CognitiveKeyValueDto { key = "parent_event_id", value = "parent_event" },
                new CognitiveKeyValueDto { key = "last_response_event_id", value = "exp_parent" },
                new CognitiveKeyValueDto { key = "last_response_seq", value = "2" } } };
            var drafts = new List<ExperienceDraft>();
            new DialogueExperienceAdapter(drafts.Add).Handle(new PresentedCognitiveUtterance("auto_request",
                "auto_trace", "auto_run", "npc_cafe_01", "player_01", "补充一句。", 1, 5,
                "auto_event", "conversation_test", null, "WAIT_FOR_PLAYER", evt));
            Assert.That(drafts.Count, Is.EqualTo(1));
            Assert.That(drafts[0].RequestId, Is.EqualTo("auto_request"));
            Assert.That(drafts[0].StructuredPayload["parent_request_id"], Is.EqualTo("parent_request"));
            Assert.That(drafts[0].StructuredPayload["initiative"], Is.EqualTo("dialogue_continuation"));
            Assert.That(drafts[0].StructuredPayload["last_response_seq"], Is.EqualTo(2));
            Assert.That(drafts[0].ActorEntityId, Is.EqualTo("npc_cafe_01"));
        }

        [TestCase("queue_full")]
        [TestCase("retry_exhausted")]
        public void EarlyAppendFailureCannotArmContinuationAfterPresentation(string status)
        {
            var go = new GameObject("isolated-scheduler-ack-order");
            try
            {
                var controller = go.AddComponent<DialogueController>();
                var scheduler = go.AddComponent<DialogueContinuationScheduler>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(DialogueController).GetField("activeContext", flags).SetValue(controller,
                    new DialogueContext { conversationId = "conv", speakerEntityId = "player_01", targetAgentId = "npc_cafe_01" });
                var parent = new CognitiveRequest { request_id = "parent", trace_id = "trace",
                    run = new CognitiveRunDto { agent_entity_id = "npc_cafe_01" },
                    context = new CognitiveContextSnapshot { current_event = new CognitiveEventDto { payload = new[] {
                        new CognitiveKeyValueDto { key = "conversation_id", value = "conv" } } } } };
                typeof(DialogueContinuationScheduler).GetField("controller", flags).SetValue(scheduler, controller);
                typeof(DialogueContinuationScheduler).GetField("parent", flags).SetValue(scheduler, parent);
                typeof(DialogueContinuationScheduler).GetField("featureEnabled", flags).SetValue(scheduler, true);
                var draft = new ExperienceDraftDto { event_type = "dialogue_response", request_id = "parent",
                    trace_id = "trace", observer_id = "npc_cafe_01", conversation_id = "conv" };
                typeof(DialogueContinuationScheduler).GetMethod("HandleAppendSettled", flags).Invoke(scheduler,
                    new object[] { draft, null, status });
                typeof(DialogueContinuationScheduler).GetMethod("HandlePresented", flags).Invoke(scheduler,
                    new object[] { new PresentedCognitiveUtterance("parent", "trace", "run", "npc_cafe_01", "player_01",
                        "回复。", 0, 1, "event", "conv", null, "MAY_CONTINUE") });
                Assert.That(scheduler.LastStatus, Is.EqualTo("persistence_failed_" + status));
                Assert.That(typeof(DialogueContinuationScheduler).GetField("candidate", flags).GetValue(scheduler), Is.Null);
                Assert.That(scheduler.ActiveRequest, Is.Null);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test] public void DisabledFeatureAddsNoDialogueDependency()
        {
            var go = new GameObject("isolated-disabled-policy");
            try
            {
                var scheduler = go.AddComponent<DialogueContinuationScheduler>();
                var controller = go.AddComponent<DialogueController>();
                var npc = new GameObject("isolated-npc"); npc.transform.SetParent(go.transform);
                npc.AddComponent<EmbodiedNPC.World.WorldEntity>().Configure("npc_cafe_01", "test.character",
                    EmbodiedNPC.World.EntityKind.Agent, EmbodiedNPC.World.AgentRole.Npc);
                var participant = npc.AddComponent<DialogueParticipant>();
                var composer = npc.AddComponent<CognitiveContextComposer>(); composer.enabled = false;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(DialogueParticipant).GetMethod("Awake", flags).Invoke(participant, null);
                typeof(DialogueController).GetField("activeContext", flags).SetValue(controller,
                    new DialogueContext { conversationId = "conversation", speakerEntityId = "player_01", targetAgentId = "npc_cafe_01" });
                typeof(DialogueController).GetField("selectedTarget", flags).SetValue(controller, participant);
                typeof(DialogueContinuationScheduler).GetField("controller", flags).SetValue(scheduler, controller);
                typeof(DialogueContinuationScheduler).GetField("composer", flags).SetValue(scheduler, composer);
                typeof(DialogueContinuationScheduler).GetField("latestDisplayedRequest", flags).SetValue(scheduler, "previous_auto");
                typeof(DialogueContinuationScheduler).GetField("latestDisplayedSession", flags).SetValue(scheduler, "session");
                scheduler.SetFeatureEnabled(true);
                var enabledPayload = new Dictionary<string, string>();
                scheduler.AddDisplayedResponseDependency(enabledPayload, "npc_cafe_01", "conversation");
                Assert.That(enabledPayload["required_response_request_id"], Is.EqualTo("previous_auto"));
                scheduler.SetFeatureEnabled(false);
                var payload = new Dictionary<string, string>();
                scheduler.AddDisplayedResponseDependency(payload, "npc_cafe_01", "conversation");
                Assert.That(payload, Is.Empty);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
