using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Events;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbodiedNPC.Tests
{
    public sealed class ModeDialogueRecordingPresenter : MonoBehaviour, ICognitiveResponsePresenter
    {
        public bool Accept = true;
        public string Shown;
        public bool TryPresentCognitiveUtterance(string text)
        { if (!Accept) return false; Shown = text; return true; }
    }

    // Expected-behavior regressions: the normal-answer case intentionally fails on 60b99a3.
    // Real Driver + DailyBehaviorController + DialogueExperienceAdapter; no backend or SQLite.
    public sealed class DailyModeDialoguePresentationRegressionTests
    {
        private readonly List<Object> objects = new();
        private readonly List<ExperienceDraft> ledgerDrafts = new();
        private GameObject npc;
        private WorldEntity owner;
        private AgentOrchestrator orchestrator;
        private CognitiveRunDriver driver;
        private DailyBehaviorController daily;
        private ModeDialogueRecordingPresenter presenter;
        private int serial;

        private static void Set(object instance, string field, object value) => instance.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);

        [SetUp] public void SetUp()
        {
            npc = new GameObject("OfflineModeDialogueNpc"); objects.Add(npc);
            owner = npc.AddComponent<WorldEntity>();
            owner.Configure("npc_cafe_01", "character.npc.test", EntityKind.Agent, AgentRole.Npc);
            var receiver = npc.AddComponent<AgentEventReceiver>();
            var interest = ScriptableObject.CreateInstance<EventInterestProfile>(); objects.Add(interest);
            interest.Configure(EventInterestDecision.Ignore); receiver.Configure(owner, profile: interest);
            var bridge = npc.AddComponent<WakeCognitiveTriggerBridge>(); bridge.Configure(receiver);
            orchestrator = npc.AddComponent<AgentOrchestrator>(); orchestrator.Configure(owner, bridge);
            var composer = npc.AddComponent<CognitiveContextComposer>();
            presenter = npc.AddComponent<ModeDialogueRecordingPresenter>();
            driver = npc.AddComponent<CognitiveRunDriver>(); driver.Configure(owner, orchestrator, composer, null, presenter);
            var admission = npc.AddComponent<NpcDefaultRoutineController>();
            var runner = npc.AddComponent<GoalPlanRunner>();
            var channels = npc.AddComponent<MotionChannelCoordinator>();
            // The admission method under test only consumes these real occupancy owners.
            Set(admission, "goalPlanRunner", runner); Set(admission, "orchestrator", orchestrator);
            Set(admission, "motionChannels", channels);
            var profile = ScriptableObject.CreateInstance<DailyBehaviorProfile>(); objects.Add(profile);
            profile.Configure(DailyBehaviorMode.Work, new[] { DailyBehaviorMode.Work, DailyBehaviorMode.Rest },
                new[] { new DailyBehaviorTransition(DailyBehaviorMode.Work, DailyBehaviorMode.Rest, AgentEventTypes.WorkBreakStarted),
                    new DailyBehaviorTransition(DailyBehaviorMode.Rest, DailyBehaviorMode.Work, AgentEventTypes.WorkBreakEnded) }, 0, 0);
            daily = npc.AddComponent<DailyBehaviorController>();
            Set(daily, "profile", profile); Set(daily, "owner", owner); Set(daily, "receiver", receiver);
            Set(daily, "admission", admission); Set(daily, "enteredAt", -100d); Set(daily, "nextTransitionAt", -100d);
            var playerObject = new GameObject("OfflineModeDialoguePlayer"); objects.Add(playerObject);
            playerObject.AddComponent<WorldEntity>().Configure("player_01", "character.player.test", EntityKind.Agent, AgentRole.Player);
            var registryObject = new GameObject("OfflineModeDialogueRegistry"); objects.Add(registryObject);
            var registry = registryObject.AddComponent<EntityRegistry>(); registry.RebuildRegistry();
            var adapter = new DialogueExperienceAdapter(ledgerDrafts.Add, registry);
            driver.UtterancePresented += adapter.Handle;
        }

        [TearDown] public void TearDown()
        {
            foreach (var item in objects.AsEnumerable().Reverse()) if (item != null) Object.DestroyImmediate(item);
            objects.Clear(); ledgerDrafts.Clear();
        }

        private CognitiveRequest Request(bool includeDaily = true)
        {
            var id = (++serial).ToString();
            var trigger = new CognitiveTrigger("trigger_" + id, CognitiveTriggerTypes.Event, owner.EntityId,
                "event_" + id, 1d, AgentEventTypes.PlayerUtterance);
            Assert.That(orchestrator.TryStartRun(trigger, out var run), Is.True);
            var triggerDto = CognitiveTriggerDto.From(trigger);
            var request = new CognitiveRequest {
                request_id = "request_" + id, trace_id = "trace_" + id,
                run = CognitiveRunDto.From(run), trigger = triggerDto,
                context = new CognitiveContextSnapshot {
                    run_id = run.RunId, agent_entity_id = owner.EntityId, trigger = triggerDto,
                    current_event = new CognitiveEventDto { event_id = "event_" + id,
                        event_type = AgentEventTypes.PlayerUtterance, source_entity_id = "player_01", target_entity_id = owner.EntityId,
                        payload = new[] { new CognitiveKeyValueDto { key = "text", value = "歇会儿吧，你平时喜欢做什么？" },
                            new CognitiveKeyValueDto { key = "conversation_id", value = "mode_dialogue_test" } } },
                    relevant_player = new CognitivePlayerDto { player_entity_id = "player_01", display_name = "玩家" },
                    character = new CognitiveCharacterDto { character_id = "cafe.merchant", display_name = "林薇薇",
                        role_summary = "咖啡馆店员", backstory = "喜欢画招牌。", personality = "开朗。",
                        speaking_style = "自然简短。", boundaries = "不承诺未执行的动作。" },
                    perception = PerceptionSnapshotDto.Empty(owner.EntityId),
                    daily_behavior = includeDaily ? daily.CaptureCognitiveContext(run.RunId) : null } };
            Assert.That(request.IsValid, Is.True);
            Assert.That(driver.TryOwnRequest(request), Is.True);
            return request;
        }

        private CognitiveResponse Response(CognitiveRequest request, string mode, string text) => new() {
            message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
            trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = owner.EntityId,
            decision = new CognitiveDecision { decision_kind = "DIRECT_RESPONSE", target_daily_mode = mode, utterance = text } };

        private void AssertLedgerMatchesDisplay()
        {
            Assert.That(ledgerDrafts.Count, Is.EqualTo(1));
            Assert.That(ledgerDrafts[0].NaturalLanguagePayload, Is.EqualTo(presenter.Shown));
            Assert.That(ledgerDrafts[0].EventType, Is.EqualTo("dialogue_response"));
        }

        [Test] public void SuccessfulSwitch_MustKeepSubstantiveAnswerWithoutStartingWorldAction()
        {
            var request = Request();
            var response = Response(request, "rest", "我先歇会儿。空闲时我喜欢画街头招牌。");
            Assert.That(driver.TryConsumeResponse(response), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(driver.LastValidatedGoalProposal, Is.Null);
            AssertLedgerMatchesDisplay();
            Assert.That(presenter.Shown, Does.Contain("画街头招牌"), "Current bug: the entire answer is replaced by ModeStatus.");
        }

        [Test] public void PureModeIntent_PreservesOrdinaryReply()
        {
            Assert.That(driver.TryConsumeResponse(Response(Request(), "rest", "我先歇会儿。")), Is.True);
            Assert.That(presenter.Shown, Is.EqualTo("我先歇会儿。"));
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); AssertLedgerMatchesDisplay();
        }

        [Test] public void ExpiredModeIntent_KeepsActualModeAndPreservesUncertifiedDialogue()
        {
            var request = Request();
            Set(daily, "capturedAt", Time.unscaledTimeAsDouble - 31);
            Assert.That(driver.TryConsumeResponse(Response(request, "rest", "已经休息了，拿铁也做好了。")), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(daily.ModeRevision, Is.Zero);
            Assert.That(presenter.Shown, Is.EqualTo("已经休息了，拿铁也做好了。")); AssertLedgerMatchesDisplay();
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(driver.LastValidatedGoalProposal, Is.Null);
        }

        [Test] public void NoDailyBehavior_NormalChatRemainsVerbatim()
        {
            Object.DestroyImmediate(daily); daily = null;
            var request = Request(false);
            Assert.That(driver.TryConsumeResponse(Response(request, null, "我喜欢画街头招牌。")), Is.True);
            Assert.That(presenter.Shown, Is.EqualTo("我喜欢画街头招牌。")); AssertLedgerMatchesDisplay();
        }

        [Test] public void Rest_NormalChatDoesNotRequireReturningToWork()
        {
            Assert.That(driver.TryConsumeResponse(Response(Request(), "rest", "我先歇会儿。")), Is.True);
            ledgerDrafts.Clear();
            var request = Request();
            Assert.That(driver.TryConsumeResponse(Response(request, null, "我喜欢画街头招牌。")), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
            Assert.That(presenter.Shown, Is.EqualTo("我喜欢画街头招牌。")); AssertLedgerMatchesDisplay();
        }

        [TestCase("我这就给你做拿铁，稍等两分钟。")]
        [TestCase("杯子我已经拿来了，咖啡也做好了。")]
        [TestCase("我喜欢画街头招牌。那杯我马上给你端过去。")]
        // Also retain all three samples from the previous duplicate candidate fixture.
        [TestCase("刚刚已为你做好拿铁，我这就送到你的桌边。")]
        [TestCase("我已经回到吧台，拿铁也做好了。")]
        [TestCase("我喜欢画街头招牌。那杯拿铁马上给你送过去。")]
        public void UnsubmittedActionWords_AreDialogueOnlyAndNeverExecute(string text)
        {
            // Retired requirement: replace every mode reply with ModeStatus.
            // The original dangerous samples remain; speech is not execution evidence.
            int outcomes = 0;
            var runner = npc.GetComponent<GoalPlanRunner>();
            runner.GoalOutcomePublished += _ => outcomes++;
            Assert.That(driver.TryConsumeResponse(Response(Request(), "rest", text)), Is.True);
            Assert.That(presenter.Shown, Is.EqualTo(text));
            Assert.That(runner.HasActiveTask, Is.False);
            Assert.That(outcomes, Is.Zero);
            Assert.That(ledgerDrafts[0].StructuredPayload["model_content_is_not_world_truth"], Is.EqualTo(true));
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(driver.LastValidatedGoalProposal, Is.Null); AssertLedgerMatchesDisplay();
        }

        [Test] public void CombinedActionMode_CannotBypassMissingActionHandoff()
        {
            var request = Request();
            var response = Response(request, "rest", "杯子我马上拿给你。");
            response.decision.decision_kind = "ACTION_PROPOSAL";
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                bindings = new Dictionary<string, string> { ["target"] = "cup_01", ["destination"] = "player_01" } };
            response.validated_goal = new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" };
            Assert.That(response.IsValid, Is.True);
            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(daily.ModeRevision, Is.Zero);
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(presenter.Shown, Does.Not.Contain("马上拿给你")); AssertLedgerMatchesDisplay();
        }

        [Test] public void FailedPresentation_KeepsCommittedModeAndEmitsNoLedgerDraft()
        {
            presenter.Accept = false;
            CognitiveRequestFeedback feedback = null;
            driver.RequestFeedback += value => feedback = value;
            Assert.That(driver.TryConsumeResponse(Response(Request(), "rest", "我先歇会儿。")), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
            Assert.That(driver.LastDailyModeResult, Is.EqualTo("cognition_mode_applied"));
            Assert.That(feedback.Reason, Is.EqualTo("PRESENTATION_UNAVAILABLE"));
            Assert.That(ledgerDrafts, Is.Empty); Assert.That(presenter.Shown, Is.Null);
        }

        [Test] public void CurrentWire_CannotCertifyWhetherUtteranceContainsActionCommitments()
        {
            var request = Request();
            foreach (var text in new[] { "我喜欢画街头招牌。", "杯子我已经拿来了。", "我不想休息，但拿铁马上送来。" })
            {
                var response = Response(request, "rest", text);
                Assert.That(CognitiveResponseValidator.TryParse(JsonConvert.SerializeObject(response), out var parsed, out _), Is.True);
                Assert.That(parsed.decision.decision_kind, Is.EqualTo("DIRECT_RESPONSE"));
                Assert.That(parsed.decision.action_proposal, Is.Null);
                Assert.That(parsed.decision.target_daily_mode, Is.EqualTo("rest"));
            }
        }

        [Test] public void RestToWork_PreservesPersonalAnswerAndLedger()
        {
            typeof(DailyBehaviorController).GetProperty("CurrentMode").SetValue(daily, DailyBehaviorMode.Rest);
            Assert.That(driver.TryConsumeResponse(Response(Request(), "work", "我喜欢画招牌，也想听听你的配色建议。")), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(presenter.Shown, Is.EqualTo("我喜欢画招牌，也想听听你的配色建议。"));
            AssertLedgerMatchesDisplay();
        }

        [Test] public void ModeRejected_StillAnswersPersonalQuestion()
        {
            var request = Request(); Set(daily, "capturedAt", Time.unscaledTimeAsDouble - 31);
            Assert.That(driver.TryConsumeResponse(Response(request, "rest", "我喜欢画街头招牌。")), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(driver.LastDailyModeResult, Is.EqualTo("stale_or_invalid_context"));
            Assert.That(presenter.Shown, Is.EqualTo("我喜欢画街头招牌。")); AssertLedgerMatchesDisplay();
        }

        [Test] public void NextRequest_UsesConfirmedModeWithoutChangingPersona()
        {
            var first = Request(); string persona = JsonConvert.SerializeObject(first.context.character);
            Assert.That(driver.TryConsumeResponse(Response(first, "rest", "空闲时我喜欢画招牌。")), Is.True);
            var next = Request();
            Assert.That(next.context.daily_behavior.current_mode, Is.EqualTo("rest"));
            Assert.That(next.context.daily_behavior.mode_revision, Is.EqualTo(1));
            Assert.That(JsonConvert.SerializeObject(next.context.character), Is.EqualTo(persona));
        }

        [Test] public void OldRunResponse_CannotCommitModeOrReplaceNewReply()
        {
            var old = Request(); orchestrator.CancelCurrentRun(); var current = Request();
            Assert.That(driver.TryConsumeResponse(Response(old, "rest", "过期回答")), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(daily.ModeRevision, Is.Zero); Assert.That(ledgerDrafts, Is.Empty);
            Assert.That(driver.PendingRequest, Is.SameAs(current));
            Assert.That(driver.TryConsumeResponse(Response(current, null, "当前回答")), Is.True);
            Assert.That(presenter.Shown, Is.EqualTo("当前回答")); AssertLedgerMatchesDisplay();
        }

        [Test] public void WrongAgentResponse_CannotCommitOrPresent()
        {
            var request = Request(); var response = Response(request, "rest", "错误归属");
            response.agent_entity_id = "npc_other";
            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(ledgerDrafts, Is.Empty); Assert.That(presenter.Shown, Is.Null);
        }
    }
}
