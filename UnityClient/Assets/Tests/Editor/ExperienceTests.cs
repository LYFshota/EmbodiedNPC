using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class ExperienceTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void Presented_NoConversation_IsNotDialogueEvidence(string conversationId)
        {
            Assert.That(Presented(conversationId).IsDialogueEvidence, Is.False);
        }

        [Test]
        public void Presented_WithConversation_IsDialogueEvidence()
        {
            Assert.That(Presented("player_01:npc_cafe_01:conversation").IsDialogueEvidence, Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t ")]
        public void DialogueAdapter_NonConversationPresented_DoesNotEnqueue(string conversationId)
        {
            var drafts = new List<ExperienceDraft>();
            new DialogueExperienceAdapter(drafts.Add).Handle(Presented(conversationId));
            Assert.That(drafts, Is.Empty);
        }

        [Test]
        public void DialogueAdapter_ConversationPresented_EnqueuesOnce()
        {
            const string conversation = "player_01:npc_cafe_01:conversation";
            var drafts = new List<ExperienceDraft>();
            new DialogueExperienceAdapter(drafts.Add).Handle(Presented(conversation));
            Assert.That(drafts, Has.Count.EqualTo(1));
            Assert.That(drafts[0].EventType, Is.EqualTo("dialogue_response"));
            Assert.That(drafts[0].SourceType, Is.EqualTo("dialogue"));
            Assert.That(drafts[0].ConversationId, Is.EqualTo(conversation));
            Assert.That(drafts[0].NaturalLanguagePayload, Is.EqualTo("你好"));
            Assert.That(drafts[0].ObserverId, Is.EqualTo("npc_cafe_01"));
        }

        private static PresentedCognitiveUtterance Presented(string conversationId) => new(
            "cognitive-request-test", "cognitive-trace-test", "run-test", "npc_cafe_01", "player_01",
            "你好", 1, 2d, "source-event-test", conversationId, null);

        [Test]
        public void ObservationAdapter_PreservesObserverUnknownsAndClockDomain()
        {
            Observation observation = new("obs_audio", "npc_ybot_01", ObservationSourceType.GameplayEvent,
                "audio:event:01", ObservationChannel.Audio, string.Empty,
                new Dictionary<string, string> { ["event_type"] = "DoorOpened" },
                new[] { "actor_entity_id", "target_entity_id", "exact_position" },
                0.7f, 4, 12d, 20d, PerceptionLifecycleState.Detected, "evt_audio_01", 10d);
            ExperienceDraft draft = ObservationExperienceAdapter.Create(observation, 3);

            Assert.That(draft.ObserverId, Is.EqualTo("npc_ybot_01"));
            Assert.That(draft.Provenance, Is.EqualTo("HEARD"));
            Assert.That(draft.ActorEntityId, Is.Null);
            Assert.That(draft.TargetEntityIds, Is.Empty);
            Assert.That(draft.ClockDomain, Is.EqualTo("unity_realtime"));
            Assert.That((IReadOnlyList<string>)draft.StructuredPayload["unknown_fields"],
                Does.Contain("actor_entity_id"));
        }

        [Test]
        public void ObservationAdapter_UnchangedDoesNotEmitAndTypedProjectionIsSkipped()
        {
            int count = 0;
            var adapter = new ObservationExperienceAdapter(_ => count++);
            Observation visual = Observation("Tracked", "Manual", 1);
            adapter.Handle(new ObservationApplyResult(ObservationApplyCode.Unchanged, visual, 1));
            Observation goal = Observation("Tracked", "GoalSucceeded", 2);
            adapter.Handle(new ObservationApplyResult(ObservationApplyCode.Applied, goal, 2));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void ObservationAdapter_ContinuousJitterKeepsOnlyLastAdmittedAnchor()
        {
            var drafts = new List<ExperienceDraft>();
            var adapter = new ObservationExperienceAdapter(drafts.Add);
            var aggregator = new PerceptionAggregator("npc_ybot_01");
            adapter.Handle(aggregator.ApplyOrRefresh(Scan("0,1,0", 1)));
            for (int i = 2; i <= 100; i++)
                adapter.Handle(aggregator.ApplyOrRefresh(Scan(i % 2 == 0 ? "0.02,1,0" : "0,1,0", i)));
            Assert.That(drafts, Has.Count.EqualTo(1));
            // The live perception remains exact and fresh while historical sampling is quiet.
            Assert.That(aggregator.PerceptionRevision, Is.GreaterThan(1));
            foreach (string position in new[] { "0.10,1,0", "0.20,1,0", "0.30,1,0" })
                adapter.Handle(aggregator.ApplyOrRefresh(Scan(position, 101)));
            Assert.That(drafts, Has.Count.EqualTo(2), "Compare with the admitted anchor, not the last tiny step.");
        }

        [Test]
        public void ObservationAdapter_PreservesTransitionsFieldsScopesAndGameplayEvents()
        {
            var drafts = new List<ExperienceDraft>();
            var adapter = new ObservationExperienceAdapter(drafts.Add);
            void Admit(Observation value) => adapter.Handle(new ObservationApplyResult(ObservationApplyCode.Applied, value, 1));
            Admit(Scan("0,1,0", 1));
            Admit(Scan("0.01,1,0", 2, field: "changed"));
            Admit(Scan("0.01,1,0", 3, lifecycle: PerceptionLifecycleState.Lost, field: "changed"));
            Admit(Scan("0.01,1,0", 4, field: "changed"));
            Admit(Scan("0.01,1,0", 5, observer: "npc_ybot_02", field: "changed"));
            Admit(Scan("0.01,1,0", 6, subject: "player_02", field: "changed"));
            Admit(Scan("bad-position", 7, field: "changed"));
            Admit(Scan("other-bad-position", 8, field: "changed"));
            for (int i = 9; i <= 10; i++)
                Admit(new Observation("wave_" + i, "npc_ybot_01", ObservationSourceType.GameplayEvent,
                    "wave_" + i, ObservationChannel.Visual, "player_01",
                    new Dictionary<string, string> { ["event_type"] = "PlayerWave" }, Array.Empty<string>(),
                    1f, 1, i, double.PositiveInfinity));
            Assert.That(drafts, Has.Count.EqualTo(10));
        }

        private static Observation Scan(string position, int tick, string observer = "npc_ybot_01",
            string subject = "player_01", PerceptionLifecycleState lifecycle = PerceptionLifecycleState.Tracked,
            string field = "unchanged") => new("visual_" + subject, observer, ObservationSourceType.ContinuousScan,
            "visual:" + subject, ObservationChannel.Visual, subject,
            new Dictionary<string, string> { ["last_seen_position"] = position, ["public_state"] = field },
            Array.Empty<string>(), 1f, tick, tick, double.PositiveInfinity, lifecycle);

        [Test]
        public void ReportedObservation_PreservesOneHopLineage()
        {
            Observation value = new("obs_report", "npc_ybot_02", ObservationSourceType.Direct,
                "report_01", ObservationChannel.Direct, "cup_01",
                new Dictionary<string, string> { ["event_type"] = "GoalSucceeded", ["epistemic_status"] = "reported" },
                Array.Empty<string>(), 0.6f, 5, 20d, double.PositiveInfinity,
                PerceptionLifecycleState.Tracked, "report_01", 10d,
                "npc_ybot_01", "mem_parent_01", "conversation_01", 1);
            ExperienceDraft draft = ObservationExperienceAdapter.Create(value, 4);

            Assert.That(draft.EventType, Is.EqualTo("reported_observation"));
            Assert.That(draft.Provenance, Is.EqualTo("REPORTED"));
            Assert.That(draft.StructuredPayload["parent_memory_id"], Is.EqualTo("mem_parent_01"));
            Assert.That(draft.StructuredPayload["report_hop"], Is.EqualTo(1));
        }

        [TestCase(PlanExecutionStatus.Succeeded, "goal_succeeded")]
        [TestCase(PlanExecutionStatus.Failed, "goal_failed")]
        [TestCase(PlanExecutionStatus.Cancelled, "goal_cancelled")]
        [TestCase(PlanExecutionStatus.TimedOut, "goal_timed_out")]
        public void GoalAdapter_MapsTerminalStatus(PlanExecutionStatus status, string eventType)
        {
            var outcome = new GoalOutcome("outcome_01", "proposal_01", 1, "trace_01", "bring_object",
                "npc_ybot_01", "cup_01", "player_01", status, "failure", 8, 11d);
            ExperienceDraft draft = GoalOutcomeExperienceAdapter.Create(outcome);
            Assert.That(draft.EventType, Is.EqualTo(eventType));
            Assert.That(draft.Provenance, Is.EqualTo("EXECUTED_BY_SELF"));
            Assert.That(draft.RequestId, Is.Null);
            Assert.That(draft.ConversationId, Is.Null);
        }

        [Test]
        public void GoalAndSkillAdapters_RejectOtherActor()
        {
            int count = 0;
            var goalAdapter = new GoalOutcomeExperienceAdapter("npc_ybot_01", _ => count++);
            goalAdapter.Handle(new GoalOutcome("outcome", "proposal", 0, "trace", "open_door",
                "npc_ybot_02", "door_01", "", PlanExecutionStatus.Succeeded, "", 1, 1d));
            var skillAdapter = new SkillResultExperienceAdapter("npc_ybot_01", _ => count++);
            skillAdapter.Handle(new SkillResult("skill", "trace", "Open", "npc_ybot_02", "door_01", "",
                SkillStatus.Succeeded, "", 1, 0, 1));
            Assert.That(count, Is.Zero);
        }

        [Test]
        public void SkillAndDialogueAdapters_PreserveDistinctGranularityAndCorrelation()
        {
            SkillResult skill = new("skill_01", "trace_01", "Open", "npc_ybot_01", "door_01", "",
                SkillStatus.Succeeded, "", 2, 3, 4);
            ExperienceDraft skillDraft = SkillResultExperienceAdapter.Create(skill, 5d);
            AcceptedDialogueTurn turn = new(false, "dialogue-request-01", "dialogue-trace-01",
                "player_01:npc_ybot_01:test", "player_01", "npc_ybot_01", "你好", 4, 6d, 7d);
            ExperienceDraft dialogue = DialogueExperienceAdapter.Create(turn);
            Assert.That(skillDraft.EventType, Is.EqualTo("skill_succeeded"));
            Assert.That(dialogue.EventType, Is.EqualTo("dialogue_input"));
            Assert.That(dialogue.RequestId, Is.EqualTo("dialogue-request-01"));
            Assert.That(dialogue.NaturalLanguagePayload, Is.EqualTo("你好"));
            Assert.That(dialogue.Provenance, Is.EqualTo("SYSTEM_CONFIRMED"));
        }

        [Test]
        public void RuntimeV2DialogueTurns_ProjectToOnlyTheirTargetObserver()
        {
            AcceptedDialogueTurn merchantTurn = new(false, "cognitive-request-merchant",
                "cognitive-trace-merchant", "conversation-merchant", "player_01", "npc_cafe_01",
                "merchant-memory-a", 7, 2d, 3d, "agent_event_merchant");
            AcceptedDialogueTurn customerTurn = new(false, "cognitive-request-customer",
                "cognitive-trace-customer", "conversation-customer", "player_01",
                "npc_cafe_regular_01", "customer-memory-a", 7, 4d, 5d,
                "agent_event_customer");

            ExperienceDraft merchant = DialogueExperienceAdapter.Create(merchantTurn);
            ExperienceDraft customer = DialogueExperienceAdapter.Create(customerTurn);

            Assert.That(merchant.ObserverId, Is.EqualTo("npc_cafe_01"));
            Assert.That(customer.ObserverId, Is.EqualTo("npc_cafe_regular_01"));
            Assert.That(merchant.ActorEntityId, Is.EqualTo("player_01"));
            Assert.That(customer.ActorEntityId, Is.EqualTo("player_01"));
            Assert.That(merchant.SourceEventId, Is.EqualTo("agent_event_merchant"));
            Assert.That(customer.SourceEventId, Is.EqualTo("agent_event_customer"));
            Assert.That(merchant.RequestId, Is.EqualTo("cognitive-request-merchant"));
            Assert.That(customer.RequestId, Is.EqualTo("cognitive-request-customer"));
            Assert.That(merchant.NaturalLanguagePayload, Is.Not.EqualTo(customer.NaturalLanguagePayload));
        }

        [Test]
        public void ExperienceProtocol_IsStrictAndQueueDeduplicatesPendingDraft()
        {
            const string valid = "{\"message_type\":\"experience_append_result\",\"schema_version\":\"1.0\"," +
                                 "\"request_id\":\"request_01\",\"trace_id\":\"trace_01\"," +
                                 "\"event_id\":\"exp_01\",\"observer_id\":\"npc_ybot_01\"," +
                                 "\"experience_seq\":1,\"status\":\"inserted\",\"reason\":\"STORED\"}";
            Assert.That(ExperienceAppendResultValidator.TryParse(valid, out var result, out _), Is.True);
            Assert.That(result.IsPersisted, Is.True);
            Assert.That(ExperienceAppendResultValidator.TryParse(valid.TrimEnd('}') + ",\"extra\":1}",
                out _, out _), Is.False);
            Assert.That(ExperienceAppendResultValidator.TryParse(
                valid.Replace("\"experience_seq\":1", "\"experience_seq\":\"1\""), out _, out _), Is.False);

            GameObject owner = new("experience-client-test");
            BackendWebSocketClient client = owner.AddComponent<BackendWebSocketClient>();
            var coordinator = new ExperienceWriteCoordinator(client);
            ExperienceDraft draft = GoalOutcomeExperienceAdapter.Create(new GoalOutcome(
                "outcome_01", "proposal_01", 1, "trace_01", "open_door", "npc_ybot_01", "door_01", "",
                PlanExecutionStatus.Succeeded, "", 1, 1d));
            Assert.That(coordinator.Enqueue(draft), Is.True);
            Assert.That(coordinator.Enqueue(draft), Is.False);
            Assert.That(coordinator.PendingCount, Is.EqualTo(1));
            UnityEngine.Object.DestroyImmediate(owner);
        }

        [Test]
        public void TransportSerialization_ContainsExplicitNullsAndNoLedgerOwnedFields()
        {
            ExperienceDraft draft = GoalOutcomeExperienceAdapter.Create(new GoalOutcome(
                "outcome_01", "proposal_01", 1, "trace_01", "open_door", "npc_ybot_01", "door_01", "",
                PlanExecutionStatus.Succeeded, "", 1, 1d));
            string json = ExperienceAppendRequest.FromDraft(draft).Serialize();
            Assert.That(json, Does.Contain("\"world_revision\":1"));
            Assert.That(json, Does.Contain("\"confidence\":null"));
            Assert.That(json, Does.Not.Contain("experience_seq"));
            Assert.That(json, Does.Not.Contain("ingested_at_utc"));
            Assert.That(json, Does.Not.Contain("IsValid"));
        }

        private static Observation Observation(string lifecycle, string eventType, int revision)
        {
            Observation value = new("obs_" + revision, "npc_ybot_01", ObservationSourceType.Direct,
                "event_" + revision, ObservationChannel.Direct, "cup_01",
                new Dictionary<string, string> { ["event_type"] = eventType }, Array.Empty<string>(),
                1f, revision, revision, double.PositiveInfinity,
                Enum.Parse<PerceptionLifecycleState>(lifecycle), "event_" + revision, revision);
            return value;
        }
    }
}
