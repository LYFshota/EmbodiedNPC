using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Memory;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
namespace CafeDemo.Tests
{
    // Actual Cafe scene, deterministic decisions, no sockets or paid model.
    public sealed class CafeDailyModeDialogueTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        CafeP1aLifecycleTests fixture;
        CognitiveDialogueSubmissionHandler merchant;
        CognitiveRunDriver driver;
        GoalPlanRunner runner;
        DialogueController dialogue;
        DailyBehaviorController daily;
        WorldStateService world;
        WorldEntity player;
        List<GoalOutcome> outcomes;
        List<ExperienceDraft> drafts;
        List<PresentedCognitiveUtterance> shown;
        T Field<T>(string name) => (T)typeof(CafeP1aLifecycleTests).GetField(name, Fields).GetValue(fixture);
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Fields).Invoke(target, args);
        [UnitySetUp] public IEnumerator Setup()
        {
            fixture = new CafeP1aLifecycleTests(); Call(fixture, "PrepareScene");
            foreach (var trigger in Object.FindObjectsByType<DailyBehaviorDemoTrigger>()) trigger.ConfigureDemo(false, 15, 120);
            yield return new EnterPlayMode();
            fixture = new CafeP1aLifecycleTests(); Call(fixture, "BindScene");
            merchant = Field<CognitiveDialogueSubmissionHandler>("merchant"); driver = Field<CognitiveRunDriver>("driver");
            runner = Field<GoalPlanRunner>("runner"); dialogue = Field<DialogueController>("dialogue");
            world = Field<WorldStateService>("world"); player = Field<WorldEntity>("player");
            outcomes = Field<List<GoalOutcome>>("outcomes"); drafts = Field<List<ExperienceDraft>>("dialogueDrafts");
            shown = Field<List<PresentedCognitiveUtterance>>("utterances"); daily = merchant.GetComponent<DailyBehaviorController>();
            var profile = Object.Instantiate(daily.Profile);
            var timing = new SerializedObject(profile);
            timing.FindProperty("minimumModeSeconds").floatValue = 0;
            timing.FindProperty("transitionCooldown").floatValue = 0;
            timing.ApplyModifiedPropertiesWithoutUndo();
            var fields = new SerializedObject(daily); fields.FindProperty("profile").objectReferenceValue = profile; fields.ApplyModifiedPropertiesWithoutUndo();
        }
        [UnityTearDown] public IEnumerator Teardown()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        }
        CognitiveRequest Begin(string text)
        {
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            return (CognitiveRequest)Call(fixture, "BeginUtterance", text);
        }
        static CognitiveResponse Reply(CognitiveRequest request, string text, string mode = null)
        {
            var response = (CognitiveResponse)typeof(CafeP1aLifecycleTests).GetMethod("Response", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { request, false });
            response.decision.utterance = text; response.decision.target_daily_mode = mode; return response;
        }
        void AssertActualReply(string text)
        {
            Assert.That(shown.Last().Utterance, Is.EqualTo(text));
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages("npc_cafe_01").Last().Text, Is.EqualTo(text));
            Assert.That(drafts.Last().NaturalLanguagePayload, Is.EqualTo(text));
            Assert.That(drafts.Last().StructuredPayload["model_content_is_not_world_truth"], Is.EqualTo(true));
        }
        [UnityTest] public IEnumerator ModeAndReply_ConfirmedBeforeBubble_NextContextUsesMode()
        {
            var request = Begin("歇会儿吧，你最喜欢什么？");
            var persona = Newtonsoft.Json.JsonConvert.SerializeObject(request.context.character);
            DailyBehaviorMode? atPresentation = null; driver.UtterancePresented += _ => atPresentation = daily.CurrentMode;
            Assert.That(driver.TryConsumeResponse(Reply(request, "我喜欢画街头招牌。", "rest")), Is.True, driver.LastDailyModeResult);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(atPresentation, Is.EqualTo(DailyBehaviorMode.Rest));
            AssertActualReply("我喜欢画街头招牌。");
            var next = Begin("休息时聊聊你的画吧。");
            Assert.That(next.context.daily_behavior.current_mode, Is.EqualTo("rest"));
            Assert.That(next.context.daily_behavior.mode_revision, Is.EqualTo(daily.ModeRevision));
            Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(next.context.character), Is.EqualTo(persona));
            Assert.That(driver.TryConsumeResponse(Reply(next, "我爱画旧招牌的字。")), Is.True);
            AssertActualReply("我爱画旧招牌的字。"); Assert.That(outcomes, Is.Empty); Assert.That(world.WorldRevision, Is.Zero);
            yield return null;
        }
        [UnityTest] public IEnumerator ExpiredMode_PreservesBubbleAndActualOldState()
        {
            var request = Begin("休息一下，聊聊你的兴趣。");
            typeof(DailyBehaviorController).GetField("capturedAt", Fields).SetValue(daily, Time.unscaledTimeAsDouble - 31);
            Assert.That(driver.TryConsumeResponse(Reply(request, "我喜欢画街头招牌。", "rest")), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(driver.LastDailyModeResult, Is.EqualTo("stale_or_invalid_context")); AssertActualReply("我喜欢画街头招牌。");
            Assert.That(outcomes, Is.Empty); yield return null;
        }
        [UnityTest] public IEnumerator DangerousOriginalSpeech_CannotCreateWorldEffectsOrOutcomes()
        {
            string[] samples = { "我这就给你做拿铁，稍等两分钟。", "杯子我已经拿来了，咖啡也做好了。", "我喜欢画街头招牌。那杯我马上给你端过去。" };
            foreach (var text in samples)
            {
                var request = Begin("聊聊吧。");
                Assert.That(driver.TryConsumeResponse(Reply(request, text, daily.CurrentMode == DailyBehaviorMode.Work ? "rest" : null)), Is.True);
                AssertActualReply(text);
                Assert.That(runner.HasActiveTask || driver.HasPendingActionOutcome, Is.False);
                Assert.That(driver.LastValidatedGoalProposal, Is.Null);
                yield return null; Assert.That(outcomes, Is.Empty);
                Assert.That(world.WorldRevision, Is.Zero); Assert.That(world.GetDrinkCount(player.EntityId, "latte"), Is.Zero);
            }
        }
        [UnityTest] public IEnumerator Rest_ExplicitLegalDrinkAction_UsesOriginalWorldExecutor()
        {
            var mode = Begin("先休息一下吧。");
            Assert.That(driver.TryConsumeResponse(Reply(mode, "聊聊天也不错。", "rest")), Is.True);
            var request = Begin("请现在做一杯拿铁。");
            Assert.That(request.context.daily_behavior.current_mode, Is.EqualTo("rest"));
            var response = Reply(request, "我去制作拿铁。");
            response.decision.decision_kind = "ACTION_PROPOSAL";
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "prepare_drink", bindings = new Dictionary<string, string> { ["drink_type"] = "latte" } };
            response.validated_goal = new GoalProposal { type = "prepare_drink", drink_type = "latte" };
            Assert.That(driver.TryConsumeResponse(response), Is.True, driver.LastActionHandoffResult);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            double deadline = Time.realtimeSinceStartupAsDouble + 20;
            while (outcomes.Count == 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(outcomes.Count, Is.EqualTo(1)); Assert.That(outcomes[0].Status, Is.EqualTo(PlanExecutionStatus.Succeeded));
            Assert.That(outcomes[0].TraceId, Is.EqualTo(request.trace_id));
            Assert.That(world.GetDrinkCount(player.EntityId, "latte"), Is.EqualTo(1)); Assert.That(world.WorldRevision, Is.EqualTo(1));
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
        }
        [UnityTest] public IEnumerator HiddenConversation_LateModeReplyCannotCommitOrAppear()
        {
            var old = Begin("先歇歇吧。");
            var before = daily.CurrentMode; int revision = daily.ModeRevision;
            dialogue.HideDialoguePresentation();
            Assert.That(driver.TryConsumeResponse(Reply(old, "过期回答", "rest")), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(before)); Assert.That(daily.ModeRevision, Is.EqualTo(revision));
            Assert.That(driver.LastDailyModeResult, Is.EqualTo("none"));
            Assert.That(shown, Is.Empty); Assert.That(drafts, Is.Empty); Assert.That(outcomes, Is.Empty);
            yield return null;
        }
        [UnityTest] public IEnumerator ClosedConversation_CombinedModeCannotCommitOrStartAction()
        {
            var old = Begin("休息一下，再给我做杯拿铁。");
            var response = Reply(old, "我来处理。", "rest");
            response.decision.decision_kind = "ACTION_PROPOSAL";
            response.decision.action_proposal = new CognitiveActionProposal { action_id = "prepare_drink", bindings = new Dictionary<string, string> { ["drink_type"] = "latte" } };
            response.validated_goal = new GoalProposal { type = "prepare_drink", drink_type = "latte" };
            int revision = daily.ModeRevision; var before = daily.CurrentMode;
            dialogue.ClearSession();
            Assert.That(driver.TryConsumeResponse(response), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(before)); Assert.That(daily.ModeRevision, Is.EqualTo(revision));
            Assert.That(runner.HasActiveTask || driver.HasPendingActionOutcome, Is.False);
            Assert.That(world.WorldRevision, Is.Zero); Assert.That(outcomes, Is.Empty); Assert.That(shown, Is.Empty); Assert.That(drafts, Is.Empty);
            yield return null;
        }
        [UnityTest] public IEnumerator SwitchedNpc_LateModeCannotCommitOrAppear()
        {
            var old = Begin("先休息一下吧。"); int revision = daily.ModeRevision; var before = daily.CurrentMode;
            var other = Object.FindObjectsByType<WorldEntity>().Single(e => e.EntityId == "npc_cafe_regular_01");
            Call(fixture, "MovePlayer", other.transform.position + other.transform.forward * 1.3f);
            Assert.That(dialogue.SelectTarget(other.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(driver.TryConsumeResponse(Reply(old, "旧 NPC 的回答", "rest")), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(before)); Assert.That(daily.ModeRevision, Is.EqualTo(revision));
            Assert.That(dialogue.SelectedTarget.Entity, Is.SameAs(other)); Assert.That(shown, Is.Empty); Assert.That(drafts, Is.Empty);
            yield return null;
        }
        [UnityTest] public IEnumerator ClosedConversation_LateModeReplyCannotCommitOrAppear()
        {
            var old = Begin("先歇歇吧。"); dialogue.ClearSession();
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(driver.TryConsumeResponse(Reply(old, "过期回答", "rest")), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work)); Assert.That(shown, Is.Empty); Assert.That(drafts, Is.Empty);
            var fresh = Begin("刚才再聊一次。"); Assert.That(driver.TryConsumeResponse(Reply(fresh, "当前回答")), Is.True);
            AssertActualReply("当前回答"); yield return null;
        }
    }
}
