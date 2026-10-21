using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeNpcDebugPanelTests
    {
        private CafeDemoDeveloperPanel panel;
        private CafeNpcCharacterSettings characters;
        private CafeNpcDebugPanel debug;
        private CafePerceptionDebugVisualizer visualizer;
        private Transform debugPage;
        private bool previousEnglish;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            previousEnglish = SettingsUiLanguage.IsEnglish;
            SettingsUiLanguage.SetEnglish(true);
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            yield return new EnterPlayMode();
            panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            characters = Object.FindAnyObjectByType<CafeNpcCharacterSettings>(FindObjectsInactive.Include);
            debug = Object.FindAnyObjectByType<CafeNpcDebugPanel>();
            visualizer = Object.FindAnyObjectByType<CafePerceptionDebugVisualizer>();
            debugPage = Field<GameObject>(debug, "debugPage").transform;
            panel.Open();
            panel.ShowDebug();
            Select("npc_cafe_01"); // Candidate ordering and previous UI selection are not fixture inputs.
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
            SettingsUiLanguage.SetEnglish(previousEnglish);
        }

        private static T Field<T>(object owner, string name)
        {
            var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            var value = (T)field.GetValue(owner);
            Assert.That(value, Is.Not.Null, name);
            return value;
        }

        private int CandidateIndex(string entityId)
        {
            for (int i = 0; i < characters.CandidateCount; i++)
                if (characters.GetCandidate(i).GetComponent<EmbodiedNPC.World.WorldEntity>().EntityId == entityId) return i;
            Assert.Fail("Missing formal scene NPC: " + entityId);
            return -1;
        }

        private void Select(string entityId)
        {
            characters.SelectCandidate(CandidateIndex(entityId));
            debug.RefreshNow();
            Assert.That(debug.SelectedEntityId, Is.EqualTo(entityId));
        }

        private FirstRunModelSetup ModelForm()
        {
            // Bind a version-2 Backend snapshot through the real form API. No API calls or saved settings.
            panel.ShowModelPresentation();
            var form = panel.ModelForm;
            var cloud = new ModelPresetView { preset_id = "fixture_cloud", name = "Fixture Cloud",
                source = "cloud", protocol = "openai_compatible", discovery = "manual", base_url = "https://fixture.invalid/v1" };
            var local = new ModelPresetView { preset_id = "fixture_local", name = "Fixture Local",
                source = "local", protocol = "ollama", discovery = "manual", base_url = "http://fixture.invalid:11434" };
            form.ApplyStartupResult(new ModelConfigView { configured = true, profiles = new ModelProfilesView {
                schema_version = 2, main_profiles = Array.Empty<MainProfileView>(), embedding_profiles = Array.Empty<EmbeddingProfileView>(),
                default_main_preset_id = cloud.preset_id, default_embedding_preset_id = cloud.preset_id,
                main_presets = new[] { cloud, local }, embedding_presets = new[] { cloud, local } } }, null);
            form.SetSettingsPage(ModelSettingsPage.Configuration);
            return form;
        }

        [UnityTest]
        public IEnumerator Settings_ExposeLanguageModesAndCheckedFeedback()
        {
            panel.PlayerNavigation.SetDeveloper(false);
            var language = panel.LanguageDropdown;
            Assert.That(language, Is.Not.Null);
            Assert.That(language.gameObject.activeInHierarchy, Is.True);
            language.value = 0;
            Assert.That(SettingsUiLanguage.IsEnglish, Is.False);
            // Runtime overview now owns separate label/value cells, rather than a prefixed value string.
            Assert.That(Field<GameObject>(panel, "overviewPage").GetComponentsInChildren<TMP_Text>(true)
                .Any(x => x.text == "后端"), Is.True);
            Assert.That(Field<TMP_Text>(panel, "backendText").text, Is.Not.Empty);
            var form = ModelForm();
            // Profile V2 and Editor restart presentation own these actions, not the retired three-button footer.
            foreach (var action in new[] { ("saveButton", "保存并应用配置"), ("recheckButton", "刷新配置 / 运行状态"), ("restartBackendButton", "Editor 本地后端重启") })
                Assert.That(Field<Button>(form, action.Item1).GetComponentInChildren<TMP_Text>(true).text, Is.EqualTo(action.Item2));
            // The current UI exposes provider profiles, rather than the hidden legacy Cloud/Local row.
            var supplier = Field<TMP_Dropdown>(form, "mainSupplier");
            Assert.That(supplier.gameObject.activeInHierarchy, Is.True);
            CollectionAssert.AreEquivalent(new[] { "Fixture Cloud", "Fixture Local" }, supplier.options.Select(x => x.text));
            var modelInput = Field<TMP_InputField>(form, "mainModel");
            var modelChoices = Field<TMP_Dropdown>(form, "mainCandidates");
            Assert.That(modelChoices.transform.parent, Is.EqualTo(modelInput.transform));
            Assert.That(modelChoices.template.rect.width, Is.GreaterThan(300));
            Assert.That(Field<TMP_Dropdown>(form, "embeddingSupplier"), Is.Not.SameAs(supplier));
            Assert.That(Field<Toggle>(form, "reuseMainApi"), Is.Not.Null);
            var window = Field<GameObject>(panel, "panelRoot").GetComponentsInChildren<Image>(true).Single(x => x.name == "Window");
            Assert.That(window.color.r, Is.GreaterThan(window.color.g));
            Assert.That(window.color.g, Is.GreaterThan(window.color.b));
            panel.ShowDebug();
            var vision = Field<Toggle>(debug, "visionToggle");
            // The formal themed switch uses an amber track and a moving thumb; green is the legacy checkbox style.
            vision.isOn = false;
            var thumb = vision.GetComponentsInChildren<RectTransform>(true).Single(x => x.name == "SwitchThumb");
            Assert.That(thumb.anchorMin.x, Is.EqualTo(0f));
            Assert.That(vision.targetGraphic.color, Is.EqualTo(SettingsPanelTheme.Muted));
            vision.isOn = true;
            Assert.That(thumb.gameObject.activeInHierarchy, Is.True);
            Assert.That(thumb.anchorMin.x, Is.EqualTo(1f));
            Assert.That(vision.targetGraphic.color, Is.EqualTo(SettingsPanelTheme.Accent));
            language.value = 1;
            Assert.That(SettingsUiLanguage.IsEnglish, Is.True);
            Assert.That(panel.PlayerNavigation.GeneralPage.GetComponentsInChildren<TMP_Text>(true)
                .Any(x => x.text == "General & controls"), Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator ModelSourceDropdown_ShowsRealOptionsAndClosesOnSecondClick()
        {
            var form = ModelForm();
            yield return null; // Let the real TMP dropdown finish its startup warmup.
            var dropdown = Field<TMP_Dropdown>(form, "mainSupplier");
            CollectionAssert.AreEquivalent(new[] { "Fixture Cloud", "Fixture Local" }, dropdown.options.Select(x => x.text));
            var hit = dropdown.GetComponentInChildren<SettingsDropdownClickToggle>(true);
            Assert.That(hit, Is.Not.Null);
            var click = new PointerEventData(EventSystem.current);
            hit.OnPointerClick(click);
            Assert.That(dropdown.IsExpanded, Is.True);
            var visible = dropdown.GetComponentsInChildren<TMP_Text>(true);
            Assert.That(Array.Exists(visible, x => x.gameObject.activeInHierarchy && x.text == "Fixture Cloud"), Is.True);
            Assert.That(Array.Exists(visible, x => x.gameObject.activeInHierarchy && x.text == "Option A"), Is.False);
            hit.OnPointerClick(click);
            yield return new WaitForSecondsRealtime(.25f);
            Assert.That(dropdown.IsExpanded, Is.False);
        }

        [UnityTest]
        public IEnumerator SelectedNpc_ProjectsEffectiveIdentityAndRuntimeState()
        {
            Assert.That(debugPage.gameObject.activeSelf, Is.True);
            Assert.That(debug.SelectedEntityId, Is.EqualTo("npc_cafe_01"));
            var definition = characters.SelectedComposer.CharacterDefinition;
            string effectiveName = SettingsUiLanguage.NpcDisplayName(definition.CharacterId,
                CharacterProfileResolver.Resolve(definition).display_name);
            Assert.That(debug.CurrentSelectedText, Does.Contain("Display Name: " + effectiveName));
            Assert.That(debug.CurrentSelectedText, Does.Contain("CharacterId: cafe.merchant"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("EntityId: npc_cafe_01"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("AgentRole: Npc"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("Behavior Mode: working"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("Cognition Enabled: Yes"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("Current Run: Idle"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("Current Task: None"));
            yield break;
        }

        [UnityTest]
        public IEnumerator EffectiveDisplayName_UsesCurrentCharacterOverride()
        {
            var shared = typeof(CharacterOverrideStore).GetField("shared",
                BindingFlags.Static | BindingFlags.NonPublic);
            object previous = shared.GetValue(null);
            string directory = Path.Combine(Path.GetTempPath(), "p6-debug-" + Guid.NewGuid().ToString("N"));
            try
            {
                var overrides = new CharacterOverrideStore(Path.Combine(directory, CharacterOverrideStore.FileName));
                overrides.Save(characters.SelectedCharacterId, "阿明", "店员", "在咖啡馆工作", "温和", "回答简短", "不编造事实");
                shared.SetValue(null, overrides);
                debug.RefreshNow();
                Assert.That(debug.CurrentSelectedText, Does.Contain("Display Name: 阿明"));
                Assert.That(debug.CurrentSelectedText, Does.Contain("CharacterId: cafe.merchant"));
            }
            finally
            {
                shared.SetValue(null, previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield break;
        }

        [UnityTest]
        public IEnumerator Capabilities_ComeFromSelectedAgentProfiles()
        {
            foreach (string id in new[] { "npc_cafe_01", "npc_cafe_regular_01", "npc_cafe_companion_01", "npc_cafe_quiet_01" })
            {
                Select(id);
                var profile = characters.SelectedComposer.GetComponent<AgentActionCapabilityProfile>();
                Assert.That(profile, Is.Not.Null);
                Assert.That(profile.TryBuildManifest(out var manifest), Is.True);
                // Formal scene allowlists: only the merchant may carry, prepare drinks, or clean the counter.
                string[] expected = id == "npc_cafe_01" ? new[] { "bring_object", "prepare_drink", "clean_counter" } : Array.Empty<string>();
                CollectionAssert.AreEquivalent(expected, profile.AllowedActionIds);
                CollectionAssert.AreEquivalent(expected, manifest.available_actions.Select(x => x.action_id));
                string actions = expected.Length == 0 ? "[]" : string.Join(", ", manifest.available_actions.Select(x => x.action_id));
                Assert.That(debug.CurrentSelectedText, Does.Contain("Available Actions: " + actions));
            }
            yield break;
        }

        [UnityTest]
        public IEnumerator Perception_ProjectsOnlySelectedObserverStateAndLastTransition()
        {
            PerceivedStateStore store = characters.SelectedComposer.GetComponent<PerceivedStateStore>();
            int revision = Math.Max(0, store.Aggregator.LatestWorldRevision);
            double now = Time.unscaledTimeAsDouble;
            void Apply(string source, string subject, ObservationChannel channel, string field, string state,
                string name)
            {
                var fields = new Dictionary<string, string> { [field] = state, ["display_name"] = name };
                store.Apply(new Observation("p6_" + source, store.ObserverEntityId,
                    ObservationSourceType.Direct, "p6_" + source, channel, subject, fields,
                    Array.Empty<string>(), 1f, revision, now, double.PositiveInfinity));
            }
            Apply("visible", "cup_01", ObservationChannel.Visual, "visibility_state", "current_visible", "Cup");
            Apply("nearby", "player_01", ObservationChannel.Proximity, "proximity_state", "nearby", "Player");
            Apply("remembered", "npc_cafe_regular_01", ObservationChannel.Visual,
                "visibility_state", "remembered", "RegularCustomer");
            debug.RefreshNow();
            Assert.That(debug.CurrentPerceptionText, Does.Contain("Cup · cup_01"));
            Assert.That(debug.CurrentPerceptionText, Does.Contain("Player · player_01"));
            Assert.That(debug.CurrentPerceptionText, Does.Contain("RegularCustomer · npc_cafe_regular_01 · remembered"));
            Assert.That(debug.CurrentPerceptionText, Does.Contain("visual · remembered · RegularCustomer"));
            yield break;
        }

        [UnityTest]
        public IEnumerator SelectionSwitch_KeepsPersonaAndDebugPagesOnTheSameNpc()
        {
            Field<TMP_Dropdown>(debug, "npcDropdown").value = CandidateIndex("npc_cafe_regular_01");
            Assert.That(characters.SelectedCharacterId, Is.EqualTo("cafe.regular_customer"));
            panel.ShowNpc();
            Assert.That(characters.SelectedCharacterId, Is.EqualTo("cafe.regular_customer"));
            Select("npc_cafe_01");
            panel.ShowDebug();
            Assert.That(debug.SelectedEntityId, Is.EqualTo("npc_cafe_01"));
            Assert.That(debug.CurrentSelectedText, Does.Contain("CharacterId: cafe.merchant"));
            yield break;
        }

        [UnityTest]
        public IEnumerator VisualizationToggles_OnlyChangeRenderers()
        {
            for (int i = 0; i < characters.CandidateCount; i++)
                if (characters.GetCandidate(i).GetComponent<EmbodiedNPC.World.WorldEntity>().EntityId == "npc_cafe_01")
                { characters.SelectCandidate(i); break; }
            PerceivedStateStore state = characters.SelectedComposer.GetComponent<PerceivedStateStore>();
            int before = state.Aggregator.PerceptionRevision;
            Toggle Control(string name) => (Toggle)typeof(CafeNpcDebugPanel).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(debug);
            Control("visionToggle").isOn = true;
            Control("proximityToggle").isOn = true;
            Control("entityIdsToggle").isOn = true;
            Control("perceivedEntitiesToggle").isOn = true;
            Assert.That(visualizer.IsVisionVisible, Is.False, "Settings is a modal surface.");
            Assert.That(visualizer.IsProximityVisible, Is.False);
            Assert.That(visualizer.ShowEntityIds, Is.True);
            Assert.That(visualizer.ShowPerceivedEntities, Is.True);
            Assert.That(state.Aggregator.PerceptionRevision, Is.EqualTo(before));
            Control("visionToggle").isOn = false;
            Control("proximityToggle").isOn = false;
            Assert.That(visualizer.IsVisionVisible, Is.False);
            Assert.That(visualizer.IsProximityVisible, Is.False);
            yield break;
        }
    }
}
