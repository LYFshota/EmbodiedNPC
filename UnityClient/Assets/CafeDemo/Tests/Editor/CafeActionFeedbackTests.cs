using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Authored V2, physical navigation/transactions, scripted decisions, all network lifecycles disabled before Play.</summary>
    public sealed class CafeActionFeedbackTests
    {
        private WorldEntity merchant, player, item;
        private GoalPlanRunner runner;
        private WorldStateService world;
        private CognitiveRunDriver driver;
        private DialogueController dialogue;
        private ActionFeedbackPresenter feedback;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        [UnitySetUp] public IEnumerator Load()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                var fields = new SerializedObject(network);
                fields.FindProperty("connectOnStart").boolValue = false;
                fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
                fields.ApplyModifiedPropertiesWithoutUndo(); network.enabled = false;
            }
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (component is ModelConfigClient || component is FirstRunModelSetup || component is CafeDemoDeveloperPanel ||
                    component is DialogueHistoryPanel || component is RuntimeTraceRecorder || component is CognitiveRunDriver ||
                    component is ServiceZoneDwellCognitionSource) component.enabled = false;
            foreach (var trigger in Object.FindObjectsByType<DailyBehaviorDemoTrigger>(FindObjectsInactive.Include))
                trigger.ConfigureDemo(false, 15, 35);
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("action-feedback-offline", "测试玩家", 1, DateTimeOffset.UnixEpoch);
            yield return new EnterPlayMode();
            AssertOffline();
            var entities = Object.FindObjectsByType<WorldEntity>();
            merchant = entities.Single(x => x.EntityId == "npc_cafe_01");
            player = entities.Single(x => x.EntityId == "player_01");
            item = entities.Single(x => x.EntityId == "cup_01");
            runner = merchant.GetComponent<GoalPlanRunner>(); world = Object.FindAnyObjectByType<WorldStateService>();
            driver = merchant.GetComponent<CognitiveRunDriver>(); dialogue = Object.FindAnyObjectByType<DialogueController>();
            feedback = Object.FindAnyObjectByType<ActionFeedbackPresenter>();
            Assert.That(feedback, Is.Not.Null, "The real authored UI must be wired, not created only inside the test.");
            dialogue.SetSetupReady(true);
            foreach (var npc in entities.Where(x => x.IsAgent(AgentRole.Npc)))
            {
                npc.GetComponent<AgentOrchestrator>()?.CancelCurrentRun();
                var bridge = npc.GetComponent<VisualObservationAgentEventBridge>();
                if (bridge != null) bridge.enabled = false;
            }
            foreach (var cameraController in Object.FindObjectsByType<CafeDemoOrbitCamera>()) cameraController.enabled = false;
            var camera = Camera.main;
            camera.transform.position = new Vector3(-1.4f, 1.9f, 2.7f);
            camera.transform.LookAt(merchant.transform.position + Vector3.up * 1.45f);
            yield return null;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (Application.isPlaying) { AssertOffline(); yield return new ExitPlayMode(); }
        }
        private static void AssertOffline()
        {
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include))
            {
                Assert.That(network.isActiveAndEnabled, Is.False);
                Assert.That(network.ConnectionAttempts, Is.Zero, "No real connection attempt is allowed.");
                Assert.That(network.IsConnected, Is.False);
                Assert.That(new SerializedObject(network).FindProperty("serverUrl").stringValue, Is.EqualTo("ws://127.0.0.1:0/ws"));
            }
            foreach (var component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (component is ModelConfigClient || component is FirstRunModelSetup || component is CafeDemoDeveloperPanel || component is DialogueHistoryPanel)
                    Assert.That(component.isActiveAndEnabled, Is.False);
        }
        private CognitiveResponse Propose(bool combined)
        {
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                item.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            merchant.GetComponent<VisionSensor>().ScanNow();
            var receiver = merchant.GetComponent<AgentEventReceiver>();
            receiver.Receive(new AgentEvent("action-feedback-" + Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerUtterance,
                player.EntityId, merchant.EntityId, "", new Dictionary<string,string> { [AgentEventPayloadKeys.Text] = "请工作并把杯子给我" },
                Time.realtimeSinceStartupAsDouble, world.WorldRevision));
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request, Is.Not.Null);
            driver.enabled = true; Assert.That(driver.TryOwnRequest(request), Is.True);
            return new CognitiveResponse { message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = merchant.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.ActionProposal,
                    target_daily_mode = combined ? "work" : null, utterance = "好呀，我这就帮你拿过来。",
                    action_proposal = new CognitiveActionProposal { action_id = "bring_object", bindings = new Dictionary<string,string> {
                        ["target"] = item.EntityId, ["destination"] = player.EntityId } } },
                validated_goal = new GoalProposal { type = "bring_object", target_entity_id = item.EntityId, destination_entity_id = player.EntityId } };
        }
        [UnityTest] public IEnumerator RealDelivery_UpdatesPhasesOnceAndPreservesCompletionBubble() => Delivery(false);
        [UnityTest] public IEnumerator CombinedWorkAndDelivery_PreservesDailyPriorityAndOutcome() => Delivery(true);
        private IEnumerator Delivery(bool combined)
        {
            if (combined)
            {
                Assert.That(merchant.GetComponent<DailyBehaviorDemoTrigger>().RequestMode(DailyBehaviorMode.Rest), Is.True);
                float until = Time.realtimeSinceStartup + 5;
                while (merchant.GetComponent<DailyBehaviorController>().CurrentMode != DailyBehaviorMode.Rest && Time.realtimeSinceStartup < until)
                    yield return null;
                Assert.That(merchant.GetComponent<DailyBehaviorController>().CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
                var rest = merchant.GetComponent<ReturnToHomeRoutine>();
                until = Time.realtimeSinceStartup + 25;
                while ((!rest.IsAtDailyDestination || rest.IsRunning || rest.OwnsPath || merchant.GetComponent<MotionChannelCoordinator>().IsNavigationActive) &&
                    Time.realtimeSinceStartup < until) yield return null;
                Assert.That(rest.IsAtDailyDestination, Is.True);
                Assert.That(rest.IsRunning || rest.OwnsPath, Is.False);
                // The EditMode enumerator drives frames directly; wait against the actual simulation clock.
                float readyAt = Time.unscaledTime + 1.2f;
                while (Time.unscaledTime < readyAt) yield return null;
            }
            int starts = 0, before = world.WorldRevision, utterances = 0;
            bool picked = false;
            world.SkillResultPublished += value => {
                if (value.ActorEntityId == merchant.EntityId && value.SkillType == "PickUp" && value.Status == SkillStatus.Succeeded)
                    picked = true;
            };
            var phases = new List<PlannerActionType>(); GoalOutcome? outcome = null;
            runner.TaskStarted += _ => starts++;
            runner.TaskPhaseChanged += value => { if (value.Progress.Action.HasValue) phases.Add(value.Progress.Action.Value); };
            runner.GoalOutcomePublished += value => outcome = value;
            driver.UtterancePresented += _ => utterances++;
            var proposed = Propose(combined);
            Assert.That(driver.TryConsumeResponse(proposed), Is.True, driver.LastActionHandoffResult + "/" + driver.LastDailyModeResult +
                "; channels=" + merchant.GetComponent<MotionChannelCoordinator>().OccupiedChannels +
                "; speaking=" + merchant.GetComponent<SpeechPresentationLifecycle>().IsSpeaking + "; time=" + Time.unscaledTime);
            Assert.That(feedback.ActiveTaskCount, Is.EqualTo(1));
            yield return null;
            if (!combined && Environment.GetEnvironmentVariable("EMBODIEDNPC_ACTION_FEEDBACK_VISUAL") == "1")
            {
                yield return Capture(1280, 720, "1280-task.png");
                yield return Capture(1920, 1080, "1920-task.png");
                yield return Capture(900, 1200, "narrow-task.png");
            }
            float deadline = Time.realtimeSinceStartup + 35;
            while (!outcome.HasValue && Time.realtimeSinceStartup < deadline)
            {
                picked |= item.GetComponent<HeldItemState>().HolderEntityId == merchant.EntityId;
                Assert.That(merchant.GetComponent<ReturnToHomeRoutine>().OwnsPath, Is.False);
                yield return null;
            }
            Assert.That(outcome.HasValue, Is.True);
            Assert.That(outcome.Value.Status, Is.EqualTo(PlanExecutionStatus.Succeeded), outcome.Value.FailureCode);
            Assert.That(starts, Is.EqualTo(1)); Assert.That(picked, Is.True);
            Assert.That(phases, Does.Contain(PlannerActionType.PickUp)); Assert.That(phases, Does.Contain(PlannerActionType.Give));
            Assert.That(phases.Count(x => x == PlannerActionType.WalkTo), Is.GreaterThanOrEqualTo(2));
            Assert.That(item.GetComponent<HeldItemState>().HolderEntityId, Is.EqualTo(player.EntityId));
            Assert.That(world.WorldRevision, Is.EqualTo(before + 2));
            yield return null;
            Assert.That(feedback.ActiveTaskCount, Is.Zero); Assert.That(runner.CurrentTaskFeedback, Is.Null);
            Assert.That(feedback.NotificationsShown, Is.EqualTo(1));
            if (!combined && Environment.GetEnvironmentVariable("EMBODIEDNPC_ACTION_FEEDBACK_VISUAL") == "1")
            {
                yield return Capture(1920, 1080, "1920-success.png");
            }
            Assert.That(feedback.LastResultText, Is.EqualTo(SettingsUiLanguage.IsEnglish ? "Item delivered" : "杯子已交付"));
            var messages = Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId);
            Assert.That(messages.Count(x => x.Text == "给你。"), Is.EqualTo(1));
            Assert.That(utterances, Is.EqualTo(1), "Acceptance emits cognition evidence; Outcome speech must not emit a second UtterancePresented.");
            typeof(GoalPlanRunner).GetMethod("PublishGoalOutcome", Private).Invoke(runner, new object[] { (GoalOutcome?)outcome.Value });
            feedback.enabled = false; feedback.enabled = true;
            yield return null;
            Assert.That(feedback.NotificationsShown, Is.EqualTo(1));
            Assert.That(messages.Count(x => x.Text == "给你。"), Is.EqualTo(1));
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            merchant.GetComponent<NpcDefaultRoutineController>().ReleaseEngagement();
            yield return null;
            Assert.That(merchant.GetComponent<DailyBehaviorController>().IsFormalTaskOccupied, Is.False);
            AssertOffline();
        }
        [UnityTest] public IEnumerator EscAndNpcSwitch_KeepOriginalTaskOwnershipThenDisableSettles()
        {
            var controller = player.GetComponent<CharacterController>();
            controller.enabled = false; player.transform.position = GameObject.Find("CounterFrontPoint").transform.position;
            controller.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(driver.TryConsumeResponse(Propose(false)), Is.True);
            var key = runner.CurrentTaskFeedback.Key;
            dialogue.HideDialoguePresentation();
            yield return null;
            Assert.That(runner.CurrentTaskFeedback.Key.Equals(key), Is.True);
            var camera = Camera.main; Quaternion facing = camera.transform.rotation;
            camera.transform.rotation = Quaternion.AngleAxis(180, Vector3.up) * facing;
            yield return null;
            Assert.That(feedback.VisibleLabelCount, Is.Zero, "A task behind the camera remains active but has no screen label.");
            Assert.That(feedback.ActiveTaskCount, Is.EqualTo(1));
            camera.transform.rotation = facing;
            var other = Object.FindObjectsByType<WorldEntity>().Single(x => x.EntityId == "npc_cafe_regular_01");
            controller.enabled = false; player.transform.position = other.transform.position + Vector3.right;
            controller.enabled = true; Physics.SyncTransforms();
            Assert.That(dialogue.SelectTarget(other.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(runner.CurrentTaskFeedback.Key.Equals(key), Is.True, "Switching UI must not reassign the original task.");
            feedback.enabled = false; feedback.enabled = true;
            Assert.That(feedback.ActiveTaskCount, Is.EqualTo(1));
            GoalOutcome? terminal = null; runner.GoalOutcomePublished += value => terminal = value;
            runner.enabled = false;
            yield return null;
            Assert.That(terminal.HasValue, Is.True);
            Assert.That(terminal.Value.Status, Is.EqualTo(PlanExecutionStatus.Cancelled));
            Assert.That(feedback.ActiveTaskCount, Is.Zero);
            Assert.That(feedback.LastResultText, Is.EqualTo(SettingsUiLanguage.IsEnglish ? "Task cancelled" : "任务已取消"));
            if (Environment.GetEnvironmentVariable("EMBODIEDNPC_ACTION_FEEDBACK_VISUAL") == "1")
                yield return Capture(1920, 1080, "1920-cancelled.png");
            AssertOffline();
        }
        private static IEnumerator Capture(int width, int height, string filename)
        {
            var assembly = typeof(Editor).Assembly;
            var sizes = assembly.GetType("UnityEditor.GameViewSizes");
            var singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizes).GetProperty("instance").GetValue(null);
            var group = sizes.GetMethod("GetGroup").Invoke(singleton, new object[] { 0 });
            var size = Activator.CreateInstance(assembly.GetType("UnityEditor.GameViewSize"),
                Enum.ToObject(assembly.GetType("UnityEditor.GameViewSizeType"), 1), width, height, "Action feedback temporary");
            int index = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            var gameType = assembly.GetType("UnityEditor.GameView");
            var window = EditorWindow.GetWindow(gameType);
            var property = gameType.GetProperty("selectedSizeIndex", BindingFlags.Public | Private);
            int previous = (int)property.GetValue(window);
            try
            {
                property.SetValue(window, index); window.Focus();
                for (int frame = 0; frame < 3; frame++) yield return null;
                Assert.That(Screen.width, Is.EqualTo(width)); Assert.That(Screen.height, Is.EqualTo(height));
                string directory = Path.Combine(Path.GetTempPath(), "embodiednpc-action-feedback-p0");
                Directory.CreateDirectory(directory);
                ScreenCapture.CaptureScreenshot(Path.Combine(directory, filename));
                for (int frame = 0; frame < 3; frame++) yield return null;
            }
            finally
            {
                property.SetValue(window, previous);
                group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { index });
            }
        }
    }
}
