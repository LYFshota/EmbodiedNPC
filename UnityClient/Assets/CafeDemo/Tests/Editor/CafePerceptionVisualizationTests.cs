using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using Newtonsoft.Json;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafePerceptionVisualizationUnitTests
    {
        [Test] public void StatesAreEntityKeyedAndRemainWhenToolIsSuspended()
        {
            var root = new GameObject("PerceptionToolTest");
            try
            {
                var tool = root.AddComponent<CafePerceptionDebugVisualizer>();
                WorldEntity Entity(string id)
                {
                    var go = new GameObject(id); go.transform.SetParent(root.transform);
                    var entity = go.AddComponent<WorldEntity>(); entity.Configure(id, "test", EntityKind.Agent, AgentRole.Npc);
                    return entity;
                }
                var entities = Enumerable.Range(0, 4).Select(i => Entity("debug_npc_" + i)).ToArray();
                for (int i = 0; i < entities.Length; i++)
                {
                    tool.SetSelection(entities[i], null, null, null);
                    Assert.IsFalse(tool.SelectionState.EntityIds);
                    tool.SetVisibility(i == 0, i == 1, i == 2, i == 3, i % 2 == 0);
                }
                for (int i = 0; i < entities.Length; i++)
                {
                    tool.SetSelection(entities[i], null, null, null);
                    var state = tool.SelectionState;
                    Assert.AreEqual(i == 0, state.Vision); Assert.AreEqual(i == 1, state.Proximity);
                    Assert.AreEqual(i == 2, state.EntityIds); Assert.AreEqual(i == 3, state.PerceivedEntities);
                    Assert.AreEqual(i % 2 == 0, state.Hearing);
                }
                tool.SetToolActive(false); Assert.IsFalse(tool.CanRenderOverlays);
                tool.SetToolActive(true); Assert.IsTrue(tool.SelectionState.PerceivedEntities);
                // A new component with the same stable ID recovers its projection.
                tool.SetSelection(Entity("debug_npc_2"), null, null, null);
                Assert.IsTrue(tool.SelectionState.EntityIds);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test] public void MissingAndDisabledSensorsAreDifferentAndCleanupReleasesLines()
        {
            var root = new GameObject("PerceptionSensorTest");
            try
            {
                var entity = root.AddComponent<WorldEntity>(); entity.Configure("debug_npc", "test", EntityKind.Agent, AgentRole.Npc);
                var tool = root.AddComponent<CafePerceptionDebugVisualizer>();
                tool.SetSelection(entity, null, null, null);
                Assert.IsFalse(tool.CanShowProximity);
                Assert.That(tool.SensorSummary, Does.Contain(SettingsUiLanguage.T("未配置近距传感器", "No proximity sensor configured")));
                var sensor = root.AddComponent<ProximitySensor>();
                tool.SetSelection(entity, root.GetComponent<PerceivedStateStore>(), null, sensor);
                Assert.IsTrue(tool.CanShowProximity);
                tool.SetVisibility(false, true, false, false);
                sensor.enabled = false;
                Assert.IsFalse(tool.CanShowProximity);
                Assert.That(tool.SensorSummary, Does.Contain(SettingsUiLanguage.T("已禁用", "Disabled")));
                var line = root.GetComponentInChildren<LineRenderer>();
                // EditMode does not guarantee MonoBehaviour destruction messages for a never-awakened fixture.
                typeof(CafePerceptionDebugVisualizer).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tool, null);
                Object.DestroyImmediate(tool);
                Assert.IsTrue(line == null);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }

    /// <summary>Real scene/UGUI/IMGUI/LineRenderer acceptance with isolated, offline startup prerequisites.
    /// The fixture never changes a model config, formal character file, or production sensor setting.</summary>
    public sealed class CafePerceptionVisualizationPlayTests
    {
        private const string Prefix = "PerceptionVisualizationTest.";
        private const string LanguageKey = "EmbodiedNPC.Settings.Language";
        private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static string Root => Environment.GetEnvironmentVariable("EMBODIEDNPC_PERCEPTION_TEST_ROOT");
        private static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>(FindObjectsInactive.Include);
        private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private).GetValue(owner);
        private CafeDemoDeveloperPanel host;
        private CafeNpcDebugPanel panel;
        private CafeNpcCharacterSettings characters;
        private CafePerceptionDebugVisualizer tool;
        private CafeUiFlowController flow;
        private readonly List<string> checks = new();

        [UnitySetUp] public IEnumerator SetUp()
        {
            Assert.IsFalse(Application.isPlaying);
            Assert.IsNotEmpty(Root, "Set an external evidence root; this UI fixture needs no Backend.");
            foreach (string key in new[] { "EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE", "EMBODIEDNPC_KNOWLEDGE_ADMIN_URL" })
            {
                string previous = Environment.GetEnvironmentVariable(key);
                SessionState.SetBool(Prefix + key + ".exists", previous != null);
                SessionState.SetString(Prefix + key, previous ?? "");
            }
            // A missing isolated credential fails closed even if domain reload restores an authored URL.
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE", Path.Combine(Root, "no-session.json"));
            Environment.SetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_ADMIN_URL", "http://127.0.0.1:18943");
            SessionState.SetString(Prefix + "scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            SessionState.SetBool(Prefix + "languageExists", PlayerPrefs.HasKey(LanguageKey));
            SessionState.SetInt(Prefix + "language", PlayerPrefs.GetInt(LanguageKey, 0));
            SessionState.SetInt(Prefix + "gameSize", GameSizeIndex);
            SessionState.SetString(Prefix + "addedGameSizes", "");
            SessionState.SetBool(Prefix + "gizmos", GameGizmos);
            GameGizmos = false; // Editor-only sensor gizmos are not part of a Standalone overlay.
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var transport = new SerializedObject(Find<BackendWebSocketClient>());
            transport.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:18943/ws";
            transport.FindProperty("connectOnStart").boolValue = false;
            transport.ApplyModifiedPropertiesWithoutUndo();
            var recorder = new SerializedObject(Find<RuntimeTraceRecorder>());
            recorder.FindProperty("outputDirectoryOverride").stringValue = Path.Combine(Root, "Traces");
            recorder.ApplyModifiedPropertiesWithoutUndo();
            yield return new EnterPlayMode();
            foreach (var driver in Object.FindObjectsByType<CognitiveRunDriver>()) driver.enabled = false;
            host = Find<CafeDemoDeveloperPanel>(); panel = Find<CafeNpcDebugPanel>();
            characters = Find<CafeNpcCharacterSettings>(); tool = Find<CafePerceptionDebugVisualizer>(); flow = Find<CafeUiFlowController>();
            // Like AnimationSceneTestIsolation, visual acceptance does not assert Backend startup.
            // Domain reload can restore the scene's authored URL. Stop transport and use the
            // existing startup seam rather than relaxing S01 or ever connecting to port 8000.
            var backend = Find<BackendWebSocketClient>();
            var disconnect = backend.DisconnectAsync(); while (!disconnect.IsCompleted) yield return null;
            backend.enabled = false;
            typeof(CafeDemoDeveloperPanel).GetField("startupTransportReady", Private).SetValue(host, (Func<bool>)(() => true));
            host.ApplyStartupConfig(new ModelConfigView { configured = true });
            Assert.IsFalse(host.IsStartupAdmissionHeld);
            SettingsUiLanguage.SetEnglish(true);
            host.Open(); host.ShowDebug(); yield return null;
        }

        [UnityTearDown] public IEnumerator TearDown()
        {
            foreach (var backend in Object.FindObjectsByType<BackendWebSocketClient>())
            { var close = backend.DisconnectAsync(); while (!close.IsCompleted) yield return null; }
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (SessionState.GetBool(Prefix + "languageExists", false)) PlayerPrefs.SetInt(LanguageKey, SessionState.GetInt(Prefix + "language", 0));
            else PlayerPrefs.DeleteKey(LanguageKey);
            PlayerPrefs.Save();
            foreach (string key in new[] { "EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE", "EMBODIEDNPC_KNOWLEDGE_ADMIN_URL" })
                Environment.SetEnvironmentVariable(key, SessionState.GetBool(Prefix + key + ".exists", false) ? SessionState.GetString(Prefix + key, "") : null);
            GameSizeIndex = SessionState.GetInt(Prefix + "gameSize", 0);
            RemoveFixtureGameSizes();
            GameGizmos = SessionState.GetBool(Prefix + "gizmos", true);
            string scene = SessionState.GetString(Prefix + "scene", "Assets/Scenes/CafeDemoV2.unity");
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(scene) ? "Assets/Scenes/CafeDemoV2.unity" : scene);
        }

        private Toggle Toggle(string field) => Field<Toggle>(panel, field);
        private void Click(string name)
        {
            var button = name == "Close" ? Field<Button>(host, "closeButton") :
                host.transform.parent.GetComponentsInChildren<Button>(true).Single(x => x.name == name && x.isActiveAndEnabled);
            Assert.IsTrue(button.isActiveAndEnabled && button.IsInteractable(), name);
            button.onClick.Invoke();
        }
        private void Snapshot(string name)
        {
            var entity = Field<WorldEntity>(tool, "selectedEntity");
            var camera = Field<Camera>(tool, "sceneCamera");
            var state = new { selected = entity == null ? null : entity.EntityId,
                visualizer = tool.isActiveAndEnabled, hostActive = tool.gameObject.activeInHierarchy,
                toolActive = Field<bool>(tool, "toolActive"), expected = tool.SelectionState,
                settingsOpen = host.IsOpen, menuOpen = flow.IsMenuOpen, blocked = host.BlocksWorldDebugOverlays,
                developer = host.PlayerNavigation.IsDeveloper, canRender = tool.CanRenderOverlays,
                vision = tool.IsVisionVisible, proximity = tool.IsProximityVisible, hearing = tool.IsHearingReferenceVisible,
                visionSensor = Field<VisionSensor>(tool, "vision") != null,
                proximitySensor = Field<ProximitySensor>(tool, "proximity") != null,
                onGuiEligible = tool.CanRenderOverlays && camera != null && (tool.ShowEntityIds || tool.ShowPerceivedEntities) };
            File.WriteAllText(Path.Combine(Root, name + ".json"), JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        private IEnumerator AssertRenderedPixels()
        {
            float timeScale = Time.timeScale;
            Texture2D shown = null, hidden = null;
            try
            {
                // Freeze the scene between captures: compare actual rendered pixels, not only intent flags.
                Time.timeScale = 0;
                yield return null; yield return new WaitForEndOfFrame();
                shown = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(Root, "05-game-restored-render.png"), shown.EncodeToPNG());
                var camera = Field<Camera>(tool, "sceneCamera");
                var entity = Field<WorldEntity>(tool, "selectedEntity");
                Vector3 Point(WorldEntity target) => camera.WorldToScreenPoint(target.GetComponent<PerceivableEntity>()?.VisualTargetPosition ?? target.transform.position);
                var p = Point(entity);
                Assert.Greater(p.z, 0);
                var labelRect = new Rect(p.x + 14, p.y - 16, 245, 28);
                var perception = Field<PerceivedStateStore>(tool, "perception");
                var registry = Field<EntityRegistry>(tool, "entityRegistry");
                var markerRects = new List<Rect>();
                foreach (var record in perception.Aggregator.Entities.Values)
                {
                    if (record.KnownFields.TryGetValue("visibility_state", out var state) && state == "forgotten") continue;
                    if (!registry.TryGetEntity(record.EntityId, out var target)) continue;
                    var at = Point(target);
                    if (at.z > 0 && at.x > 12 && at.x < Screen.width - 12 && at.y > 12 && at.y < Screen.height - 12)
                        markerRects.Add(new Rect(at.x - 11, at.y - 11, 22, 22));
                }
                tool.SetToolActive(false); yield return null; yield return new WaitForEndOfFrame();
                hidden = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(Root, "06-tool-suspended-render.png"), hidden.EncodeToPNG());
                int Count(Texture2D image, Rect region, Func<Color, bool> match)
                {
                    int count = 0;
                    for (int y = Mathf.Max(0, (int)region.yMin); y < Mathf.Min(image.height, (int)region.yMax); y++)
                        for (int x = Mathf.Max(0, (int)region.xMin); x < Mathf.Min(image.width, (int)region.xMax); x++)
                            if (match(image.GetPixel(x, y))) count++;
                    return count;
                }
                Func<Color, bool> yellow = c => c.r > .7f && c.g > .7f && c.b < .4f;
                Func<Color, bool> green = c => c.g > .7f && c.g > c.r * 1.4f && c.g > c.b * 1.4f;
                Func<Color, bool> cyan = c => c.g > .6f && c.b > .5f && c.r < .4f;
                int labels = Count(shown, labelRect, yellow) - Count(hidden, labelRect, yellow);
                int markers = markerRects.Sum(rect => Count(shown, rect, green) - Count(hidden, rect, green));
                var screen = new Rect(0, 0, shown.width, shown.height);
                int lines = Count(shown, screen, cyan) - Count(hidden, screen, cyan);
                File.WriteAllText(Path.Combine(Root, "rendered-pixels.json"), JsonConvert.SerializeObject(new { labels, markers, lines, markerRegions = markerRects.Count }, Formatting.Indented));
                Assert.Greater(labels, 5, "Selected NPC ID must produce visible yellow glyph pixels.");
                Assert.Greater(markers, 8, "Perceived entities must produce visible green marker pixels.");
                Assert.Greater(lines, 25, "Vision line must contribute actual cyan pixels.");
            }
            finally
            {
                Time.timeScale = timeScale; tool.SetToolActive(true);
                if (shown != null) Object.Destroy(shown); if (hidden != null) Object.Destroy(hidden);
            }
            yield return null;
        }

        [UnityTest] public IEnumerator Lifecycle_RealNavigationCloseAndEscapeRestoreRendering()
        {
            host = Find<CafeDemoDeveloperPanel>(); panel = Find<CafeNpcDebugPanel>();
            characters = Find<CafeNpcCharacterSettings>(); tool = Find<CafePerceptionDebugVisualizer>(); flow = Find<CafeUiFlowController>();
            Size(1920, 1080); yield return null;
            host.PlayerNavigation.SetDeveloper(false);
            Click("DeveloperToolsEntry"); Click("DeveloperTool1"); Click("PerceptionDebugNav");
            Select("npc_cafe_01");
            foreach (string field in new[] { "visionToggle", "proximityToggle", "entityIdsToggle", "perceivedEntitiesToggle", "hearingToggle" }) Toggle(field).isOn = true;
            yield return null; Snapshot("01-settings-open"); AssertHidden(); yield return Capture("01-settings-open");
            Click("Close"); yield return null;
            Snapshot("02-close-button"); yield return Capture("02-close-button");
            Assert.IsTrue(tool.CanRenderOverlays); Assert.IsTrue(tool.IsVisionVisible); Assert.IsTrue(tool.IsProximityVisible); Assert.IsTrue(tool.IsHearingReferenceVisible);
            host.Open(); Click("PerceptionDebugNav"); yield return null;
            Snapshot("03-reopened");
            foreach (string field in new[] { "visionToggle", "proximityToggle", "entityIdsToggle", "perceivedEntitiesToggle", "hearingToggle" }) Assert.IsTrue(Toggle(field).isOn);
            // Use the same Cancel routing as the real ESC input, including settings back navigation.
            for (int i = 0; i < 4 && host.IsOpen; i++) { flow.RequestCancel(); yield return null; }
            Snapshot("04-escape-closed"); yield return Capture("04-escape-closed");
            Assert.IsFalse(host.IsOpen);
            Assert.IsTrue(tool.CanRenderOverlays, "Closing settings must preserve visualization intent independently of settings navigation.");
            Assert.IsTrue(tool.IsVisionVisible); Assert.IsTrue(tool.IsProximityVisible); Assert.IsTrue(tool.IsHearingReferenceVisible);
            yield return AssertRenderedPixels();
            flow.OpenMenu(); yield return null; AssertHidden(); yield return Capture("07-esc-hidden");
            flow.OpenSettings(); Click("PerceptionDebugNav"); yield return null; AssertHidden();
            Click("Close"); yield return null; AssertHidden(); // The parent ESC menu is still open.
            flow.Resume(); yield return null;
            Snapshot("08-resume-from-parent-menu"); Assert.IsTrue(tool.IsVisionVisible); Assert.IsTrue(tool.IsProximityVisible); Assert.IsTrue(tool.IsHearingReferenceVisible);
            yield return Capture("08-resume-from-parent-menu");
            host.Open(); Click("PerceptionDebugNav");
            Select("npc_cafe_regular_01"); Assert.IsFalse(Toggle("visionToggle").isOn);
            Toggle("entityIdsToggle").isOn = true;
            Assert.IsFalse(Toggle("proximityToggle").interactable);
            Select("npc_cafe_01"); Assert.IsTrue(Toggle("visionToggle").isOn); Assert.IsTrue(Toggle("proximityToggle").isOn);
            Click("Close"); yield return null; Assert.IsTrue(tool.IsVisionVisible);
            host.Open(); Click("PerceptionDebugNav");
            Click("BackToPlayerSettings"); Click("Close"); yield return null;
            AssertHidden(); Snapshot("09-explicit-developer-exit");
        }
        private void Select(string id)
        {
            int index = Enumerable.Range(0, characters.CandidateCount).Single(i => characters.GetCandidate(i).GetComponent<WorldEntity>().EntityId == id);
            characters.SelectCandidate(index); panel.RefreshNow();
            Assert.AreEqual(id, panel.SelectedEntityId);
        }
        private void AssertHidden()
        {
            Assert.IsFalse(tool.CanRenderOverlays, "IMGUI labels/markers must share the modal gate.");
            Assert.IsFalse(tool.IsVisionVisible); Assert.IsFalse(tool.IsProximityVisible);
            Assert.IsFalse(tool.IsHearingVisible); Assert.IsFalse(tool.IsHearingReferenceVisible);
        }
        private static IEnumerator Capture(string name)
        {
            Canvas.ForceUpdateCanvases(); yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(Root, name + ".png"));
            yield return null;
        }
        private static Type GameViewType => typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        private static bool GameGizmos
        {
            get => (bool)GameViewType.GetField("m_Gizmos", Private).GetValue(EditorWindow.GetWindow(GameViewType));
            set => GameViewType.GetField("m_Gizmos", Private).SetValue(EditorWindow.GetWindow(GameViewType), value);
        }
        private static int GameSizeIndex
        {
            get => (int)GameViewType.GetProperty("selectedSizeIndex", Private | BindingFlags.Public).GetValue(EditorWindow.GetWindow(GameViewType));
            set => GameViewType.GetProperty("selectedSizeIndex", Private | BindingFlags.Public).SetValue(EditorWindow.GetWindow(GameViewType), value);
        }
        private static void Size(int width, int height)
        {
            var assembly = typeof(EditorWindow).Assembly; var type = assembly.GetType("UnityEditor.GameViewSizes");
            var instance = typeof(ScriptableSingleton<>).MakeGenericType(type).GetProperty("instance").GetValue(null);
            var group = type.GetMethod("GetGroup").Invoke(instance, new[] { Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeGroupType"), "Standalone") });
            int total = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            for (int i = 0; i < total; i++)
            {
                object size = group.GetType().GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                if ((int)size.GetType().GetProperty("width").GetValue(size) == width && (int)size.GetType().GetProperty("height").GetValue(size) == height)
                {
                    string existingLabel = (string)size.GetType().GetProperty("baseText").GetValue(size);
                    if (existingLabel == "EmbodiedNPC.PerceptionFixture " + width + "x" + height)
                        SessionState.SetString(Prefix + "addedGameSizes", SessionState.GetString(Prefix + "addedGameSizes", "") + existingLabel + "\n");
                    GameSizeIndex = i; return;
                }
            }
            // Keep fixed-resolution prerequisites inside this existing fixture; restore them on teardown.
            string label = "EmbodiedNPC.PerceptionFixture " + width + "x" + height;
            var sizeType = assembly.GetType("UnityEditor.GameViewSize");
            var kind = Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeType"), "FixedResolution");
            var fixedSize = Activator.CreateInstance(sizeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { kind, width, height, label }, null);
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { fixedSize });
            SessionState.SetString(Prefix + "addedGameSizes", SessionState.GetString(Prefix + "addedGameSizes", "") + label + "\n");
            GameSizeIndex = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null) - 1;
        }
        private static void RemoveFixtureGameSizes()
        {
            var labels = SessionState.GetString(Prefix + "addedGameSizes", "").Split('\n');
            var assembly = typeof(EditorWindow).Assembly; var type = assembly.GetType("UnityEditor.GameViewSizes");
            var instance = typeof(ScriptableSingleton<>).MakeGenericType(type).GetProperty("instance").GetValue(null);
            var group = type.GetMethod("GetGroup").Invoke(instance, new[] { Enum.Parse(assembly.GetType("UnityEditor.GameViewSizeGroupType"), "Standalone") });
            int builtIn = (int)group.GetType().GetMethod("GetBuiltinCount").Invoke(group, null);
            int before = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null);
            var removed = new List<string>();
            for (int i = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null) - 1; i >= builtIn; i--)
            {
                object size = group.GetType().GetMethod("GetGameViewSize").Invoke(group, new object[] { i });
                string text = (string)size.GetType().GetProperty("baseText").GetValue(size);
                if (labels.Contains(text))
                {
                    group.GetType().GetMethod("RemoveCustomSize").Invoke(group, new object[] { i });
                    removed.Add(text);
                }
            }
            File.WriteAllText(Path.Combine(Root, "gameview-cleanup-" + TestContext.CurrentContext.Test.Name + ".json"),
                JsonConvert.SerializeObject(new { before, after = (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null), removed }));
            SessionState.SetString(Prefix + "addedGameSizes", "");
        }

        [UnityTest] public IEnumerator FourNpcOptionsModalsAndRealSensorProjection()
        {
            // Reacquire after the test runner's EnterPlayMode domain reload.
            Assert.IsTrue(Application.isPlaying);
            host = Find<CafeDemoDeveloperPanel>(); panel = Find<CafeNpcDebugPanel>();
            characters = Find<CafeNpcCharacterSettings>(); tool = Find<CafePerceptionDebugVisualizer>(); flow = Find<CafeUiFlowController>();
            Assert.AreEqual(4, characters.CandidateCount);
            var ids = new[] { "npc_cafe_01", "npc_cafe_regular_01", "npc_cafe_companion_01", "npc_cafe_quiet_01" };
            Size(1920, 1080); yield return null;
            for (int i = 0; i < ids.Length; i++)
            {
                Select(ids[i]); Assert.IsFalse(Toggle("visionToggle").isOn); Assert.IsFalse(Toggle("hearingToggle").isOn);
                Toggle("visionToggle").isOn = i % 2 == 0; Toggle("entityIdsToggle").isOn = i % 2 == 0;
                Toggle("perceivedEntitiesToggle").isOn = i % 2 != 0; Toggle("hearingToggle").isOn = i != 2;
                Toggle("proximityToggle").isOn = i == 0;
                Assert.AreEqual(i == 0, Toggle("proximityToggle").interactable);
                if (i != 0) Assert.That(tool.SensorSummary, Does.Contain("No proximity sensor configured"));
                yield return Capture("npc-" + i + "-settings-en-1920");
                host.CloseWindow(); yield return null;
                Assert.AreEqual(i % 2 == 0, tool.IsVisionVisible);
                Assert.AreEqual(i != 2, tool.IsHearingReferenceVisible);
                yield return Capture("npc-" + i + "-overlays-1920");
                host.Open(); host.ShowDebug();
            }
            for (int i = 3; i >= 0; i--)
            {
                Select(ids[i]); Assert.AreEqual(i % 2 == 0, Toggle("visionToggle").isOn);
                Assert.AreEqual(i % 2 != 0, Toggle("perceivedEntitiesToggle").isOn); Assert.AreEqual(i != 2, Toggle("hearingToggle").isOn);
                var thumb = Toggle("visionToggle").transform.Find("Background/SwitchThumb") as RectTransform;
                Assert.IsNotNull(thumb);
                Assert.AreEqual(i % 2 == 0 ? 1f : 0f, thumb.anchorMin.x);
            }
            checks.Add("Four NPC state sets and switching back: PASS");
            Select(ids[0]); host.CloseWindow(); yield return null;
            Assert.IsTrue(tool.IsVisionVisible); Assert.IsTrue(tool.IsProximityVisible); Assert.IsTrue(tool.IsHearingReferenceVisible);
            var proximity = characters.SelectedComposer.GetComponent<ProximitySensor>();
            proximity.enabled = false; host.Open(); host.ShowDebug(); panel.RefreshNow();
            Assert.IsFalse(Toggle("proximityToggle").interactable); Assert.That(tool.SensorSummary, Does.Contain("Disabled"));
            proximity.enabled = true; panel.RefreshNow(); host.CloseWindow(); yield return null;
            var line = tool.transform.Find("ProximityRange").GetComponent<LineRenderer>();
            Assert.That(Vector3.Distance(line.GetPosition(0), proximity.transform.position + Vector3.up * .05f), Is.EqualTo(proximity.Range).Within(.001f));
            var vision = characters.SelectedComposer.GetComponent<VisionSensor>();
            var visionLine = tool.transform.Find("VisionCone").GetComponent<LineRenderer>();
            Vector3 origin = vision.transform.position + Vector3.up * vision.EyeHeight;
            Assert.That(Vector3.Distance(visionLine.GetPosition(13), origin + vision.transform.forward * vision.EnterDistance), Is.LessThan(.01f));
            Assert.IsFalse(tool.HasSelectedSpeechReceipt);
            Assert.That(tool.HearingSummary, Does.Contain("Waiting for speech"));
            var reference = tool.transform.Find("HearingReference").GetComponent<LineRenderer>();
            Assert.That(Vector3.Distance(reference.GetPosition(0), characters.SelectedComposer.GetComponent<HearingSensor>().EarPosition),
                Is.EqualTo(Find<AudioStimulusRouter>().SpeechProfile.HearingRange).Within(.001f));
            yield return Capture("ranges-no-speech-1920");
            flow.OpenMenu(); yield return null; Assert.IsTrue(flow.IsMenuOpen); AssertHidden(); yield return Capture("esc-1920");
            flow.OpenHelp(); yield return null; AssertHidden(); yield return Capture("help-1920");
            flow.CloseHelp(); flow.OpenExitConfirmation(); yield return null; AssertHidden(); yield return Capture("exit-confirm-1920");
            flow.CancelExit();
            typeof(CafeUiFlowController).GetMethod("OpenCommissions", Private).Invoke(flow, null);
            yield return null; AssertHidden(); yield return Capture("commissions-1920");
            flow.OpenSettings(); host.ShowDebug(); yield return null; AssertHidden();
            Assert.IsTrue(Toggle("visionToggle").isOn); Assert.IsTrue(Toggle("hearingToggle").isOn);
            host.CloseWindow(); flow.Resume(); yield return null; Assert.IsTrue(tool.IsVisionVisible);
            checks.Add("ESC/help/exit/commissions/Settings hide all render paths and restore: PASS");
            var registry = Find<EntityRegistry>(); registry.TryGetEntity("player_01", out var speaker);
            var ear = characters.SelectedComposer.GetComponent<HearingSensor>().EarPosition;
            speaker.transform.position = ear + Vector3.right * .4f - Vector3.up * 1.5f;
            typeof(CafePerceptionDebugVisualizer).GetMethod("ObserveSpeech", Private).Invoke(tool, new object[] { "player_01", ids[3], "debug_visual_speech" });
            Assert.IsFalse(tool.HasSelectedSpeechReceipt, "A circle/event announcement is not a receipt.");
            var speech = new AgentEvent("debug_visual_speech", AgentEventTypes.SpeechPresented, "player_01", ids[3], "",
                new Dictionary<string, string> { ["text"] = "Isolated visual test.", ["parent_source_event_id"] = "debug_visual_parent" },
                Time.realtimeSinceStartupAsDouble, Math.Max(0, characters.SelectedComposer.GetComponent<PerceivedStateStore>().Aggregator.LatestWorldRevision), "debug_visual_trace");
            int applied = Find<AudioStimulusRouter>().RouteSpeech(speech, speaker.transform.position + Vector3.up * 1.5f);
            Assert.Greater(applied, 0); Assert.IsTrue(tool.HasSelectedSpeechReceipt);
            yield return null; Assert.IsTrue(tool.IsHearingVisible); yield return Capture("ranges-with-speech-1920");
            checks.Add("Ear/source circles use live SpeechProfile and receipt requires matching Audio Observation: PASS");
            host.Open(); host.ShowDebug(); Size(1280, 720); SettingsUiLanguage.SetEnglish(false); yield return null;
            Assert.That(tool.SensorSummary, Does.Contain("参考范围")); yield return Capture("settings-zh-1280");
            SettingsUiLanguage.SetEnglish(true); yield return Capture("settings-en-1280");
            host.CloseWindow(); yield return null; flow.OpenMenu(); yield return null; AssertHidden(); yield return Capture("esc-1280");
            flow.Resume(); host.Open(); host.ShowDebug();
            for (int i = 0; i < 3; i++) { host.CloseWindow(); host.Open(); host.ShowDebug(); }
            Assert.AreEqual(1, host.transform.parent.GetComponentsInChildren<Toggle>(true).Count(x => x.name == "HearingToggle"));
            host.PlayerNavigation.SetDeveloper(false); host.CloseWindow(); yield return null; AssertHidden();
            host.Open(); host.ShowDebug(); host.CloseWindow(); yield return null; Assert.IsTrue(tool.IsVisionVisible);
            tool.enabled = false; yield return null; AssertHidden();
            var material = Field<Material>(tool, "lineMaterial");
            var temporaryLines = tool.GetComponentsInChildren<LineRenderer>();
            Object.Destroy(tool); yield return null;
            Assert.IsTrue(material == null); Assert.IsTrue(temporaryLines.All(x => x == null));
            checks.Add("Repeated open/close, language/resolution, explicit developer exit and disable: PASS");
            File.WriteAllText(Path.Combine(Root, "playmode-checks.json"), JsonConvert.SerializeObject(checks, Formatting.Indented));
        }
    }
}
