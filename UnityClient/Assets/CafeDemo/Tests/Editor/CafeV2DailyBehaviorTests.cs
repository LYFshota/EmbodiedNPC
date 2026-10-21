using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    // In-memory transport only. Overrides every connection/send entry used by this fixture.
    public sealed class ServiceZoneFakeBackend : BackendWebSocketClient
    {
        public readonly List<CognitiveRequest> Requests = new();
        public readonly List<CognitiveRequest> Cancels = new();
        public TaskCompletionSource<bool> BlockServiceSend;
        public override bool IsConnected => isActiveAndEnabled;
        public override bool EnsureConnected() => false;
        public override Task<bool> SendCognitiveRequestAsync(CognitiveRequest request)
        {
            Requests.Add(request);
            return request.context.current_event.event_type == AgentEventTypes.PlayerDwelledInServiceZone && BlockServiceSend != null
                ? BlockServiceSend.Task : Task.FromResult(true);
        }
        public override Task<bool> SendCognitiveCancelAsync(CognitiveRequest request)
        { Cancels.Add(request); return Task.FromResult(true); }
        public override Task<bool> SendExperienceAppendRequestAsync(ExperienceAppendRequest request) => Task.FromResult(false);
        public CognitiveResponse Response(CognitiveRequest request, string kind = "DAILY_MODE_INTENT") => new() {
            message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
            trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = request.run.agent_entity_id,
            decision = new CognitiveDecision { decision_kind = kind, target_daily_mode = kind == "DAILY_MODE_INTENT" ? "work" : null } };
        public bool Reply(CognitiveResponse response) => ProcessIncomingJson(Newtonsoft.Json.JsonConvert.SerializeObject(response));
    }
    /// <summary>Offline V2 cases: actual scene/agent/runners, no provider or database.</summary>
    public sealed class CafeV2DailyBehaviorTests : PlayerPresentationFixture
    {
        protected override bool UsesStartupPanel => false;
        protected override bool DisableDailyDemo => false;
        private DailyBehaviorController daily;
        private DailyBehaviorDemoTrigger source;
        private ReturnToHomeRoutine routine;
        private NpcDefaultRoutineController admission;
        private AgentOrchestrator orchestrator;
        private AgentEventReceiver receiver;
        private DialogueController dialogue;
        private NavMeshAgent agent;

        private void Prepare(bool keepSceneTiming = false)
        {
            AssertOffline();
            ControlledPerception();
            foreach (var entity in Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None))
            {
                if (!entity.IsAgent(AgentRole.Npc)) continue;
                var visual = entity.GetComponent<VisualObservationAgentEventBridge>(); if (visual != null) visual.enabled = false;
                var vision = entity.GetComponent<VisionSensor>(); if (vision != null) vision.enabled = false;
                var run = entity.GetComponent<AgentOrchestrator>(); if (run != null) run.CancelCurrentRun();
                var driver = entity.GetComponent<CognitiveRunDriver>(); if (driver != null) driver.enabled = false;
            }
            foreach (var trace in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None)) trace.enabled = false;
            daily = merchant.GetComponent<DailyBehaviorController>(); source = merchant.GetComponent<DailyBehaviorDemoTrigger>();
            routine = merchant.GetComponent<ReturnToHomeRoutine>(); admission = merchant.GetComponent<NpcDefaultRoutineController>();
            orchestrator = merchant.GetComponent<AgentOrchestrator>(); receiver = merchant.GetComponent<AgentEventReceiver>();
            dialogue = Object.FindAnyObjectByType<DialogueController>(); agent = merchant.GetComponent<NavMeshAgent>();
            if (!keepSceneTiming) source.ConfigureDemo(false, 15, 120);
            dialogue.SetSetupReady(true); dialogue.ClearSession("daily_test"); admission.ReleaseEngagement();
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
        }
        private IEnumerator PrepareLocalSettings()
        {
            var form = Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include);
            Assert.That(form.enabled, Is.False, "Building existing UI must not enable its network lifecycle.");
            // Only a caption protocol is needed by the layout. Never load official config or credentials.
            typeof(FirstRunModelSetup).GetField("mainChoices", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(form, new[] { new ModelPresetView { preset_id = "offline-layout", protocol = "openai_compatible" } });
            foreach (string method in new[] { "EnsureControls", "EnsureProfileControls", "EnsureCompactLayout" })
                typeof(FirstRunModelSetup).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            // Open only the existing local hierarchy, keeping all model/developer network components disabled.
            form.gameObject.SetActive(true); form.AdvancedSettingsRoot.gameObject.SetActive(true);
            yield return Wait(.3f);
            Assert.That(Object.FindAnyObjectByType<CafeDailyBehaviorSettings>().ModeDropdown, Is.Not.Null);
            AssertOffline();
        }
        private static void AssertOffline()
        {
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (network is ServiceZoneFakeBackend)
                { Assert.That(network.ConnectionAttempts, Is.Zero); continue; }
                Assert.That(network.isActiveAndEnabled || network.IsConnected, Is.False);
                Assert.That(network.ConnectionAttempts, Is.Zero);
                Assert.That(network.CognitiveRequestSendCount, Is.Zero);
                Assert.That(new UnityEditor.SerializedObject(network).FindProperty("serverUrl").stringValue,
                    Is.EqualTo("ws://127.0.0.1:0/ws"));
            }
            foreach (var config in Object.FindObjectsByType<ModelConfigClient>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Assert.That(config.isActiveAndEnabled, Is.False);
            foreach (var form in Object.FindObjectsByType<FirstRunModelSetup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Assert.That(form.isActiveAndEnabled, Is.False);
            foreach (var developer in Object.FindObjectsByType<CafeDemoDeveloperPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Assert.That(developer.isActiveAndEnabled, Is.False);
            foreach (var history in Object.FindObjectsByType<DialogueHistoryPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Assert.That(history.isActiveAndEnabled, Is.False);
        }
        private static string ReadDebug(WorldEntity target)
        {
            var panel = Object.FindAnyObjectByType<CafeNpcDebugPanel>(FindObjectsInactive.Include);
            // Select at the existing read-only selection boundary without opening network-backed settings UI.
            typeof(CafeNpcDebugPanel).GetMethod("OnSelectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(panel, new object[] { target.GetComponent<CognitiveContextComposer>() });
            panel.RefreshNow(); return panel.CurrentSelectedText;
        }
        private IEnumerator WaitMode(DailyBehaviorMode mode)
        {
            float deadline = Time.realtimeSinceStartup + 6;
            while (daily.CurrentMode != mode && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(mode), daily.LastTransitionReason);
        }
        private IEnumerator WaitAtDestination()
        {
            bool walked = false;
            float deadline = Time.realtimeSinceStartup + 22;
            while ((!routine.IsAtDailyDestination || routine.IsRunning) && Time.realtimeSinceStartup < deadline)
            { walked |= agent.velocity.magnitude > .1f; yield return null; }
            Assert.That(walked, Is.True, "Must navigate rather than teleport.");
            Assert.That(routine.IsAtDailyDestination, Is.True);
            Assert.That(routine.OwnsPath || routine.IsRunning, Is.False);
            Assert.That(merchant.GetComponentInChildren<LocomotionPresenter>().PlanarSpeed, Is.LessThan(.1f));
        }

        [UnityTest] public IEnumerator FourNpcDefaults_KeepSeatAndExistingBehaviorMode()
        {
            Prepare();
            var npcs = Object.FindObjectsByType<DailyBehaviorController>(FindObjectsSortMode.None);
            Assert.That(npcs.Length, Is.EqualTo(4));
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(daily.Profile.IsValid, Is.True);
            Assert.That(source.isActiveAndEnabled && routine.isActiveAndEnabled, Is.True);
            var seats = npcs.Where(x => x != daily).ToArray();
            var positions = seats.Select(x => x.transform.position).ToArray();
            yield return Wait(.4f);
            for (int i = 0; i < seats.Length; i++)
            {
                Assert.That(seats[i].CurrentMode, Is.EqualTo(DailyBehaviorMode.Leisure));
                Assert.That(seats[i].Profile.Allows(DailyBehaviorMode.Rest), Is.False);
                Assert.That(seats[i].GetComponent<NavMeshAgent>(), Is.Null);
                Assert.That(seats[i].transform.position, Is.EqualTo(positions[i]));
                Assert.That(seats[i].GetComponentInChildren<AttentionController>().isActiveAndEnabled, Is.True);
            }
            Assert.That(merchant.GetComponent<CurrentBehaviorMode>().CurrentModeId, Is.EqualTo("working"));
        }

        [UnityTest] public IEnumerator TrustedEvents_RejectForgeryDuplicatesAndWrongNpc()
        {
            Prepare(); int revision = world.WorldRevision;
            int requests = Object.FindAnyObjectByType<BackendWebSocketClient>().CognitiveRequestSendCount;
            Assert.That(source.BeginBreak(), Is.True); Assert.That(source.BeginBreak(), Is.False);
            yield return WaitMode(DailyBehaviorMode.Rest);
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
            var original = source.LastEvent;
            receiver.Receive(original);
            receiver.Receive(new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.WorkBreakEnded,
                merchant.EntityId, merchant.EntityId, "", null, Time.unscaledTimeAsDouble));
            customer.GetComponent<AgentEventReceiver>().Receive(original);
            yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
            Assert.That(customer.GetComponent<DailyBehaviorController>().CurrentMode, Is.EqualTo(DailyBehaviorMode.Leisure));
            Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(source.PublishedEventCount, Is.EqualTo(1));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(Object.FindAnyObjectByType<BackendWebSocketClient>().CognitiveRequestSendCount, Is.EqualTo(requests));
        }

        [UnityTest] public IEnumerator BusyAndExpiredConditions_RecheckBeforeSwitching()
        {
            Prepare(); var channels = merchant.GetComponent<MotionChannelCoordinator>();
            channels.BeginTaskMotion(); source.ConfigureDemo(false, 15, .25f);
            Assert.That(source.BeginBreak(), Is.True);
            yield return Wait(.5f);
            Assert.That(source.BreakRequested, Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            channels.EndTaskMotion(); yield return Wait(1.2f);
            Assert.That(daily.ModeRevision, Is.EqualTo(0), "Expired start must never replay.");
            source.ConfigureDemo(false, 15, 120); channels.BeginTaskMotion();
            Assert.That(source.BeginBreak(), Is.True); yield return Wait(.2f);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(daily.HasPendingTransition, Is.True);
            channels.EndTaskMotion(); yield return WaitMode(DailyBehaviorMode.Rest);
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
        }

        [UnityTest] public IEnumerator V2Navigation_DialogueSuspendsAndResumesThenReturnsToWork()
        {
            Prepare(); int revision = world.WorldRevision;
            yield return PrepareLocalSettings();
            var settings = Object.FindAnyObjectByType<CafeDailyBehaviorSettings>();
            settings.ModeDropdown.value = 1; yield return WaitMode(DailyBehaviorMode.Rest);
            yield return Wait(.5f);
            Assert.That(routine.OwnsPath, Is.True);
            Place(merchant.transform.position + new Vector3(0, 0, -1.4f));
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return Wait(.3f);
            Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.IsActivitySuspended, Is.True);
            dialogue.HideDialoguePresentation(); admission.ReleaseEngagement();
            Place(new Vector3(-5, .03f, -1));
            yield return WaitAtDestination(); yield return Wait(.3f);
            var attention = merchant.GetComponentInChildren<AttentionController>();
            Assert.That(attention.Source, Is.EqualTo(AttentionSource.DailyRest));
            Assert.That(daily.IsRestingAtWindow, Is.True);
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.WindowIdle));
            var look = GameObject.Find("DailyBehaviorAnchors/WindowLookPoint").transform;
            var direction = Vector3.ProjectOnPlane(look.position - merchant.transform.position, Vector3.up);
            Assert.That(Vector3.Angle(merchant.transform.forward, direction), Is.LessThan(2f));
            Assert.That(Vector3.Angle(merchant.GetComponentInChildren<FacingController>().Visual.forward, direction), Is.LessThan(2f));
            var origin = merchant.transform.position + Vector3.up * 1.55f;
            var hits = Physics.RaycastAll(origin, (look.position - origin).normalized,
                Vector3.Distance(origin, look.position), -1, QueryTriggerInteraction.Ignore);
            Assert.That(hits.Any(x => x.collider.name.StartsWith("WindowReturn_")), Is.False,
                "Rest gaze must pass through the window opening, not its wooden side wall.");
            Assert.That(merchant.GetComponentInChildren<HeadGazeController>().CurrentWeight, Is.GreaterThan(.1f));
            int starts = routine.NavigationStartCount;
            Vector3 position = merchant.transform.position; yield return Wait(2.2f);
            Assert.That(Vector3.Distance(position, merchant.transform.position), Is.LessThan(.03f));
            Assert.That(agent.hasPath, Is.False, "No repeated seeking at the window.");
            Assert.That(routine.NavigationStartCount, Is.EqualTo(starts));
            settings.ModeDropdown.value = 0;
            yield return WaitMode(DailyBehaviorMode.Work); yield return WaitAtDestination();
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.HomeIdle));
            Assert.That(daily.ModeRevision, Is.EqualTo(2)); Assert.That(source.PublishedEventCount, Is.EqualTo(2));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(attention.Source, Is.Not.EqualTo(AttentionSource.DailyRest));
            AssertOffline();
        }

        [UnityTest] public IEnumerator DebugPanel_ShowsIndependentCognitiveAndDailyModes()
        {
            Prepare(); bool english = SettingsUiLanguage.IsEnglish;
            SettingsUiLanguage.SetEnglish(true);
            try
            {
                Assert.That(ReadDebug(merchant), Does.Contain("Cognitive Behavior Mode: working"));
                Assert.That(ReadDebug(merchant), Does.Contain("Daily Behavior Mode: Work"));
                Assert.That(source.BeginBreak(), Is.True); yield return WaitMode(DailyBehaviorMode.Rest);
                string resting = ReadDebug(merchant);
                Assert.That(resting, Does.Contain("Cognitive Behavior Mode: working"));
                Assert.That(resting, Does.Contain("Daily Behavior Mode: Rest"));
                Assert.That(ReadDebug(customer), Does.Contain("Daily Behavior Mode: Leisure"));
                SettingsUiLanguage.SetEnglish(false);
                Assert.That(ReadDebug(merchant), Does.Contain("认知行为模式：working"));
                Assert.That(ReadDebug(merchant), Does.Contain("生活行为模式：Rest"));
                AssertOffline();
            }
            finally { SettingsUiLanguage.SetEnglish(english); }
        }

        [UnityTest] public IEnumerator AutomaticBreak_TimingAndCognitiveBusyResumeToHome()
        {
            Prepare(); Place(new Vector3(-5, .03f, -1));
            double startedAt = (double)typeof(DailyBehaviorDemoTrigger)
                .GetField("sceneStartedAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
            source.ConfigureDemo(true, 15, 35);
            while (Time.unscaledTimeAsDouble - startedAt < 14.5) yield return null;
            Assert.That(source.PublishedEventCount, Is.Zero);
            float deadline = Time.realtimeSinceStartup + 3;
            while (!source.BreakRequested && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(source.BreakRequested, Is.True);
            double breakAt = source.LastEvent.OccurredAt;
            Assert.That(breakAt - startedAt, Is.InRange(15d, 15.5d));
            yield return WaitMode(DailyBehaviorMode.Rest); yield return WaitAtDestination();
            Assert.That(routine.DailyDestination, Is.EqualTo(daily.WindowRestPoint.position));
            Assert.That(merchant.GetComponent<CurrentBehaviorMode>().CurrentModeId, Is.EqualTo("working"));

            // Synthetic emote run: visual presence now records only; keep testing busy-run recovery.
            receiver.Receive(new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerEmote,
                player.GetComponent<WorldEntity>().EntityId, merchant.EntityId, "", null, Time.unscaledTimeAsDouble));
            Assert.That(orchestrator.HasActiveRun, Is.True);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            Assert.That(driver.TryOwnRequest(request), Is.True);
            Assert.That(driver.CurrentTaskEventType, Is.EqualTo(AgentEventTypes.PlayerEmote));
            while (source.BreakRequested && Time.unscaledTimeAsDouble - breakAt < 37) yield return null;
            Assert.That(source.BreakRequested, Is.False);
            Assert.That(source.LastEvent.OccurredAt - breakAt, Is.InRange(35d, 35.5d));
            Assert.That(source.PublishedEventCount, Is.EqualTo(2));
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.HasPendingTransition, Is.True);
            Assert.That(routine.OwnsPath, Is.False);
            Assert.That(driver.TryConsumeResponse(new CognitiveResponse {
                message_type = "cognitive_response", schema_version = "1.0",
                request_id = request.request_id, trace_id = request.trace_id, run_id = request.run.run_id,
                agent_entity_id = merchant.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.NoAction }
            }), Is.True);
            Assert.That(orchestrator.HasActiveRun, Is.False);
            yield return WaitMode(DailyBehaviorMode.Work); yield return WaitAtDestination();
            Assert.That(routine.DailyDestination, Is.EqualTo(routine.HomePosition));
            Assert.That(daily.ModeRevision, Is.EqualTo(2));
            Assert.That(source.PublishedEventCount, Is.EqualTo(2));
            AssertOffline();
        }

        [UnityTest] public IEnumerator ActualBringObject_ProtectsTaskAndHolderThenAllowsRest()
        {
            Prepare();
            yield return PrepareLocalSettings();
            var settings = Object.FindAnyObjectByType<CafeDailyBehaviorSettings>();
            var cup = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "cup_01");
            var held = cup.GetComponent<HeldItemState>();
            var playerEntity = player.GetComponent<WorldEntity>();
            var runner = merchant.GetComponent<GoalPlanRunner>(); int revision = world.WorldRevision;
            Place(new Vector3(-2.2f, .03f, -.8f));
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            var vision = merchant.GetComponent<VisionSensor>(); vision.enabled = true; vision.ScanNow();
            Assert.That(vision.CurrentVisibleEntityIds, Does.Contain(cup.EntityId));
            receiver.Receive(new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerUtterance,
                playerEntity.EntityId, merchant.EntityId, "", new Dictionary<string, string> { [AgentEventPayloadKeys.Text] = "拿杯子" },
                Time.unscaledTimeAsDouble, world.WorldRevision));
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = true;
            Assert.That(driver.TryOwnRequest(request), Is.True);
            Assert.That(driver.TryConsumeResponse(new CognitiveResponse {
                message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
                trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = merchant.EntityId,
                decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.ActionProposal,
                    action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                        bindings = new Dictionary<string, string> { ["target"] = cup.EntityId, ["destination"] = playerEntity.EntityId } } },
                validated_goal = new GoalProposal { type = "bring_object", target_entity_id = cup.EntityId, destination_entity_id = playerEntity.EntityId }
            }), Is.True);
            Assert.That(runner.HasActiveTask, Is.True);
            Assert.That(driver.TryConsumeResponse(ModeResponse(request, "rest", "我开始休息了。")), Is.False,
                "A real pending bring_object must reject an extra mode result without settling its action ownership.");
            Assert.That(driver.HasPendingActionOutcome && runner.HasActiveTask, Is.True);
            settings.ModeDropdown.value = 1;
            Assert.That(settings.StatusText, Does.Contain("Work"));
            Assert.That(settings.ModeDropdown.captionText.text, Does.Contain(SettingsUiLanguage.T("等待切换", "waiting")));
            bool carried = false; int successes = 0;
            runner.GoalOutcomePublished += outcome => { if (outcome.Status == PlanExecutionStatus.Succeeded) successes++; };
            float deadline = Time.realtimeSinceStartup + 35;
            while (driver.HasPendingActionOutcome && Time.realtimeSinceStartup < deadline)
            {
                carried |= held.HolderEntityId == merchant.EntityId;
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
                Assert.That(routine.OwnsPath, Is.False); yield return null;
            }
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            Assert.That(carried, Is.True); Assert.That(successes, Is.EqualTo(1));
            Assert.That(held.HolderEntityId, Is.EqualTo(playerEntity.EntityId));
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            vision.enabled = false; admission.ReleaseEngagement();
            yield return WaitMode(DailyBehaviorMode.Rest);
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2));
            yield return Wait(.3f);
            Assert.That(settings.StatusText, Does.Contain("Rest"));
            AssertOffline();
        }

        [UnityTest] public IEnumerator SettingsManual_DefaultWorkAndPersistentRest_CancelPendingAndReload()
        {
            Prepare(true); Place(new Vector3(-5, .03f, -1));
            Assert.That(new UnityEditor.SerializedObject(source).FindProperty("automaticDemo").boolValue, Is.False);
            double began = Time.unscaledTimeAsDouble;
            while (Time.unscaledTimeAsDouble - began < 51) yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(source.PublishedEventCount, Is.Zero);
            Assert.That(routine.DailyDestination, Is.EqualTo(routine.HomePosition));
            yield return PrepareLocalSettings();
            var settings = Object.FindAnyObjectByType<CafeDailyBehaviorSettings>();
            bool english = SettingsUiLanguage.IsEnglish;
            try
            {
                SettingsUiLanguage.SetEnglish(true);
                var router = Object.FindAnyObjectByType<AgentEventRouter>();
                router.enabled = false; settings.ModeDropdown.value = 1;
                Assert.That(settings.ModeDropdown.value, Is.Zero);
                Assert.That(settings.StatusText, Does.Contain("Current: Work").And.Contain("request rejected"));
                router.enabled = true;
                var channels = merchant.GetComponent<MotionChannelCoordinator>();
                channels.BeginTaskMotion(); settings.ModeDropdown.value = 1;
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
                Assert.That(settings.StatusText, Does.Contain("Current: Work").And.Contain("waiting for: Rest"));
                settings.ModeDropdown.value = 0; // Replace the pending request before the task ends.
                channels.EndTaskMotion(); yield return Wait(1.2f);
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
                Assert.That(daily.HasPendingTransition, Is.False);
                Assert.That(settings.ModeDropdown.value, Is.Zero);
                settings.ModeDropdown.value = 1; yield return WaitMode(DailyBehaviorMode.Rest);
                yield return WaitAtDestination();
                double restStarted = Time.unscaledTimeAsDouble;
                while (Time.unscaledTimeAsDouble - restStarted < 36) yield return null;
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
                Assert.That(source.BreakRequested, Is.True, "Manual Rest must not inherit the old 35-second timer.");
                SettingsUiLanguage.SetEnglish(false);
                Assert.That(settings.StatusText, Does.Contain("当前：Rest"));
                Assert.That(settings.ModeDropdown.options[0].text, Is.EqualTo("工作 Work"));
                settings.ModeDropdown.value = 0; yield return WaitMode(DailyBehaviorMode.Work);
                yield return WaitAtDestination(); AssertOffline();
            }
            finally { SettingsUiLanguage.SetEnglish(english); }
            yield return new ExitPlayMode();
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            foreach (var network in Object.FindObjectsByType<BackendWebSocketClient>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var fields = new UnityEditor.SerializedObject(network);
                fields.FindProperty("connectOnStart").boolValue = false;
                fields.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
                fields.ApplyModifiedPropertiesWithoutUndo(); network.enabled = false;
            }
            foreach (var form in Object.FindObjectsByType<FirstRunModelSetup>(FindObjectsInactive.Include, FindObjectsSortMode.None)) form.enabled = false;
            foreach (var config in Object.FindObjectsByType<ModelConfigClient>(FindObjectsInactive.Include, FindObjectsSortMode.None)) config.enabled = false;
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>(FindObjectsInactive.Include).enabled = false;
            yield return new EnterPlayMode();
            Assert.That(Object.FindAnyObjectByType<DailyBehaviorDemoTrigger>().BreakRequested, Is.False);
            Assert.That(Object.FindAnyObjectByType<DailyBehaviorDemoTrigger>().PublishedEventCount, Is.Zero);
            Assert.That(Object.FindAnyObjectByType<DailyBehaviorDemoTrigger>().GetComponent<DailyBehaviorController>().CurrentMode,
                Is.EqualTo(DailyBehaviorMode.Work));
            AssertOffline();
        }

        [UnityTest] public IEnumerator NaturalIdle_FourRealRigs_EnvironmentYieldsToDialogueGesturesCarryAndNavigation()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            var actors = Object.FindObjectsByType<DailyBehaviorController>(FindObjectsSortMode.None);
            var positions = actors.Select(x => x.transform.position).ToArray();
            var seen = new HashSet<string>(); var points = new Dictionary<string, Vector3>();
            int revision = world.WorldRevision;
            float deadline = Time.realtimeSinceStartup + 35;
            while (seen.Count < 4 && Time.realtimeSinceStartup < deadline)
            {
                foreach (var actor in actors)
                {
                    var attention = actor.GetComponentInChildren<AttentionController>();
                    var gaze = actor.GetComponentInChildren<HeadGazeController>();
                    if (attention.Source == AttentionSource.EnvironmentIdle && gaze.CurrentWeight > .5f && gaze.CurrentAngles.magnitude > 2)
                    { seen.Add(actor.GetComponent<WorldEntity>().EntityId); points[actor.name] = attention.CurrentGazePoint; }
                }
                yield return null;
            }
            Assert.That(seen.Count, Is.EqualTo(4), string.Join(",", seen));
            for (int i = 0; i < actors.Length; i++)
            {
                var actor = actors[i]; var attention = actor.GetComponentInChildren<AttentionController>();
                var channels = actor.GetComponent<MotionChannelCoordinator>();
                var character = actor.GetComponentInChildren<Animator>();
                var settings = new UnityEditor.SerializedObject(attention);
                var anchors = settings.FindProperty("idleLookPoints");
                Assert.That(anchors.arraySize, Is.EqualTo(2));
                var configured = new[] { (Transform)anchors.GetArrayElementAtIndex(0).objectReferenceValue,
                    (Transform)anchors.GetArrayElementAtIndex(1).objectReferenceValue };
                Assert.That(configured.Select(x => x.position), Does.Contain(points[actor.name]));
                Assert.That(character.speed, Is.EqualTo(1)); Assert.That(character.applyRootMotion, Is.False);
                Assert.That(Vector3.Distance(actor.transform.position, positions[i]), Is.LessThan(.03f));
                Assert.That(actor.CurrentActivity, Is.EqualTo(actor == daily ? DailyBehaviorActivity.HomeIdle : DailyBehaviorActivity.SeatedIdle));
                if (actor != daily)
                {
                    Assert.That(actor.CurrentMode, Is.EqualTo(DailyBehaviorMode.Leisure));
                    Assert.That(actor.GetComponent<NavMeshAgent>(), Is.Null);
                    Assert.That(actor.Profile.ConsideredActivitiesFor(DailyBehaviorMode.Leisure),
                        Is.EqualTo(new[] { "seated_idle", "observe_environment" }));
                }
                Assert.That(character.runtimeAnimatorController.animationClips.Any(c =>
                    c.name == (actor == daily ? "Idle" : "Sitting_Idle_Loop")), Is.True);
                foreach (string gesture in new[] { "wave", "nod" })
                {
                    Assert.That(channels.RequestGesture(gesture, false), Is.True);
                    attention.Tick(.02f);
                    Assert.That(attention.Source, Is.Not.EqualTo(AttentionSource.EnvironmentIdle));
                    Assert.That(channels.ActiveGesture, Is.EqualTo(gesture));
                    channels.CancelGesture();
                }
                channels.SetCarryPresentationActive(true); attention.Tick(.02f);
                Assert.That(attention.Source, Is.Not.EqualTo(AttentionSource.EnvironmentIdle));
                Assert.That(channels.IsCarryPresentationActive, Is.True);
                channels.SetCarryPresentationActive(false);
                channels.SetNavigationActive(true); attention.Tick(.02f);
                Assert.That(attention.Source, Is.Not.EqualTo(AttentionSource.EnvironmentIdle));
                channels.SetNavigationActive(false);
                attention.ResetAttention();
                attention.ConfigureIdleLook(configured, new Vector2(.1f, .15f));
                attention.Tick(.2f);
                Assert.That(attention.Source, Is.EqualTo(AttentionSource.EnvironmentIdle));
                Vector3 first = attention.CurrentGazePoint;
                attention.Tick(4.2f);
                Assert.That(attention.CurrentGazePoint, Is.Not.EqualTo(first), "Do not repeat the same glance immediately.");
            }
            Place(merchant.transform.position + merchant.transform.forward * 1.3f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return Wait(.3f);
            Assert.That(merchant.GetComponentInChildren<AttentionController>().Source, Is.EqualTo(AttentionSource.Dialogue));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(source.PublishedEventCount, Is.Zero); AssertOffline();
        }

        private CognitiveRequest OwnPlayerRequest(string text)
        {
            receiver.Receive(new AgentEvent(Guid.NewGuid().ToString("N"), AgentEventTypes.PlayerUtterance,
                player.GetComponent<WorldEntity>().EntityId, merchant.EntityId, "",
                new Dictionary<string, string> { [AgentEventPayloadKeys.Text] = text },
                Time.unscaledTimeAsDouble, world.WorldRevision));
            Assert.That(orchestrator.HasActiveRun, Is.True);
            var request = merchant.GetComponent<CognitiveContextComposer>().LastRequest;
            Assert.That(request.IsValid, Is.True);
            Assert.That(merchant.GetComponent<CognitiveRunDriver>().TryOwnRequest(request), Is.True);
            return request;
        }
        private CognitiveResponse ModeResponse(CognitiveRequest request, string target, string text) => new() {
            message_type = "cognitive_response", schema_version = "1.0", request_id = request.request_id,
            trace_id = request.trace_id, run_id = request.run.run_id, agent_entity_id = merchant.EntityId,
            decision = new CognitiveDecision { decision_kind = CognitiveDecisionKinds.DirectResponse,
                utterance = text, target_daily_mode = target } };

        private ServiceZoneFakeBackend systemFake;
        private ServiceZoneDwellCognitionSource dwellSource;
        [UnityTest] public IEnumerator FirstDialogue_VisibleEventRecordsWithoutBlockingPlayerInput()
        {
            Prepare();
            var root = new GameObject("FirstDialogue_InMemoryTransport"); root.SetActive(false);
            systemFake = root.AddComponent<ServiceZoneFakeBackend>();
            var transport = new UnityEditor.SerializedObject(systemFake);
            transport.FindProperty("connectOnStart").boolValue = false;
            transport.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
            transport.ApplyModifiedPropertiesWithoutUndo(); root.SetActive(true);
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var presenter = (MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
            driver.Configure(merchant, orchestrator, merchant.GetComponent<CognitiveContextComposer>(), systemFake, presenter);
            driver.enabled = true;
            var ui = new UnityEditor.SerializedObject(dialogue);
            ui.FindProperty("backendClient").objectReferenceValue = systemFake; ui.ApplyModifiedPropertiesWithoutUndo();

            var observation = new Observation("first-dialogue-observation", merchant.EntityId,
                ObservationSourceType.ContinuousScan, "first-dialogue-visual", ObservationChannel.Visual,
                "player_01", new Dictionary<string,string> { ["visibility_state"]="current_visible",
                    ["entity_kind"]="Agent", ["agent_role"]="Player" }, Array.Empty<string>(),
                1f, world.WorldRevision, Time.unscaledTimeAsDouble, double.PositiveInfinity);
            Assert.That(merchant.GetComponent<PerceivedStateStore>().Apply(observation).IsApplied, Is.True);
            var visible = new AgentEvent("first-dialogue-visible", AgentEventTypes.EntityBecameVisible,
                "player_01", merchant.EntityId, "", null, Time.realtimeSinceStartupAsDouble,
                sourceObservationId: observation.ObservationId);
            Assert.That(receiver.Receive(visible), Is.True);
            Assert.That(receiver.LastInterestDecision, Is.EqualTo(EventInterestDecision.Record));
            Assert.That(receiver.LastEvent, Is.SameAs(visible), "Visual evidence must still be recorded.");
            Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(systemFake.Requests, Is.Empty, "Seeing the player must not occupy the first dialogue slot.");

            var front = GameObject.Find("CounterFrontPoint").transform.position;
            Place(new Vector3(front.x, .03f, front.z));
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(Object.FindAnyObjectByType<TextUtteranceInput>().SubmitText("早啊"), Is.True);
            Assert.That(systemFake.Requests.Count, Is.EqualTo(1));
            var request = systemFake.Requests.Single();
            Assert.That(request.context.current_event.event_type, Is.EqualTo(AgentEventTypes.PlayerUtterance));
            Assert.That(orchestrator.CurrentRun.IsServiceZoneConsideration, Is.False);
            var response = systemFake.Response(request, "DIRECT_RESPONSE"); response.decision.utterance = "早上好。";
            Assert.That(systemFake.Reply(response), Is.True); yield return null;
            Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Last().Text, Is.EqualTo("早上好。"));
            AssertOffline();
        }

        private IEnumerator PrepareServiceZone(bool useSavedInterest = false)
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            source.RequestMode(DailyBehaviorMode.Rest); yield return WaitMode(DailyBehaviorMode.Rest);
            yield return WaitAtDestination(); yield return Wait(1.2f);
            var root = new GameObject("P2D1_InMemoryTransport"); root.SetActive(false);
            systemFake = root.AddComponent<ServiceZoneFakeBackend>();
            var transport = new UnityEditor.SerializedObject(systemFake);
            transport.FindProperty("connectOnStart").boolValue = false;
            transport.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws"; transport.ApplyModifiedPropertiesWithoutUndo();
            root.SetActive(true);
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var presenter = (MonoBehaviour)typeof(CognitiveRunDriver).GetField("presentationBehaviour", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
            driver.Configure(merchant, orchestrator, merchant.GetComponent<CognitiveContextComposer>(), systemFake, presenter); driver.enabled = true;
            var ui = new UnityEditor.SerializedObject(dialogue); ui.FindProperty("backendClient").objectReferenceValue = systemFake; ui.ApplyModifiedPropertiesWithoutUndo();
            // Production Wake remains disabled while lifecycle tests explicitly opt into a temporary rule.
            var interest = useSavedInterest ? receiver.InterestProfile : Object.Instantiate(receiver.InterestProfile);
            if (!useSavedInterest) interest.Configure(interest.FallbackDecision, interest.Rules.Where(x=>x.EventType!=AgentEventTypes.PlayerDwelledInServiceZone).Concat(new[] {
                new EventInterestRule(AgentEventTypes.PlayerDwelledInServiceZone, EventInterestDecision.Wake,
                    "area_cafe_counter", EventInterestRelevance.TargetSelf, "rest") }).ToArray());
            var receiverFields = new UnityEditor.SerializedObject(receiver);
            receiverFields.FindProperty("interestProfile").objectReferenceValue = interest; receiverFields.ApplyModifiedPropertiesWithoutUndo();
            dwellSource = merchant.GetComponent<ServiceZoneDwellCognitionSource>();
            Assert.That(dwellSource, Is.Not.Null);
            var fields = new UnityEditor.SerializedObject(dwellSource); fields.FindProperty("cognitionEnabled").boolValue = true; fields.ApplyModifiedPropertiesWithoutUndo();
            dwellSource.enabled = true; AssertOffline();
        }
        private IEnumerator EnterServiceAndWaitForRequest(bool checkThreshold = false)
        {
            Vector3 point = dwellSource.ServiceZone.transform.TransformPoint(dwellSource.ServiceZone.center);
            point.y = .03f; Place(point + Vector3.back * 1.5f);
            float deadline = Time.realtimeSinceStartup + 4;
            while (Vector3.ProjectOnPlane(point - player.transform.position, Vector3.up).magnitude > .05f && Time.realtimeSinceStartup < deadline)
            {
                var delta = Vector3.ProjectOnPlane(point - player.transform.position, Vector3.up);
                player.Controller.Move(Vector3.ClampMagnitude(delta, 2.6f * Time.deltaTime)); yield return null;
            }
            Assert.That(dwellSource.ContainsPlayer(), Is.True, "Actual CharacterController must enter the authored front zone.");
            while (!dwellSource.DwellStartedAt.HasValue) yield return null;
            double started = dwellSource.DwellStartedAt.Value;
            while (Time.unscaledTimeAsDouble - started < 4.35)
            { if (checkThreshold) Assert.That(systemFake.Requests, Is.Empty); yield return null; }
            deadline = Time.realtimeSinceStartup + 2;
            while (systemFake.Requests.Count == 0 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(systemFake.Requests.Count, Is.EqualTo(1), dwellSource.LastStatus);
            Assert.That(dwellSource.CurrentEvent.OccurredAt - started, Is.InRange(4.5d, 5d));
        }

        [UnityTest] public IEnumerator P2D1_DwellSilentWork_UsesTrustedEventAndCurrentActivity()
        {
            yield return PrepareServiceZone(true); int revision = world.WorldRevision;
            var bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>(); int messages = bubbles.GetMessages(merchant.EntityId).Count;
            int presented = 0; merchant.GetComponent<CognitiveRunDriver>().UtterancePresented += _ => presented++;
            receiver.Receive(new AgentEvent("forged_dwell", AgentEventTypes.PlayerDwelledInServiceZone, "player_01", merchant.EntityId,
                "area_cafe_counter", null, Time.unscaledTimeAsDouble));
            Assert.That(orchestrator.HasActiveRun, Is.False); Assert.That(systemFake.Requests, Is.Empty);
            yield return EnterServiceAndWaitForRequest(true);
            var request = systemFake.Requests.Single();
            Assert.That(orchestrator.CurrentRun.IsServiceZoneConsideration, Is.True);
            Assert.That(request.context.current_event.event_type, Is.EqualTo(AgentEventTypes.PlayerDwelledInServiceZone));
            Assert.That(request.context.current_event.payload.Any(x => x.key == "text" || x.key == "conversation_id"), Is.False);
            Assert.That(systemFake.Reply(systemFake.Response(request)), Is.True);
            yield return WaitMode(DailyBehaviorMode.Work);
            yield return WaitAtDestination();
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.HomeIdle));
            Assert.That(orchestrator.HasActiveRun || merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False);
            Assert.That(presented, Is.Zero); Assert.That(bubbles.GetMessages(merchant.EntityId).Count, Is.EqualTo(messages));
            Assert.That(dialogue.HasActiveSession, Is.False); Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
        }

        [UnityTest] public IEnumerator P2D1_NoActionDedupAndForbiddenOutputs_RemainSilent()
        {
            yield return PrepareServiceZone(); yield return EnterServiceAndWaitForRequest();
            var request = systemFake.Requests.Single();
            var trustedEvent = dwellSource.CurrentEvent;
            Assert.That(systemFake.Reply(systemFake.Response(request, "NO_ACTION")), Is.True);
            yield return Wait(1.2f);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(orchestrator.HasActiveRun, Is.False);
            yield return Wait(2.4f); Assert.That(systemFake.Requests.Count, Is.EqualTo(1));
            Place(new Vector3(-5, .03f, -4)); yield return Wait(.3f);
            var center = dwellSource.ServiceZone.transform.position; Place(new Vector3(center.x,.03f,center.z));
            yield return Wait(5.1f); Assert.That(systemFake.Requests.Count, Is.EqualTo(1), "Re-entry cannot bypass 90s cooldown.");
            // New independent stay after configured cooldown: source still grants no public-output/action authority.
            Place(new Vector3(-5,.03f,-4)); yield return Wait(.3f);
            typeof(ServiceZoneDwellCognitionSource).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dwellSource, 0d);
            Place(new Vector3(center.x,.03f,center.z)); yield return Wait(5.1f);
            var bad = systemFake.Response(systemFake.Requests.Last(), "DIRECT_RESPONSE"); bad.decision.utterance = "我来帮你。";
            int before = Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Count;
            systemFake.Reply(bad); yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Count, Is.EqualTo(before));
            Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False); AssertOffline();
            Place(new Vector3(-5,.03f,-4)); yield return Wait(.3f);
            typeof(ServiceZoneDwellCognitionSource).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dwellSource, 0d);
            Place(new Vector3(center.x,.03f,center.z)); yield return Wait(5.1f);
            var action = systemFake.Response(systemFake.Requests.Last(), "ACTION_PROPOSAL");
            action.decision.action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                bindings = new Dictionary<string,string> { ["target"] = "cup_01", ["destination"] = "player_01" } };
            action.validated_goal = new GoalProposal { type="bring_object",target_entity_id="cup_01",destination_entity_id="player_01" };
            systemFake.Reply(action); yield return null;
            Assert.That(orchestrator.HasActiveRun || merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False);
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Count, Is.EqualTo(before));
            Place(new Vector3(-5,.03f,-4)); yield return Wait(.3f);
            typeof(ServiceZoneDwellCognitionSource).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dwellSource, 0d);
            Place(new Vector3(center.x,.03f,center.z)); yield return Wait(5.1f);
            var autonomous = systemFake.Response(systemFake.Requests.Last(), CognitiveDecisionKinds.AutonomousActionIntent);
            autonomous.decision.action_proposal = new CognitiveActionProposal { action_id="clean_counter", bindings=new Dictionary<string,string>{{"target","counter_cafe_clean_01"}} };
            autonomous.validated_goal = new GoalProposal {type="clean_counter",target_entity_id="counter_cafe_clean_01"};
            systemFake.Reply(autonomous); yield return null;
            Assert.That(orchestrator.HasActiveRun || merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False);
            int calls = systemFake.Requests.Count;
            Place(new Vector3(-5,.03f,-4)); yield return Wait(.3f);
            typeof(ServiceZoneDwellCognitionSource).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dwellSource, 0d);
            var channels = merchant.GetComponent<MotionChannelCoordinator>(); channels.BeginTaskMotion();
            Place(new Vector3(center.x,.03f,center.z)); yield return Wait(5.1f);
            Assert.That(systemFake.Requests.Count, Is.EqualTo(calls)); channels.EndTaskMotion();
            Assert.That(EventInterestEvaluator.Evaluate(trustedEvent, merchant.EntityId, receiver.InterestProfile,
                "working", receiver.ModeInterestOverrides, "work"), Is.EqualTo(EventInterestDecision.Ignore));
            foreach (var actor in Object.FindObjectsByType<DailyBehaviorController>(FindObjectsSortMode.None).Where(x=>x!=daily))
            {
                var endpoint = actor.GetComponent<AgentEventReceiver>();
                Assert.That(EventInterestEvaluator.Evaluate(trustedEvent, endpoint.EntityId, endpoint.InterestProfile,
                    "working", endpoint.ModeInterestOverrides, "leisure"), Is.EqualTo(EventInterestDecision.Ignore));
            }
            AssertOffline();
        }

        [UnityTest] public IEnumerator P2D1_PlayerPreemptsBlockedSend_OldResultsAndTimeoutCannotOwnNewRun()
        {
            yield return PrepareServiceZone(); systemFake.BlockServiceSend = new TaskCompletionSource<bool>();
            yield return EnterServiceAndWaitForRequest();
            var old = systemFake.Requests.Single(); var driver = merchant.GetComponent<CognitiveRunDriver>();
            Assert.That(merchant.GetComponent<CognitiveDialogueSubmissionHandler>().Submit(new UserUtterance {
                conversationId="invalid",speakerEntityId="cup_01",targetAgentId=merchant.EntityId,inputMode="text",text="你好" }).Accepted, Is.False);
            Assert.That(orchestrator.CurrentRun.RunId, Is.EqualTo(old.run.run_id));
            // Buffer a result in this frame; opening a real player session must invalidate it before LateUpdate.
            systemFake.Reply(systemFake.Response(old));
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            Assert.That(orchestrator.HasActiveRun, Is.False);
            var input = Object.FindAnyObjectByType<TextUtteranceInput>();
            Assert.That(input.SubmitText("你好，薇薇。"), Is.True);
            var current = systemFake.Requests.Last(); Assert.That(current.context.current_event.event_type, Is.EqualTo(AgentEventTypes.PlayerUtterance));
            Assert.That(current.run.run_id, Is.Not.EqualTo(old.run.run_id));
            Assert.That(orchestrator.CurrentRun.IsServiceZoneConsideration, Is.False);
            systemFake.BlockServiceSend.SetResult(true); yield return null;
            Assert.That(systemFake.Cancels.Count(x => x.request_id == old.request_id), Is.GreaterThanOrEqualTo(2));
            Assert.That(driver.TryConsumeResponse(systemFake.Response(old)), Is.False);
            Assert.That(driver.TryConsumeFailure(new CognitiveTransportFailure(old.request_id, old.run.run_id, merchant.EntityId, "old_error", old.trace_id)), Is.False);
            var timer = (IEnumerator)typeof(CognitiveRunDriver).GetMethod("FailAfterTimeout", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, new object[] { old });
            Assert.That(timer.MoveNext(), Is.True); Assert.That(timer.MoveNext(), Is.False);
            Assert.That(orchestrator.CurrentRun.RunId, Is.EqualTo(current.run.run_id));
            Assert.That(driver.CancelServiceZoneConsideration("must_not_cancel_foreground"), Is.False);
            var reply = systemFake.Response(current, "DIRECT_RESPONSE"); reply.decision.utterance = "你好。";
            systemFake.Reply(reply); yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Last().Text, Is.EqualTo("你好。")); AssertOffline();
        }

        [UnityTest] public IEnumerator P2D1_LeaseAndVersionChanges_RejectLateWork()
        {
            yield return PrepareServiceZone(); yield return EnterServiceAndWaitForRequest();
            var request = systemFake.Requests.Single(); var driver = merchant.GetComponent<CognitiveRunDriver>();
            Place(new Vector3(-5,.03f,-4));
            systemFake.Reply(systemFake.Response(request)); yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(orchestrator.HasActiveRun, Is.False);
            Assert.That(driver.TryConsumeResponse(systemFake.Response(request)), Is.False);
            // A fresh stay with a changed profile must be checked at commit, even before a periodic source tick.
            yield return Wait(.3f);
            typeof(ServiceZoneDwellCognitionSource).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dwellSource, 0d);
            var center = dwellSource.ServiceZone.transform.position; Place(new Vector3(center.x,.03f,center.z)); yield return Wait(5.1f);
            var next = systemFake.Requests.Last(); var original = daily.Profile; var changed = Object.Instantiate(original);
            var fields = new UnityEditor.SerializedObject(daily); fields.FindProperty("profile").objectReferenceValue = changed; fields.ApplyModifiedPropertiesWithoutUndo();
            systemFake.Reply(systemFake.Response(next)); yield return null;
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest)); Assert.That(orchestrator.HasActiveRun, Is.False);
            fields.FindProperty("profile").objectReferenceValue = original; fields.ApplyModifiedPropertiesWithoutUndo(); Object.Destroy(changed);
            Place(new Vector3(-5,.03f,-4)); yield return Wait(.3f);
            typeof(ServiceZoneDwellCognitionSource).GetField("cooldownUntil", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dwellSource, 0d);
            Place(new Vector3(center.x,.03f,center.z)); yield return Wait(5.1f);
            var disabled = systemFake.Requests.Last(); Assert.That(orchestrator.HasActiveRun, Is.True);
            dwellSource.enabled = false;
            Assert.That(driver.TryConsumeResponse(systemFake.Response(disabled)), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            dwellSource.enabled = true; yield return Wait(5.1f);
            Assert.That(systemFake.Requests.Last(), Is.SameAs(disabled), "Disable/re-enable inside is not a new physical stay.");
            Assert.That(driver.CancelServiceZoneConsideration("duplicate"), Is.False); AssertOffline();
        }

        [UnityTest] public IEnumerator P2C_ProfileCandidatesDriveSelectionAndCognitiveProjection()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            yield return Wait(1.3f);
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.HomeIdle));
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            var original = daily.Profile; var configured = Object.Instantiate(original);
            var fields = new UnityEditor.SerializedObject(daily);
            try
            {
                // A real authored option: choose in-place rest first, even with a valid window.
                typeof(DailyBehaviorProfile).GetField("activities", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(configured,
                    new[] { new DailyBehaviorActivityBinding(DailyBehaviorMode.Work, DailyBehaviorActivity.HomeIdle),
                        new DailyBehaviorActivityBinding(DailyBehaviorMode.Work, DailyBehaviorActivity.NaturalIdle),
                        new DailyBehaviorActivityBinding(DailyBehaviorMode.Rest, DailyBehaviorActivity.NaturalIdle) });
                Assert.That(configured.IsValid, Is.True);
                fields.FindProperty("profile").objectReferenceValue = configured; fields.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(source.RequestMode(DailyBehaviorMode.Rest), Is.True);
                yield return WaitMode(DailyBehaviorMode.Rest); yield return Wait(1.2f);
                Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.NaturalIdle));
                Assert.That(daily.WindowRestPoint.gameObject.activeInHierarchy, Is.True);
                Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
                Vector3 position = merchant.transform.position; yield return Wait(1.2f);
                Assert.That(Vector3.Distance(position, merchant.transform.position), Is.LessThan(.03f));
                var request = OwnPlayerRequest("你好。");
                Assert.That(request.context.daily_behavior.considered_activities, Is.EqualTo(new[] { "natural_idle" }));
                Assert.That(request.context.daily_behavior.IsValid, Is.True);
                Assert.That(daily.AllowsEnvironmentObservation, Is.False, "Removed overlay cannot remain active in Attention.");
                orchestrator.CancelCurrentRun(); admission.ReleaseEngagement();
                yield return Wait(1.2f);
                bool english = SettingsUiLanguage.IsEnglish;
                try
                {
                    SettingsUiLanguage.SetEnglish(true);
                    Assert.That(ReadDebug(merchant), Does.Contain("Daily Activity: NaturalIdle").And.Contain("Formal Task: False"));
                }
                finally { SettingsUiLanguage.SetEnglish(english); }
                daily.enabled = false; yield return Wait(1.3f);
                Assert.That(daily.CurrentActivity, Is.Null);
                Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
                daily.enabled = true; yield return Wait(1.2f);
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
                Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.NaturalIdle));
                AssertOffline();
            }
            finally
            {
                daily.enabled = true;
                fields.FindProperty("profile").objectReferenceValue = original; fields.ApplyModifiedPropertiesWithoutUndo();
                Object.Destroy(configured);
            }
        }

        [UnityTest] public IEnumerator P2Modes_UnreachableActivitiesKeepRestAndSafeIdle()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            Vector3 originalPoint = daily.WindowRestPoint.position;
            daily.WindowRestPoint.position = new Vector3(10000, 0, 10000);
            Vector3 start = merchant.transform.position;
            Assert.That(source.RequestMode(DailyBehaviorMode.Rest), Is.True);
            yield return WaitMode(DailyBehaviorMode.Rest); yield return Wait(1.5f);
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.NaturalIdle));
            Assert.That(daily.HasUnavailableActivity, Is.True);
            int failures = routine.NavigationFailureCount;
            Assert.That(failures, Is.EqualTo(1));
            yield return Wait(2.4f);
            Assert.That(routine.NavigationFailureCount, Is.EqualTo(failures), "Unreachable activity must not be repeatedly re-issued.");
            Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
            Assert.That(Vector3.Distance(start, merchant.transform.position), Is.LessThan(.03f));
            Assert.That(daily.TryGetRestGazePoint(out _), Is.False);
            Assert.That(daily.ModeRevision, Is.EqualTo(1));
            daily.WindowRestPoint.gameObject.SetActive(false);
            yield return Wait(1.3f);
            Assert.That(daily.isActiveAndEnabled && daily.CurrentMode == DailyBehaviorMode.Rest, Is.True);
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.NaturalIdle));
            Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
            // Dialogue can still claim the NPC after a failed activity, then release normally.
            Place(merchant.transform.position + merchant.transform.forward * 1.3f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return Wait(.3f);
            Assert.That(daily.IsActivitySuspended, Is.True);
            dialogue.HideDialoguePresentation(); admission.ReleaseEngagement();
            Place(new Vector3(-5, .03f, -4));
            daily.WindowRestPoint.position = originalPoint;
            daily.WindowRestPoint.gameObject.SetActive(true);
            yield return WaitAtDestination();
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.WindowIdle));
            // Work uses its own Home candidate; its failure also falls back without changing mode.
            start = merchant.transform.position;
            typeof(ReturnToHomeRoutine).GetField("homePosition", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(routine, new Vector3(-10000, 0, -10000));
            Assert.That(source.RequestMode(DailyBehaviorMode.Work), Is.True);
            yield return WaitMode(DailyBehaviorMode.Work); yield return Wait(1.3f);
            Assert.That(daily.ModeRevision, Is.EqualTo(2));
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.NaturalIdle));
            Assert.That(routine.OwnsPath || agent.hasPath, Is.False);
            Assert.That(Vector3.Distance(start, merchant.transform.position), Is.LessThan(.03f));
            Assert.That(orchestrator.HasActiveRun, Is.False); AssertOffline();
            daily.WindowRestPoint.position = originalPoint;
        }

        [UnityTest] public IEnumerator P2Cognition_RestChatThenWorkIntent_CommitsBeforeReplyAndSettingsStillWorks()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            source.RequestMode(DailyBehaviorMode.Rest); yield return WaitMode(DailyBehaviorMode.Rest);
            yield return WaitAtDestination();
            yield return Wait(1.2f);
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var chat = OwnPlayerRequest("你好，薇薇。");
            Assert.That(chat.context.daily_behavior.current_mode, Is.EqualTo("rest"));
            Assert.That(chat.context.daily_behavior.objective, Does.Contain("个人休闲"));
            Assert.That(chat.context.daily_behavior.considered_activities, Does.Contain("window_idle"));
            Assert.That(chat.context.daily_behavior.candidates.Select(x => x.target_mode), Does.Contain("work"));
            Assert.That(chat.context.behavior_mode.mode_id, Is.EqualTo("working"));
            Assert.That(driver.TryConsumeResponse(ModeResponse(chat, null, "你好，休息时聊聊也好。")), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            int revision = world.WorldRevision;
            var service = OwnPlayerRequest("薇薇，能帮我拿杯子吗？");
            string presented = null; DailyBehaviorMode whenPresented = DailyBehaviorMode.Rest;
            driver.UtterancePresented += value => { presented = value.Utterance; whenPresented = daily.CurrentMode; };
            var response = ModeResponse(service, "work", "我先恢复工作。拿杯子的动作还没有开始。");
            Assert.That(CognitiveResponseValidator.TryParse(Newtonsoft.Json.JsonConvert.SerializeObject(response), out var parsed, out _), Is.True);
            Assert.That(driver.TryConsumeResponse(parsed), Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work), driver.LastDailyModeResult);
            Assert.That(whenPresented, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(presented, Is.EqualTo("好，那我接着忙了。"));
            Assert.That(driver.TryConsumeResponse(parsed), Is.False);
            Assert.That(daily.ModeRevision, Is.EqualTo(2));
            Assert.That(source.BreakRequested, Is.False, "Cognitive commit must synchronize the manual source condition.");
            Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
            yield return PrepareLocalSettings();
            Object.FindAnyObjectByType<CafeDailyBehaviorSettings>().ModeDropdown.value = 1;
            yield return WaitMode(DailyBehaviorMode.Rest);
            Assert.That(daily.ModeRevision, Is.EqualTo(3)); AssertOffline();
        }

        [UnityTest] public IEnumerator P2Cognition_InvalidStaleAndBusyIntents_TruthfulFallbackAndPresentationRollback()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            source.RequestMode(DailyBehaviorMode.Rest); yield return WaitMode(DailyBehaviorMode.Rest);
            yield return Wait(1.2f);
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var presenter = merchant.GetComponent<DialogueBubbleCognitivePresenter>();
            var channels = merchant.GetComponent<MotionChannelCoordinator>();
            var originalProfile = daily.Profile; var changed = Object.Instantiate(originalProfile);
            changed.Configure(DailyBehaviorMode.Work, new[] { DailyBehaviorMode.Work, DailyBehaviorMode.Rest }, Array.Empty<DailyBehaviorTransition>());
            string shown = null;
            driver.UtterancePresented += value => shown = value.Utterance;
            int revision = world.WorldRevision;
            foreach (string scenario in new[] { "illegal", "wrong_npc", "wrong_run", "profile_changed", "condition_changed", "expired",
                "busy", "tampered_context", "presentation_failure", "no_candidates" })
            {
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
                shown = null;
                var fields = new UnityEditor.SerializedObject(daily);
                if (scenario == "no_candidates")
                { fields.FindProperty("profile").objectReferenceValue = changed; fields.ApplyModifiedPropertiesWithoutUndo(); }
                var request = OwnPlayerRequest("我可以请你恢复工作吗？");
                var result = ModeResponse(request, scenario == "illegal" ? "leisure" : "work", "我已经恢复工作了。");
                if (scenario == "wrong_npc") result.agent_entity_id = customer.EntityId;
                if (scenario == "wrong_run") result.run_id = "expired_run";
                if (scenario == "profile_changed")
                { fields.FindProperty("profile").objectReferenceValue = changed; fields.ApplyModifiedPropertiesWithoutUndo(); }
                if (scenario == "condition_changed") { source.EndBreak(); source.RequestMode(DailyBehaviorMode.Rest); }
                if (scenario == "expired") typeof(DailyBehaviorController).GetField("capturedAt", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(daily, Time.unscaledTimeAsDouble - 31);
                if (scenario == "busy") channels.BeginTaskMotion();
                if (scenario == "tampered_context") request.context.daily_behavior.objective += " changed";
                if (scenario == "presentation_failure") presenter.enabled = false;
                if (scenario == "no_candidates") Assert.That(request.context.daily_behavior.candidates, Is.Empty);
                int modeRevision = daily.ModeRevision;
                bool consumed = driver.TryConsumeResponse(result);
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest), scenario);
                Assert.That(daily.ModeRevision, Is.EqualTo(modeRevision), scenario);
                Assert.That(shown, Is.Not.EqualTo(result.decision.utterance), scenario);
                if (scenario == "wrong_npc" || scenario == "wrong_run" || scenario == "presentation_failure")
                { Assert.That(consumed, Is.False, scenario); Assert.That(shown, Is.Null, scenario); }
                else { Assert.That(consumed, Is.True, scenario); Assert.That(shown, Does.Contain("还在休息"), scenario); }
                channels.EndTaskMotion(); presenter.enabled = true;
                fields.FindProperty("profile").objectReferenceValue = originalProfile; fields.ApplyModifiedPropertiesWithoutUndo();
                orchestrator.CancelCurrentRun();
                Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
            }
            Object.Destroy(changed);
        }

        private IEnumerator PrepareRestForCombinedAction()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            source.RequestMode(DailyBehaviorMode.Rest); yield return WaitMode(DailyBehaviorMode.Rest);
            yield return WaitAtDestination(); yield return Wait(1.2f);
            Place(new Vector3(-2.2f, .03f, -.8f));
            var cup = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "cup_01");
            merchant.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(
                cup.GetComponent<PerceivableEntity>().VisualAnchor.position - merchant.transform.position, Vector3.up));
            // The real cup can be occluded from the window. Keep real sensing active for rediscovery;
            // never inject current-visible evidence or change the scene cup/NPC position.
            var vision = merchant.GetComponent<VisionSensor>(); vision.enabled = true; vision.ScanNow();
        }
        private CognitiveResponse BringResponse(CognitiveRequest request, string mode = "work")
        {
            var result = ModeResponse(request, mode, "我已恢复工作，开始处理取杯请求。");
            result.decision.decision_kind = CognitiveDecisionKinds.ActionProposal;
            result.decision.action_proposal = new CognitiveActionProposal { action_id = "bring_object",
                bindings = new Dictionary<string, string> { ["target"] = "cup_01", ["destination"] = "player_01" } };
            result.validated_goal = new GoalProposal { type = "bring_object", target_entity_id = "cup_01", destination_entity_id = "player_01" };
            return result;
        }
        private void AssertBubbleControl(string text, bool expected)
        {
            if (!expected) return;
            var manager = Object.FindAnyObjectByType<DialogueBubbleManager>();
            var message = manager.GetMessages(merchant.EntityId).Last();
            Assert.That(message.Text, Is.EqualTo(text));
            var view = manager.GetComponentsInChildren<DialogueBubbleView>(true)
                .Single(x => x.Message.Sequence == message.Sequence && x.Message.SpeakerEntityId == merchant.EntityId);
            Assert.That(view.CanPresentText, Is.True);
            Assert.That(view.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(x => x.text == text), Is.True);
            var camera = (Camera)typeof(DialogueBubbleManager).GetField("sceneCamera", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            Vector3 position = camera.transform.position; Quaternion rotation = camera.transform.rotation;
            try
            {
                // Test-only framing: observe the actual NPC without moving it or changing world state.
                camera.transform.position = merchant.transform.position - merchant.transform.forward * 4 + Vector3.up * 1.8f;
                camera.transform.LookAt(merchant.transform.position + Vector3.up * 1.8f);
                manager.Refresh(Time.unscaledTimeAsDouble);
                Assert.That(view.gameObject.activeInHierarchy, Is.True);
                Assert.That(view.ScreenRect.width > 0 && view.ScreenRect.height > 0, Is.True);
            }
            finally { camera.transform.SetPositionAndRotation(position, rotation); }
        }

        private IEnumerator AssertRealDelivery(CognitiveRequest request, int worldBefore, bool expectCompletionPresentation = true)
        {
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var runner = merchant.GetComponent<GoalPlanRunner>();
            var held = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "cup_01").GetComponent<HeldItemState>();
            AssertBubbleControl("我已恢复工作，开始处理取杯请求。", expectCompletionPresentation);
            string proposalId = runner.ActiveProposalId;
            GoalOutcome? completed = null; string outcomeMessage = null;
            GoalPlanPresentation terminalPresentation = default;
            runner.GoalOutcomePublished += value => completed = value;
            runner.PresentationReady += value => { if (value.Kind == GoalPlanPresentationKind.Succeeded)
                { outcomeMessage = value.Message; terminalPresentation = value; } };
            bool carried = false; float deadline = Time.realtimeSinceStartup + 35;
            while (driver.HasPendingActionOutcome && Time.realtimeSinceStartup < deadline)
            {
                carried |= held.HolderEntityId == merchant.EntityId;
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
                Assert.That(routine.OwnsPath, Is.False, "Daily return-home must yield navigation to the real task.");
                Assert.That(daily.IsFormalTaskOccupied, Is.True);
                Assert.That(driver.CancelServiceZoneConsideration("must_not_cancel_action_wait"), Is.False);
                yield return null;
            }
            Assert.That(completed.HasValue, Is.True);
            Assert.That(completed.Value.Status, Is.EqualTo(PlanExecutionStatus.Succeeded), completed.Value.FailureCode);
            Assert.That(completed.Value.TraceId, Is.EqualTo(request.trace_id));
            Assert.That(completed.Value.ProposalId, Is.EqualTo(proposalId));
            Assert.That(completed.Value.ActorEntityId, Is.EqualTo(merchant.EntityId));
            Assert.That(driver.HasPendingActionOutcome || runner.HasActiveTask, Is.False);
            Assert.That(carried, Is.True); Assert.That(held.HolderEntityId, Is.EqualTo("player_01"));
            Assert.That(world.WorldRevision, Is.EqualTo(worldBefore + 2));
            Assert.That(outcomeMessage, Is.EqualTo("给你。"));
            var messages = Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId);
            if (expectCompletionPresentation)
            {
                Assert.That(messages.Last().Text, Is.EqualTo("给你。"), "A terminal event alone is not displayed feedback.");
                Assert.That(messages.Count(x => x.Text == "给你。"), Is.EqualTo(1));
                Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("displayed"));
                AssertBubbleControl("给你。", true);
                Assert.That(terminalPresentation.Outcome.HasValue, Is.True);
                long sequence = messages.Last().Sequence;
                // Replay the same real terminal presentation, not a fabricated success Outcome.
                typeof(CognitiveRunDriver).GetMethod("HandleGoalPresentation", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(driver, new object[] { terminalPresentation });
                Assert.That(Object.FindAnyObjectByType<DialogueBubbleManager>().GetMessages(merchant.EntityId).Last().Sequence,
                    Is.EqualTo(sequence));
            }
            else Assert.That(messages.Any(x => x.Text == "给你。"), Is.False);
            Assert.That(daily.ModeRevision, Is.EqualTo(2)); AssertOffline();
        }

        [UnityTest] public IEnumerator P2BCombined_WorkThenRealDelivery_YieldsDailyNavigationAndUsesOutcome()
        {
            yield return PrepareRestForCombinedAction();
            var request = OwnPlayerRequest("薇薇，可以先结束休息，帮我拿个杯子吗？");
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = true;
            var runner = merchant.GetComponent<GoalPlanRunner>(); int revision = world.WorldRevision;
            string shown = null; bool taskAtShow = false; DailyBehaviorMode modeAtShow = DailyBehaviorMode.Rest;
            driver.UtterancePresented += value => { shown = value.Utterance; taskAtShow = runner.HasActiveTask; modeAtShow = daily.CurrentMode; };
            var response = BringResponse(request);
            Assert.That(CognitiveResponseValidator.TryParse(Newtonsoft.Json.JsonConvert.SerializeObject(response), out var parsed, out _), Is.True);
            Assert.That(driver.TryConsumeResponse(parsed), Is.True);
            Assert.That(modeAtShow, Is.EqualTo(DailyBehaviorMode.Work)); Assert.That(taskAtShow, Is.True);
            Assert.That(shown, Is.EqualTo(response.decision.utterance));
            Assert.That(driver.LastActionHandoffResult, Is.EqualTo("action_accepted"));
            Assert.That(driver.HasPendingActionOutcome, Is.True);
            yield return AssertRealDelivery(request, revision);
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); admission.ReleaseEngagement();
            yield return WaitAtDestination();
            Assert.That(routine.DailyDestination, Is.Not.EqualTo(daily.WindowRestPoint.position));
            Assert.That(daily.CurrentActivity, Is.EqualTo(DailyBehaviorActivity.HomeIdle),
                "After delivery choose from current Work, rather than restoring the interrupted WindowIdle.");
            Assert.That(world.WorldRevision, Is.EqualTo(revision + 2)); AssertOffline();
        }

        [UnityTest] public IEnumerator P2BCombined_InvalidModeActionAndEnvelope_DoNotCommitOrStart()
        {
            yield return PrepareRestForCombinedAction();
            var original = daily.Profile; var limited = Object.Instantiate(original);
            limited.Configure(DailyBehaviorMode.Work, new[] { DailyBehaviorMode.Work, DailyBehaviorMode.Rest }, Array.Empty<DailyBehaviorTransition>());
            var driver = merchant.GetComponent<CognitiveRunDriver>(); string shown = null;
            driver.UtterancePresented += value => shown = value.Utterance;
            int revision = world.WorldRevision;
            foreach (string scenario in new[] { "restricted_profile", "stale_profile", "invalid_item", "wrong_run", "stale_world" })
            {
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); orchestrator.CancelCurrentRun(); shown = null;
                if (scenario == "restricted_profile")
                    typeof(DailyBehaviorController).GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(daily, limited);
                var request = OwnPlayerRequest("结束休息，帮我拿杯子。"); var response = BringResponse(request);
                if (scenario == "stale_profile")
                    typeof(DailyBehaviorController).GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(daily, limited);
                if (scenario == "invalid_item") { response.decision.action_proposal.bindings["target"] = "missing_item"; response.validated_goal.target_entity_id = "missing_item"; }
                if (scenario == "wrong_run") response.run_id = "foreign_run";
                if (scenario == "stale_world") request.context.current_event.world_revision = world.WorldRevision + 1;
                int before = daily.ModeRevision;
                Assert.That(driver.TryConsumeResponse(response), Is.False, scenario);
                Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest), scenario);
                Assert.That(daily.ModeRevision, Is.EqualTo(before), scenario);
                Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask || driver.HasPendingActionOutcome, Is.False, scenario);
                Assert.That(shown, Is.Not.EqualTo(response.decision.utterance), scenario);
                if (scenario != "wrong_run") Assert.That(shown, Does.Contain("你刚才托我的事还没开始做"), scenario);
                if (scenario == "invalid_item" || scenario == "stale_world")
                    Assert.That(driver.LastActionHandoffResult, Does.StartWith("action_prevalidation_failed:"));
                typeof(DailyBehaviorController).GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(daily, original);
                Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
            }
            // Rest and unavailable Work conversion do not remove an independently legal action.
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); orchestrator.CancelCurrentRun();
            typeof(DailyBehaviorController).GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(daily, limited);
            var pure = OwnPlayerRequest("请帮我拿杯子。"); Assert.That(pure.context.daily_behavior.candidates, Is.Empty);
            driver.enabled = true;
            var pureResponse = BringResponse(pure, null); pureResponse.decision.utterance = "我开始处理取杯请求。";
            Assert.That(driver.TryConsumeResponse(pureResponse), Is.True);
            Assert.That(merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.True);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Rest));
            merchant.GetComponent<GoalPlanRunner>().CancelCurrentPlan();
            Assert.That(driver.HasPendingActionOutcome, Is.False);
            typeof(DailyBehaviorController).GetField("profile", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(daily, original);
            Object.Destroy(limited); AssertOffline();
        }

        [UnityTest] public IEnumerator P2BCombined_HandoffFailure_KeepsCommittedWorkAndTruthfulFeedback()
        {
            yield return PrepareRestForCombinedAction();
            var request = OwnPlayerRequest("结束休息，帮我拿杯子。");
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = true;
            var runner = merchant.GetComponent<GoalPlanRunner>();
            var field = typeof(GoalPlanRunner).GetField("executor", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = field.GetValue(runner); field.SetValue(runner, null);
            string shown = null; driver.UtterancePresented += value => shown = value.Utterance;
            int revision = world.WorldRevision;
            Assert.That(driver.TryConsumeResponse(BringResponse(request)), Is.False);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(daily.ModeRevision, Is.EqualTo(2));
            Assert.That(driver.LastDailyModeResult, Is.EqualTo("cognition_mode_applied"));
            Assert.That(driver.LastActionHandoffResult, Is.EqualTo("action_handoff_failed"));
            Assert.That(shown, Is.EqualTo("好，那我接着忙了。不过，你刚才托我的事还没开始做。"));
            Assert.That(runner.HasActiveTask || driver.HasPendingActionOutcome, Is.False);
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            field.SetValue(runner, original); AssertOffline();
        }

        [UnityTest] public IEnumerator P2BCombined_PresentationFailure_DoesNotRollbackAcceptedWorldTask()
        {
            yield return PrepareRestForCombinedAction();
            var request = OwnPlayerRequest("结束休息，帮我拿杯子。");
            var driver = merchant.GetComponent<CognitiveRunDriver>(); driver.enabled = true;
            var presenter = merchant.GetComponent<DialogueBubbleCognitivePresenter>(); presenter.enabled = false;
            string shown = null; driver.UtterancePresented += value => shown = value.Utterance;
            int revision = world.WorldRevision;
            Assert.That(driver.TryConsumeResponse(BringResponse(request)), Is.True);
            Assert.That(shown, Is.Null);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            Assert.That(driver.HasPendingActionOutcome && merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.True);
            yield return AssertRealDelivery(request, revision, false);
            presenter.enabled = true;
        }

        [UnityTest] public IEnumerator P2BClosure_CancelFeedbackAndClosedConversation_DoNotShowSuccessOrReopen()
        {
            Prepare(); Place(merchant.transform.position + merchant.transform.forward * 1.3f);
            int worldBefore = world.WorldRevision;
            var invalid = merchant.GetComponent<GoalValidator>().ValidateAdmission(new GoalProposal
                { type = "bring_object", target_entity_id = "player_01", destination_entity_id = "player_01" });
            Assert.That(invalid.IsApproved, Is.False);
            Assert.That(invalid.FailureCode, Is.EqualTo(GoalValidationFailureCode.TargetNotItem));
            Assert.That(world.WorldRevision, Is.EqualTo(worldBefore));
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            var runner = merchant.GetComponent<GoalPlanRunner>();
            var bubbles = Object.FindAnyObjectByType<DialogueBubbleManager>();
            foreach (string lifecycle in new[] { "open", "hide", "end", "switch" })
            {
                merchant.GetComponent<SpeechPresentationLifecycle>().Cancel();
                driver.enabled = false; orchestrator.CancelCurrentRun();
                Place(merchant.transform.position + merchant.transform.forward * 1.3f);
                Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
                Assert.That(dialogue.IsPresentationVisible, Is.True);
                var request = OwnPlayerRequest("请把杯子递给我。");
                driver.enabled = true;
                var response = BringResponse(request, null); response.decision.utterance = "我开始处理这个请求。";
                Assert.That(driver.TryConsumeResponse(response), Is.True);
                Assert.That(runner.HasActiveTask, Is.True);
                long before = bubbles.GetMessages(merchant.EntityId).Last().Sequence;
                if (lifecycle == "hide") dialogue.HideDialoguePresentation();
                if (lifecycle == "end") dialogue.ClearSession("closure_test");
                if (lifecycle == "switch")
                {
                    Place(customer.transform.position + customer.transform.forward * 1.3f);
                    Assert.That(dialogue.SelectTarget(customer.GetComponent<DialogueParticipant>()), Is.True);
                }
                runner.CancelCurrentPlan();
                Assert.That(driver.HasPendingActionOutcome || runner.HasActiveTask, Is.False);
                var messages = bubbles.GetMessages(merchant.EntityId);
                Assert.That(messages.Any(x => x.Text == "给你。"), Is.False);
                if (lifecycle == "open")
                {
                    Assert.That(messages.Last().Text, Is.EqualTo("这次没能把它拿给你。"));
                    Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("displayed"));
                }
                else
                {
                    Assert.That(messages.Last().Sequence, Is.EqualTo(before), lifecycle);
                    if (lifecycle == "switch") Assert.That(dialogue.SelectedTarget.Entity, Is.EqualTo(customer));
                    else Assert.That(dialogue.IsPresentationVisible, Is.False);
                    Assert.That(driver.LastActionOutcomePresentationResult, Is.EqualTo("lifecycle_unavailable"));
                }
                dialogue.ClearSession("closure_next"); AssertOffline(); yield return null;
            }
        }

        private void AssertModeReply(CognitiveRequest request, CognitiveResponse response,
            DailyBehaviorMode expectedMode, string expectedText)
        {
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            string shown = null; DailyBehaviorMode? whenShown = null;
            driver.UtterancePresented += value => { shown = value.Utterance; whenShown = daily.CurrentMode; };
            string original = response.decision.utterance; int revision = world.WorldRevision;
            Assert.That(driver.TryConsumeResponse(response), Is.True, driver.LastDailyModeResult);
            Debug.Log("[ModeReplyNaturalPolish] target=" + response.decision.target_daily_mode +
                "; mode=" + daily.CurrentMode + "; result=" + driver.LastDailyModeResult + "; bubble=" + shown);
            Assert.That(daily.CurrentMode, Is.EqualTo(expectedMode), driver.LastDailyModeResult);
            Assert.That(whenShown, Is.EqualTo(expectedMode));
            Assert.That(shown, Is.EqualTo(expectedText)); AssertBubbleControl(expectedText, true);
            Assert.That(response.decision.utterance, Is.EqualTo(original));
            Assert.That(world.WorldRevision, Is.EqualTo(revision));
            Assert.That(driver.HasPendingActionOutcome || merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False);
            AssertOffline();
        }

        [UnityTest] public IEnumerator ModeReplyNaturalPolish_T1_RestCommitsBeforeConversationalBubble()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4)); yield return Wait(1.2f);
            Assert.That(daily.CurrentMode, Is.EqualTo(DailyBehaviorMode.Work));
            var request = OwnPlayerRequest("你忙了这么久，不休息一下吗？");
            AssertModeReply(request, ModeResponse(request, "rest", "谢谢你惦记，我想缓一缓。"),
                DailyBehaviorMode.Rest, "好，那我先歇会儿。");
        }

        [UnityTest] public IEnumerator ModeReplyNaturalPolish_T2_WorkDoesNotPromiseUnsubmittedCoffee()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4));
            source.RequestMode(DailyBehaviorMode.Rest); yield return WaitMode(DailyBehaviorMode.Rest);
            // Set up the already-resting case; do not test navigation or bypass its occupancy gate.
            var channels = merchant.GetComponent<MotionChannelCoordinator>();
            float deadline = Time.realtimeSinceStartup + 22;
            while ((routine.IsRunning || channels.IsNavigationActive) && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(routine.IsRunning || channels.IsNavigationActive, Is.False, "Rest must be idle before requesting Work.");
            Place(merchant.transform.position + merchant.transform.forward * 1.3f);
            Assert.That(dialogue.SelectTarget(merchant.GetComponent<DialogueParticipant>()), Is.True);
            yield return Wait(1.2f);
            var request = OwnPlayerRequest("休息得差不多了吧，回来工作吧。");
            Assert.That(admission.IsCognitiveModeTransitionBlocked(orchestrator.CurrentRun), Is.False,
                "Player conversation fixture must be idle: " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    nav=channels.IsNavigationActive, interaction=channels.IsInteractionMotionActive,
                    task=channels.IsFormalTaskMotionActive, carry=channels.IsCarryPresentationActive,
                    gesture=channels.ActiveGestureId, speech=merchant.GetComponent<SpeechPresentationLifecycle>().IsSpeaking }));
            // Existing real sample D: a mode-only decision also promised an unsubmitted latte.
            AssertModeReply(request, ModeResponse(request, "work", "好呀，我这就回来。拿铁要热的吧？先给我两分钟。"),
                DailyBehaviorMode.Work, "好，那我接着忙了。");
        }

        [UnityTest] public IEnumerator ModeReplyNaturalPolish_T3_ContradictionAndFailedCommitStayTruthful()
        {
            Prepare(); Place(new Vector3(-5, .03f, -4)); yield return Wait(1.2f);
            var request = OwnPlayerRequest("现在好像没什么要忙的，你要不要先去休息一会儿？");
            // Existing real sample B: Rest was selected while the text declined to rest.
            AssertModeReply(request, ModeResponse(request, "rest", "我还不累呀，店里待着就好。你要是想安静坐会儿，我一会儿不打扰你。"),
                DailyBehaviorMode.Rest, "好，那我先歇会儿。");
            merchant.GetComponent<SpeechPresentationLifecycle>().Cancel(); yield return Wait(1.2f);
            var expired = OwnPlayerRequest("回来工作吧。"); int modeRevision = daily.ModeRevision;
            typeof(DailyBehaviorController).GetField("capturedAt", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(daily, Time.unscaledTimeAsDouble - 31);
            AssertModeReply(expired, ModeResponse(expired, "work", "我已经回到吧台，拿铁也做好了。"),
                DailyBehaviorMode.Rest, "我还在休息，这会儿先照旧。");
            Assert.That(daily.ModeRevision, Is.EqualTo(modeRevision));
        }

        [UnityTest] public IEnumerator P2BModeOnly_OriginalRealPromise_IsReplacedWithoutCreatingAction()
        {
            yield return PrepareRestForCombinedAction();
            var request = OwnPlayerRequest("薇薇，可以先结束休息，帮我拿个杯子吗？");
            var driver = merchant.GetComponent<CognitiveRunDriver>();
            string shown = null; driver.UtterancePresented += value => shown = value.Utterance;
            int revision = world.WorldRevision;
            var failedSample = ModeResponse(request, "work", "好呀，休息先放一放。杯子我这就去拿，稍等一下。");
            Assert.That(driver.TryConsumeResponse(failedSample), Is.True);
            Assert.That(shown, Is.EqualTo("好，那我接着忙了。"));
            Assert.That(failedSample.decision.utterance, Is.EqualTo("好呀，休息先放一放。杯子我这就去拿，稍等一下。"));
            Assert.That(driver.LastValidatedGoalProposal, Is.Null);
            Assert.That(driver.HasPendingActionOutcome || merchant.GetComponent<GoalPlanRunner>().HasActiveTask, Is.False);
            Assert.That(world.WorldRevision, Is.EqualTo(revision)); AssertOffline();
        }
    }
}
