using System;
using System.Collections.Generic;
using System.Reflection;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace EmbodiedNPC.Tests.Editor
{
    public sealed class ActionFeedbackTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root, uiRoot, playerRoot;
        private GoalPlanRunner runner;
        private ActionFeedbackPresenter presenter;
        private TaskFeedbackSnapshot accepted;
        [SetUp] public void SetUp()
        {
            root = new GameObject("Feedback actor");
            root.SetActive(false);
            root.AddComponent<WorldEntity>().Configure("npc_feedback", "test.npc", EntityKind.Agent, AgentRole.Npc);
            runner = root.AddComponent<GoalPlanRunner>();
            root.SetActive(true);
            playerRoot = new GameObject("Feedback player");
            var player = playerRoot.AddComponent<WorldEntity>();
            player.Configure("player_feedback", "test.player", EntityKind.Agent, AgentRole.Player);
            uiRoot = new GameObject("Feedback UI", typeof(RectTransform));
            presenter = uiRoot.AddComponent<ActionFeedbackPresenter>();
            presenter.Configure(null, null, new[] { runner }, player, null, null, null);
            accepted = Snapshot("proposal_one", 1);
        }
        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(uiRoot);
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(playerRoot);
        }
        private TaskFeedbackSnapshot Snapshot(string proposal, long sequence, PlanActionProgress? progress = null) =>
            new(new TaskFeedbackKey("npc_feedback", proposal, 0, "trace_feedback"), "bring_object", "item_any",
                "player_feedback", "player_feedback", 3, sequence,
                progress ?? new PlanActionProgress(PlanProgressKind.Accepted, "", 0, 0), "cafe.cup.coffee");
        private GoalOutcome Outcome(TaskFeedbackSnapshot snapshot, PlanExecutionStatus status) => new(
            "outcome_" + snapshot.Key.Proposal + "_" + status, snapshot.Key.Proposal, snapshot.Key.Revision,
            snapshot.Key.Trace, "bring_object", snapshot.Key.Actor, snapshot.Target, snapshot.Destination,
            status, "", 5, 0);
        private void AcceptInRunner()
        {
            Set("actor", root.GetComponent<WorldEntity>());
            Set("activeGoal", new GoalProposal { type = "bring_object", target_entity_id = "item_any", destination_entity_id = "player_feedback" });
            Set("activeProposalId", accepted.Key.Proposal); Set("activeTraceId", accepted.Key.Trace);
            Set("feedbackPlayerId", accepted.Player);
            typeof(GoalPlanRunner).GetMethod("BeginTaskFeedback", Private).Invoke(runner,
                new object[] { new PlanActionProgress(PlanProgressKind.Rediscovering, "", 0, 0) });
        }
        private void Set(string field, object value) => typeof(GoalPlanRunner).GetField(field, Private).SetValue(runner, value);

        [Test] public void RunnerDisable_SettlesOnceWithCachedActorAndClearsSnapshot()
        {
            int starts = 0, outcomes = 0; GoalOutcome terminal = default;
            runner.TaskStarted += _ => starts++;
            runner.GoalOutcomePublished += value => { outcomes++; terminal = value; };
            AcceptInRunner();
            Set("actor", null); // The Unity actor reference can already be unavailable during teardown.
            // EditMode does not drive normal MonoBehaviour callbacks; the scene test covers real disable.
            typeof(GoalPlanRunner).GetMethod("OnDisable", Private).Invoke(runner, null);
            runner.CancelCurrentPlan();
            Assert.That(starts, Is.EqualTo(1)); Assert.That(outcomes, Is.EqualTo(1));
            Assert.That(terminal.ActorEntityId, Is.EqualTo("npc_feedback"));
            Assert.That(terminal.Status, Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(terminal.FailureCode, Is.EqualTo("RUNNER_DISABLED"));
            Assert.That(runner.CurrentTaskFeedback, Is.Null);
            Assert.That(presenter.ActiveTaskCount, Is.Zero);
        }
        [Test] public void AcceptedSearch_ReAdmissionFailurePublishesOneFailedOutcome()
        {
            int count = 0; GoalOutcome terminal = default;
            runner.GoalOutcomePublished += value => { count++; terminal = value; };
            AcceptInRunner();
            Set("goalValidator", null);
            var proposal = new GoalProposal { type = "bring_object", target_entity_id = "item_any", destination_entity_id = "player_feedback" };
            bool result = (bool)typeof(GoalPlanRunner).GetMethod("BeginValidatedProposal", Private).Invoke(runner,
                new object[] { proposal, accepted.Key.Trace, accepted.Key.Proposal, 0 });
            Assert.That(result, Is.False); Assert.That(count, Is.EqualTo(1));
            Assert.That(terminal.Status, Is.EqualTo(PlanExecutionStatus.Failed));
            Assert.That(terminal.ProposalId, Is.EqualTo(accepted.Key.Proposal));
            Assert.That(runner.CurrentTaskFeedback, Is.Null);
        }
        [Test] public void NeverAcceptedRejection_DoesNotStartOrPublishOutcome()
        {
            Set("goalValidator", null);
            int starts = 0, outcomes = 0;
            runner.TaskStarted += _ => starts++; runner.GoalOutcomePublished += _ => outcomes++;
            LogAssert.Expect(LogType.Warning, "[GoalPlan] REJECTED code=GoalMissing");
            Assert.That(runner.TrySubmitGoal(new GoalProposal { type = "bring_object", target_entity_id = "missing" },
                "trace", "request", 0, 0), Is.False);
            Assert.That(starts, Is.Zero); Assert.That(outcomes, Is.Zero);
        }
        [TestCase(PlanExecutionStatus.Succeeded, "杯子已交付")]
        [TestCase(PlanExecutionStatus.Failed, "任务未完成")]
        [TestCase(PlanExecutionStatus.Cancelled, "任务已取消")]
        [TestCase(PlanExecutionStatus.TimedOut, "任务超时")]
        public void TerminalResults_AreTypedAndConsumedOnce(PlanExecutionStatus status, string chinese)
        {
            presenter.ObserveStarted(accepted);
            var outcome = Outcome(accepted, status);
            presenter.ObserveOutcome(outcome); presenter.ObserveOutcome(outcome);
            Assert.That(presenter.PendingNotificationCount, Is.EqualTo(1));
            Assert.That(presenter.ActiveTaskCount, Is.Zero);
            Assert.That(presenter.LastResultText, Is.EqualTo(EmbodiedNPC.Settings.SettingsUiLanguage.IsEnglish
                ? ActionFeedbackPresenter.FormatResult(outcome, true) : chinese));
            presenter.enabled = false; presenter.enabled = true;
            presenter.ObserveOutcome(outcome);
            Assert.That(presenter.ActiveTaskCount, Is.Zero);
        }
        [Test] public void OldPhaseAndOutcome_CannotReplaceOrClearNewTask()
        {
            presenter.ObserveStarted(accepted);
            var next = Snapshot("proposal_two", 10);
            presenter.ObserveStarted(next);
            presenter.ObservePhase(Snapshot("proposal_one", 11));
            presenter.ObserveOutcome(Outcome(accepted, PlanExecutionStatus.Succeeded));
            Assert.That(presenter.ActiveTaskCount, Is.EqualTo(1));
            Assert.That(presenter.LastResultText, Is.Empty);
            presenter.ObserveOutcome(Outcome(next, PlanExecutionStatus.Cancelled));
            Assert.That(presenter.ActiveTaskCount, Is.Zero);
        }
        [Test] public void WrongActorOrPlayer_DoesNotNotifyLocalPlayer()
        {
            var other = new TaskFeedbackSnapshot(accepted.Key, "bring_object", "item_any", "other_player",
                "other_player", 0, 1, accepted.Progress);
            presenter.ObserveStarted(other); presenter.ObserveOutcome(Outcome(other, PlanExecutionStatus.Succeeded));
            Assert.That(presenter.LastResultText, Is.Empty);
            Assert.That(presenter.ActiveTaskCount, Is.Zero);
        }
        [Test] public void ActualBindings_NotActionIndex_DetermineDeliveryPhase()
        {
            var progress = new PlanActionProgress(PlanProgressKind.Action, "replanned", 0, 2,
                PlannerActionType.WalkTo, new Dictionary<string,string> { ["destination"] = "player_feedback" });
            string label = ActionFeedbackPresenter.FormatPhase(Snapshot("proposal_any", 1, progress));
            Assert.That(label, Does.Contain(EmbodiedNPC.Settings.SettingsUiLanguage.IsEnglish ? "Delivering" : "递送杯子"));
        }
        [Test] public void TwoNpcFeedbackScopes_DoNotOverwriteEachOther()
        {
            var other = new GameObject("Second feedback actor"); other.SetActive(false);
            try
            {
                other.AddComponent<WorldEntity>().Configure("npc_feedback_other", "test.other", EntityKind.Agent, AgentRole.Npc);
                var otherRunner = other.AddComponent<GoalPlanRunner>();
                presenter.Configure(null, null, new[] { runner, otherRunner }, playerRoot.GetComponent<WorldEntity>(), null, null, null);
                var snapshot = new TaskFeedbackSnapshot(new TaskFeedbackKey("npc_feedback_other", "proposal_one", 0, "trace_feedback"),
                    "bring_object", "item_any", "player_feedback", "player_feedback", 0, 1, accepted.Progress);
                presenter.ObserveStarted(accepted); presenter.ObserveStarted(snapshot);
                Assert.That(presenter.ActiveTaskCount, Is.EqualTo(2));
                presenter.ObserveOutcome(Outcome(accepted, PlanExecutionStatus.Succeeded));
                Assert.That(presenter.ActiveTaskCount, Is.EqualTo(1));
                Assert.That(presenter.PhaseText("npc_feedback_other"), Is.Not.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }
        [Test] public void LateSubscription_ReadsRunnerSnapshotWithoutReplayingResults()
        {
            AcceptInRunner();
            presenter.Configure(null, null, new[] { runner }, playerRoot.GetComponent<WorldEntity>(), null, null, null);
            Assert.That(presenter.ActiveTaskCount, Is.EqualTo(1));
            Assert.That(presenter.PendingNotificationCount, Is.Zero);
            Assert.That(presenter.NotificationsShown, Is.Zero);
        }
        [TestCase(1280,720)][TestCase(1920,1080)]
        public void SavedToastSharesExistingCapacityLayoutFadeAndExpiry(int width,int height)
        {
            ((RectTransform)uiRoot.transform).sizeDelta=new Vector2(width,height);
            var template=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/UI/ActionFeedbackItem.prefab").GetComponent<ActionFeedbackView>();
            typeof(ActionFeedbackPresenter).GetField("template",Private).SetValue(presenter,template);
            var notifications=(System.Collections.IList)typeof(ActionFeedbackPresenter).GetField("toasts",Private).GetValue(presenter);
            var layout=typeof(ActionFeedbackPresenter).GetMethod("LayoutToasts",Private);
            try
            {
                for(int i=0;i<6;i++)presenter.ObserveTaskSaved(root.GetComponent<WorldEntity>(),playerRoot.GetComponent<WorldEntity>());
                Assert.That(presenter.PendingNotificationCount,Is.EqualTo(4));
                layout.Invoke(presenter,null);Assert.That(presenter.NotificationsShown,Is.EqualTo(2));
                foreach(var view in uiRoot.GetComponentsInChildren<ActionFeedbackView>())
                {
                    var layer=((RectTransform)uiRoot.transform).rect;var size=view.Rect.sizeDelta*view.transform.localScale.x;
                    var rect=new Rect(view.Rect.anchoredPosition-size/2,size);
                    Assert.That(rect.xMax,Is.LessThanOrEqualTo(layer.xMax-23));
                    Assert.That(rect.yMax,Is.LessThanOrEqualTo(layer.yMax-89));
                    Assert.That(rect.yMin,Is.GreaterThan(layer.yMin));
                }
                foreach(var item in notifications)item.GetType().GetField("Created").SetValue(item,Time.unscaledTimeAsDouble-2.6);
                layout.Invoke(presenter,null);
                foreach(var view in uiRoot.GetComponentsInChildren<ActionFeedbackView>())Assert.That(view.GetComponent<CanvasGroup>().alpha,Is.InRange(.01f,.99f));
                // EditMode owns temporary objects; the runtime expiry branch is checked with no live Views.
                foreach(var view in uiRoot.GetComponentsInChildren<ActionFeedbackView>(true))UnityEngine.Object.DestroyImmediate(view.gameObject);
                foreach(var item in notifications)item.GetType().GetField("Created").SetValue(item,Time.unscaledTimeAsDouble-3.0);
                layout.Invoke(presenter,null);Assert.That(presenter.PendingNotificationCount,Is.Zero);
            }
            finally {foreach(var view in uiRoot.GetComponentsInChildren<ActionFeedbackView>(true))UnityEngine.Object.DestroyImmediate(view.gameObject);}
        }
        [Test] public void DestroyedNpcDuringHiddenUi_DoesNotRetainAnActiveLabel()
        {
            presenter.ObserveStarted(accepted);
            UnityEngine.Object.DestroyImmediate(root);
            presenter.Configure(null, null, new GoalPlanRunner[] { null }, playerRoot.GetComponent<WorldEntity>(), null, null, null);
            Assert.That(presenter.ActiveTaskCount, Is.Zero);
            Assert.That(presenter.VisibleLabelCount, Is.Zero);
        }
        [TestCase("bring_object")][TestCase("prepare_drink")]
        public void ScreenOverlayTaskLabelsStayVisibleBehindCharacters(string action)
        {
            var cameraRoot=new GameObject("isolated-feedback-camera");
            var blocker=new GameObject("isolated-occluding-character");
            try
            {
                root.transform.position=new Vector3(1000,1000,1000);
                var anchorPoint=new GameObject("isolated-feedback-anchor").transform;
                anchorPoint.SetParent(root.transform,false);anchorPoint.localPosition=Vector3.up*1.5f;
                var anchor=root.AddComponent<DialogueBubbleAnchor>();anchor.Configure(root.GetComponent<WorldEntity>(),anchorPoint,default);
                var camera=cameraRoot.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=4;
                camera.transform.position=anchorPoint.position-Vector3.forward*6;camera.transform.LookAt(anchorPoint);
                blocker.transform.position=anchorPoint.position-Vector3.forward*3;
                var other=blocker.AddComponent<WorldEntity>();other.Configure("occluding_npc","test.npc",EntityKind.Agent,AgentRole.Npc);
                blocker.AddComponent<DialogueBubbleAnchor>().Configure(other,blocker.transform,default);
                blocker.AddComponent<SphereCollider>().radius=.8f;Physics.SyncTransforms();
                Assert.That(Physics.Raycast(camera.transform.position,camera.transform.forward,6),Is.True,"A real world collider is between the camera and NPC.");
                var canvas=uiRoot.AddComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;
                var template=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/UI/ActionFeedbackItem.prefab").GetComponent<ActionFeedbackView>();
                presenter.Configure(canvas,camera,new[]{runner},playerRoot.GetComponent<WorldEntity>(),null,null,template);
                Canvas.ForceUpdateCanvases();
                presenter.ObserveStarted(new TaskFeedbackSnapshot(accepted.Key,action,accepted.Target,accepted.Destination,accepted.Player,
                    3,1,accepted.Progress,drinkType:"latte"));
                var refresh=typeof(ActionFeedbackPresenter).GetMethod("LateUpdate",Private);refresh.Invoke(presenter,null);
                Assert.That(presenter.VisibleLabelCount,Is.EqualTo(1),"Characters must not hide the screen overlay.");
                Assert.That(presenter.ActiveTaskCount,Is.EqualTo(1));
                camera.transform.rotation=Quaternion.Euler(0,180,0);refresh.Invoke(presenter,null);
                Assert.That(presenter.VisibleLabelCount,Is.Zero,"Behind-camera labels remain hidden.");
                Assert.That(presenter.ActiveTaskCount,Is.EqualTo(1),"UI visibility never cancels the task.");
            }
            finally
            {
                foreach(var view in uiRoot.GetComponentsInChildren<ActionFeedbackView>(true))UnityEngine.Object.DestroyImmediate(view.gameObject);
                UnityEngine.Object.DestroyImmediate(blocker);UnityEngine.Object.DestroyImmediate(cameraRoot);
            }
        }
        [Test] public void FeedbackSubscriberException_DoesNotPreventAcceptanceOrCancellation()
        {
            runner.TaskStarted += _ => throw new InvalidOperationException();
            LogAssert.Expect(LogType.Warning, "[GoalPlan] Feedback observer failed: InvalidOperationException");
            AcceptInRunner();
            Assert.That(runner.CurrentTaskFeedback, Is.Not.Null);
            Assert.That(runner.InterruptCurrentPlan("CANCELLED_BY_REQUEST"), Is.True);
            Assert.That(runner.CurrentTaskFeedback, Is.Null);
        }
    }
}
