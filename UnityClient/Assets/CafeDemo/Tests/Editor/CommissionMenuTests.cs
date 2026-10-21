using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue.Presentation;
using EmbodiedNPC.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CommissionMenuFakeTransport : BackendWebSocketClient
    {
        public readonly List<TaskRuntimeSyncRequestDto> Reads = new();
        public readonly List<JObject> Controls = new();
        public override bool IsConnected => true;
        public override bool EnsureConnected() => false;
        public override Task<bool> SendTaskRuntimeSyncAsync(TaskRuntimeSyncRequestDto request) { Reads.Add(request); return Task.FromResult(true); }
        public override Task<bool> SendTaskExecutionControlAsync(JObject control) { Controls.Add((JObject)control.DeepClone()); return Task.FromResult(true); }
    }

    // UI + real bridge validation, with an in-memory transport. No model, database or preference writes.
    public sealed class CommissionMenuFixture : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> roots = new();
        private readonly object previousVisit;
        private readonly CharacterDefinition character;
        private readonly bool ownsScene;
        public readonly Scene Scene;
        public readonly CafeUiFlowController Flow;
        public readonly TaskRuntimeBridge Bridge;
        public readonly CommissionMenuFakeTransport Transport;
        public readonly ActionFeedbackPresenter Presenter;
        public readonly WorldEntity Owner, Player;
        public GameObject Make(string name) { var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, Scene); roots.Add(go); return go; }
        public static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        public static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
        public static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
        public Button Button(string name) => Flow.GetComponentsInChildren<Button>(true).Single(b => b.name == name);
        public CommissionMenuFixture(bool preview = false)
        {
            previousVisit = typeof(TaskRuntimeBridge).GetField("processVisit", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            typeof(TaskRuntimeBridge).GetMethod("ResetProcess", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            ownsScene = preview;
            Scene = preview ? EditorSceneManager.NewPreviewScene() : SceneManager.GetActiveScene();
            Owner = Make("isolated-commission-npc").AddComponent<WorldEntity>();
            Owner.Configure("npc_cafe_01", "character.synthetic", EntityKind.Agent, AgentRole.Npc, new PersistentIdentityRef("npc", "Character", "synthetic-menu-npc"));
            Player = Make("isolated-commission-player").AddComponent<WorldEntity>();
            Player.Configure("player_synthetic", "character.synthetic_player", EntityKind.Agent, AgentRole.Player, new PersistentIdentityRef("player", "Player", "synthetic-menu-player"));
            character = ScriptableObject.CreateInstance<CharacterDefinition>();
            character.Configure("cafe.merchant", "林薇薇", "Synthetic", "Synthetic", "Independent", "Brief", "No invented facts");
            var composer = Owner.gameObject.AddComponent<CognitiveContextComposer>();
            Set(composer, "characterDefinition", character);
            Set(composer, "characterOverrides", new CharacterOverrideStore(
                Path.Combine(Path.GetTempPath(), "CommissionUi-" + Guid.NewGuid().ToString("N"), "characters.json")));
            var registry = Make("isolated-commission-registry").AddComponent<EntityRegistry>();
            Set(registry, "autoDiscoverSceneEntities", false); Set(registry, "logRegistrationsOnStart", false);
            Set(registry, "entities", new List<WorldEntity> { Owner, Player }); registry.RebuildRegistry();
            var transportRoot = Make("isolated-commission-fake"); transportRoot.SetActive(false);
            Transport = transportRoot.AddComponent<CommissionMenuFakeTransport>();
            typeof(BackendWebSocketClient).GetField("connectOnStart", Private).SetValue(Transport, false);
            transportRoot.SetActive(true);
            Bridge = Make("isolated-commission-bridge").AddComponent<TaskRuntimeBridge>();
            Assert.IsTrue(Bridge.Admit(registry, Player)); Bridge.Register(Owner, Transport);
            Presenter = Make("isolated-commission-feedback").AddComponent<ActionFeedbackPresenter>();
            Set(Presenter, "player", Player);
            var menu = Object.Instantiate(Resources.Load<GameObject>(CafeUiFlowController.MenuResource));
            SceneManager.MoveGameObjectToScene(menu, Scene); roots.Add(menu);
            Flow = menu.GetComponent<CafeUiFlowController>(); Call(Flow, "Awake");
            Set(Flow, "taskRuntime", Bridge); Set(Flow, "ownsInput", true);
            ((GameObject)Get(Flow, "menuSurface")).SetActive(true); Flow.RefreshTaskFeedback();
        }
        public static JObject Status(string state = "waiting", int version = 1, bool requested = false, string source = "player_request") => new()
        {
            ["task_id"] = "task_ui_synthetic", ["version"] = version, ["status"] = state, ["source"] = source,
            ["goal_kind"] = "serve_drink", ["attempt_id"] = state == "executing" || state == "completed" ? "attempt_ui_synthetic" : null,
            ["phase"] = state == "executing" ? "started" : state == "completed" ? "succeeded" : null,
            ["cancel_requested"] = requested, ["diagnostic"] = null, ["condition"] = null
        };
        public void Snapshot(JObject status = null)
        {
            Bridge.RefreshStatus(Owner.EntityId); var request = Transport.Reads.Last();
            var message = JObject.FromObject(new { message_type = "task_runtime_snapshot", schema_version = "1.1", request_id = request.request_id,
                agent_entity_id = Owner.EntityId, task_runtime = request.task_runtime,
                owner = new { identity_namespace = "npc", identity_type = "Character", persistent_id = "synthetic-menu-npc" },
                player = new { identity_namespace = "player", identity_type = "Player", persistent_id = "synthetic-menu-player" },
                tasks = status == null ? new JArray() : new JArray(status), opportunity_token = (string)null, task_ids = new string[0] });
            // Serialize only the protocol fields (TaskRuntimeContextDto has a computed IsValid property).
            ((JObject)message["task_runtime"]).Remove("IsValid");
            Assert.IsTrue(Transport.ProcessIncomingJson(message.ToString())); Flow.RefreshTaskFeedback();
        }
        public JObject Acknowledge(string outcome, JObject task)
        {
            Call(Bridge, "Update"); var command = Transport.Controls.Single(); var request = command["runtime_request"];
            var observer = request["recall_identity"]["observer"];
            var result = new JObject { ["message_type"] = "task_cancel_result", ["schema_version"] = "1.1", ["request_id"] = request["request_id"],
                ["command_id"] = request["task_runtime"]["command_id"], ["agent_entity_id"] = Owner.EntityId, ["task_runtime"] = request["task_runtime"].DeepClone(),
                ["owner"] = new JObject { ["identity_namespace"] = observer["identity_namespace"], ["identity_type"] = observer["identity_type"], ["persistent_id"] = observer["persistent_id"] },
                ["player"] = new JObject { ["identity_namespace"] = "player", ["identity_type"] = "Player", ["persistent_id"] = "synthetic-menu-player" },
                ["accepted"] = true, ["outcome"] = outcome, ["task"] = task, ["error_code"] = null };
            Assert.IsTrue(Transport.ProcessIncomingJson(result.ToString())); Flow.RefreshTaskFeedback(); return result;
        }
        public void Dispose()
        {
            var font = Get(Flow, "runtimeFont") as TMP_FontAsset;
            Set(Flow, "ownsInput", false); Set(Flow, "runtimeFont", null);
            foreach (var root in roots) if (root != null) Object.DestroyImmediate(root);
            if (font != null) { foreach (var texture in font.atlasTextures) if (texture != null) Object.DestroyImmediate(texture);
                if (font.material != null) Object.DestroyImmediate(font.material); Object.DestroyImmediate(font); }
            Object.DestroyImmediate(character); if (ownsScene) EditorSceneManager.ClosePreviewScene(Scene);
            typeof(TaskRuntimeBridge).GetMethod("ResetRetained", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            typeof(TaskRuntimeBridge).GetField("processVisit", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, previousVisit);
        }
    }

    public sealed class CommissionMenuTests
    {
        [Test] public void NoTaskKeepsFourPrimaryButtonsAndNoEntry()
        {
            using var f = new CommissionMenuFixture(); f.Snapshot();
            Assert.IsFalse(f.Button("CurrentCommission").gameObject.activeSelf);
            var main = (GameObject)CommissionMenuFixture.Get(f.Flow, "menuSurface");
            CollectionAssert.AreEquivalent(new[] { "Resume", "Settings", "Controls", "ExitGame" },
                main.GetComponentsInChildren<Button>().Select(b => b.name));
            Assert.IsEmpty(CafeUiFlowController.TaskFeedbackText("cancelled"));
            Assert.IsEmpty(CafeUiFlowController.TaskFeedbackText("completed"));
        }
        [Test] public void WaitingRequiresConfirmationAndKeepDoesNotCancel()
        {
            using var f = new CommissionMenuFixture(); f.Snapshot(CommissionMenuFixture.Status());
            f.Button("CurrentCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke();
            Assert.IsTrue((bool)CommissionMenuFixture.Get(f.Flow, "commissionConfirming"));
            Assert.IsFalse(f.Bridge.HasPendingResults); f.Button("CommissionBack").onClick.Invoke();
            Assert.IsFalse((bool)CommissionMenuFixture.Get(f.Flow, "commissionConfirming"));
            Assert.IsEmpty(f.Transport.Controls); Assert.IsFalse(f.Bridge.HasPendingResults);
            Assert.IsTrue((bool)CommissionMenuFixture.Call(f.Flow, "CloseCommissionLayer"));
            Assert.IsTrue(((GameObject)CommissionMenuFixture.Get(f.Flow, "menuSurface")).activeSelf);
            Assert.IsFalse((bool)CommissionMenuFixture.Call(f.Flow, "CloseCommissionLayer"));
        }
        [Test] public void ConfirmUsesTrustedBridgeAndOnlyAcknowledgedCancellationToastsOnce()
        {
            using var f = new CommissionMenuFixture(); f.Snapshot(CommissionMenuFixture.Status());
            f.Button("CurrentCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke();
            Assert.IsTrue(f.Bridge.HasPendingResults); Assert.IsFalse(f.Button("CancelPlayerCommission").gameObject.activeSelf);
            Assert.AreEqual(0, f.Presenter.PendingNotificationCount);
            f.Button("CancelPlayerCommission").onClick.Invoke(); // even a programmatic duplicate is ignored
            var result = f.Acknowledge("cancelled", CommissionMenuFixture.Status("cancelled", 2));
            Assert.AreEqual(1, f.Transport.Controls.Count); Assert.AreEqual(1, f.Presenter.PendingNotificationCount);
            Assert.IsFalse(f.Bridge.HasPendingResults); Assert.IsFalse(f.Button("CurrentCommission").gameObject.activeSelf);
            Assert.IsTrue(f.Transport.ProcessIncomingJson(result.ToString())); f.Flow.RefreshTaskFeedback();
            CommissionMenuFixture.Call(f.Bridge, "OnLifecycleConnection", "CONNECTED");
            f.Snapshot(CommissionMenuFixture.Status()); // old response cannot resurrect a completed cancellation
            Assert.AreEqual(1, f.Presenter.PendingNotificationCount); Assert.IsFalse(f.Button("CancelPlayerCommission").gameObject.activeSelf);
        }
        [TestCase("executing", "cancellation_requested")]
        [TestCase("completed", "too_late")]
        public void PendingOrTooLateNeverClaimsCancellationSuccess(string state, string outcome)
        {
            using var f = new CommissionMenuFixture(); f.Snapshot(CommissionMenuFixture.Status("executing"));
            f.Button("CurrentCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke();
            f.Acknowledge(outcome, CommissionMenuFixture.Status(state, 2, state == "executing"));
            Assert.AreEqual(0, f.Presenter.PendingNotificationCount); Assert.IsFalse(f.Button("CancelPlayerCommission").gameObject.activeSelf);
            Assert.IsNotEmpty(CafeUiFlowController.TaskFeedbackText(f.Bridge.FeedbackCode));
            Assert.IsTrue(f.Button("CurrentCommission").gameObject.activeSelf, "Important feedback must remain accessible.");
        }
        [TestCase("blocked", true)][TestCase("completed", false)][TestCase("cancelled", false)]
        public void PermissionComesFromExistingCandidate(string state, bool allowed)
        {
            using var f = new CommissionMenuFixture(); f.Snapshot(CommissionMenuFixture.Status(state));
            f.Button("CurrentCommission").onClick.Invoke();
            Assert.AreEqual(allowed, f.Button("CancelPlayerCommission").gameObject.activeSelf);
        }
        [Test] public void AsyncVersionChangeInvalidatesConfirmationAndEscUnwindsOneLayer()
        {
            using var f = new CommissionMenuFixture(); f.Snapshot(CommissionMenuFixture.Status());
            f.Button("CurrentCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke();
            f.Snapshot(CommissionMenuFixture.Status("waiting", 2));
            Assert.IsFalse((bool)CommissionMenuFixture.Get(f.Flow, "commissionConfirming")); Assert.IsFalse(f.Bridge.HasPendingResults);
            f.Button("CancelPlayerCommission").onClick.Invoke();
            CommissionMenuFixture.Call(f.Flow, "CloseCommissionLayer");
            Assert.IsFalse((bool)CommissionMenuFixture.Get(f.Flow, "commissionConfirming"));
            Assert.IsTrue(((GameObject)CommissionMenuFixture.Get(f.Flow, "commissionSurface")).activeSelf);
            CommissionMenuFixture.Call(f.Flow, "CloseCommissionLayer");
            Assert.IsFalse(((GameObject)CommissionMenuFixture.Get(f.Flow, "commissionSurface")).activeSelf);
            Assert.IsTrue(f.Flow.IsMenuOpen); Assert.IsFalse(f.Bridge.HasPendingResults);
        }
        [Test] public void EscConsumesOneLayerAndIgnoresDuplicatePressInSameFrame()
        {
            using var f = new CommissionMenuFixture(); f.Snapshot(CommissionMenuFixture.Status());
            var host = f.Make("isolated-inactive-settings"); host.SetActive(false);
            CommissionMenuFixture.Set(f.Flow, "settings", host.AddComponent<CafeDemoDeveloperPanel>());
            f.Button("CurrentCommission").onClick.Invoke(); f.Button("CancelPlayerCommission").onClick.Invoke();
            CommissionMenuFixture.Set(f.Flow, "bound", true);
            try
            {
                f.Flow.RequestCancel(); f.Flow.RequestCancel();
                Assert.IsFalse((bool)CommissionMenuFixture.Get(f.Flow, "commissionConfirming"));
                Assert.IsTrue(((GameObject)CommissionMenuFixture.Get(f.Flow, "commissionSurface")).activeSelf);
                CommissionMenuFixture.Set(f.Flow, "cancelFrame", -1); f.Flow.RequestCancel();
                Assert.IsFalse(((GameObject)CommissionMenuFixture.Get(f.Flow, "commissionSurface")).activeSelf);
                Assert.IsTrue(f.Flow.IsMenuOpen); Assert.IsFalse(f.Bridge.HasPendingResults);
            }
            finally { CommissionMenuFixture.Set(f.Flow, "bound", false); }
        }
        [Test] public void ErrorsRemainAccessibleAndSelfInitiatedTasksCannotBeCancelled()
        {
            using var f = new CommissionMenuFixture();
            f.Snapshot(CommissionMenuFixture.Status(source: "self_initiated"));
            Assert.IsFalse(f.Button("CurrentCommission").gameObject.activeSelf);
            foreach (string code in new[] { "save_failed", "failed", "needs_verification", "cancel_pending", "result_pending", "too_late" })
            {
                CommissionMenuFixture.Set(f.Bridge, "<FeedbackCode>k__BackingField", code); f.Flow.RefreshTaskFeedback();
                Assert.IsTrue(f.Button("CurrentCommission").gameObject.activeSelf, code);
                f.Button("CurrentCommission").onClick.Invoke();
                Assert.IsFalse(f.Button("CancelPlayerCommission").gameObject.activeSelf);
                var body = (TMP_Text)CommissionMenuFixture.Get(f.Flow, "taskStatus");
                Assert.That(body.text, Does.Contain(CafeUiFlowController.TaskFeedbackText(code)));
                f.Button("CommissionBack").onClick.Invoke();
            }
        }
    }
}
