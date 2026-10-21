using System.Collections;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class CafeSettingsInputTests
    {
        private CafeDemoPlayerController player;
        private CafeDemoOrbitCamera orbit;
        private CafeDemoDeveloperPanel panel;
        private Keyboard keyboard;
        private Mouse mouse;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            var client = new SerializedObject(Object.FindAnyObjectByType<EmbodiedNPC.Communication.BackendWebSocketClient>());
            client.FindProperty("connectOnStart").boolValue = false;
            client.ApplyModifiedPropertiesWithoutUndo();
            yield return new EnterPlayMode();
            player = Object.FindAnyObjectByType<CafeDemoPlayerController>();
            orbit = Object.FindAnyObjectByType<CafeDemoOrbitCamera>();
            panel = Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            panel.ApplyStartupConfig(new ModelConfigView { configured = false });
            keyboard = InputSystem.AddDevice<Keyboard>("CafeSettingsTestKeyboard");
            mouse = InputSystem.AddDevice<Mouse>("CafeSettingsTestMouse");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (InputDevice device in InputSystem.devices.Where(x => x.name.StartsWith("CafeSettingsTest")).ToArray())
                InputSystem.RemoveDevice(device);
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private void SetEscape(bool pressed)
        {
            InputSystem.QueueStateEvent(keyboard, pressed ? new KeyboardState(Key.Escape) : new KeyboardState());
            InputSystem.Update();
            // EditMode batch tests process synthetic input after the player Update loop.
            panel.SendMessage("Update");
        }

        [UnityTest]
        public IEnumerator Dialogue_SettingsEntryThenEscape_PreservesInputAndRestoresGameplay()
        {
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            panel.Close();
            var character = player.GetComponent<CharacterController>();
            character.enabled = false;
            player.transform.position = new Vector3(-.8f, .02f, 1.25f);
            character.enabled = true;
            yield return null;
            var interactor = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
            var dialogue = Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.DialogueController>();
            var mode = Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.Input.DialogueInputModeController>();
            var input = Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.Input.TextUtteranceInput>();
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            input.RestoreTextIfEmpty("settings draft");
            string conversation = dialogue.CurrentConversationId;
            yield return null;
            Canvas.ForceUpdateCanvases();
            var entry = (Button)new SerializedObject(panel).FindProperty("entryButton").objectReferenceValue;
            var rect = (RectTransform)entry.transform;
            Vector2 pointer = RectTransformUtility.WorldToScreenPoint(entry.GetComponentInParent<Canvas>().worldCamera,
                rect.TransformPoint(rect.rect.center));
            // Exercise both possible Update/EventSystem orders for the same settings click.
            Assert.That(mode.TryExitAtPointer(pointer), Is.False);
            entry.onClick.Invoke();
            Canvas.ForceUpdateCanvases();
            Assert.That(mode.TryExitAtPointer(new Vector2(Screen.width * .5f, Screen.height * .5f)), Is.False);
            SetEscape(true);
            Assert.That(panel.IsOpen, Is.False);
            Assert.That(dialogue.IsPresentationVisible, Is.True);
            Assert.That(input.IsVisible, Is.True);
            Assert.That(input.CurrentText, Is.EqualTo("settings draft"));
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
            Assert.That(player.IsInputSuspended, Is.True);
            Assert.That(orbit.IsInputSuspended, Is.True);
            SetEscape(false);
            yield return null;
            SetEscape(true);
            Assert.That(dialogue.IsPresentationVisible, Is.False);
            Assert.That(panel.IsOpen, Is.False);
            SetEscape(false);
            yield return null;
            Assert.That(player.IsInputSuspended, Is.False);
            Assert.That(orbit.IsInputSuspended, Is.False);
            Assert.That(orbit.PointerCaptured, Is.True);
        }

        [UnityTest]
        public IEnumerator Dialogue_ClearedWhileSettingsOpen_DoesNotRestoreStaleSuspension()
        {
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            panel.Close();
            var character = player.GetComponent<CharacterController>();
            character.enabled = false;
            player.transform.position = new Vector3(-.8f, .02f, 1.25f);
            character.enabled = true;
            yield return null;
            Assert.That(player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>().TryBeginDialogue(), Is.True);
            panel.Open();
            Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.DialogueController>().ClearSession();
            panel.Close();
            Assert.That(player.IsInputSuspended, Is.False);
            Assert.That(orbit.IsInputSuspended, Is.False);
            Assert.That(orbit.PointerCaptured, Is.True);
        }

        [UnityTest]
        public IEnumerator SettingsModal_HidesNpcHintAndBlocksInteraction_RestoresOnClose()
        {
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            panel.Close();
            var character = player.GetComponent<CharacterController>();
            character.enabled = false;
            player.transform.position = new Vector3(-.8f, .02f, 1.25f);
            character.enabled = true;
            yield return null;
            var interactor = player.GetComponent<EmbodiedNPC.Dialogue.Input.PlayerDialogueInteractor>();
            interactor.RefreshCandidate();
            Assert.That(interactor.IsPromptVisible, Is.True);
            panel.Open();
            Assert.That(interactor.IsPromptVisible, Is.False);
            Assert.That(interactor.TryBeginDialogue(), Is.False);
            var root = panel.transform.parent.Find("DeveloperPanelRoot");
            var modal = root.GetComponent<Canvas>();
            Assert.That(modal.overrideSorting, Is.True);
            Assert.That(modal.sortingOrder, Is.GreaterThan(root.parent.GetComponentInParent<Canvas>().sortingOrder));
            var bubbles = Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.Presentation.DialogueBubbleManager>();
            bubbles.PushMessage("npc_cafe_01", "settings modal regression", EmbodiedNPC.Dialogue.Presentation.DialogueSpeakerKind.Npc, Time.unscaledTimeAsDouble);
            yield return null;
            Assert.That(modal.sortingOrder, Is.GreaterThan(bubbles.GetComponentInParent<Canvas>().sortingOrder));
            panel.Close();
            Assert.That(interactor.IsPromptVisible, Is.True);
            Assert.That(interactor.TryBeginDialogue(), Is.True);
            var dialogue = Object.FindAnyObjectByType<EmbodiedNPC.Dialogue.DialogueController>();
            string conversation = dialogue.CurrentConversationId;
            panel.Open(); panel.Close();
            Assert.That(dialogue.CurrentConversationId, Is.EqualTo(conversation));
            Assert.That(dialogue.IsPresentationVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator SettingsModal_ReopenRepairsBothScreenSpaceModes()
        {
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            var root = panel.transform.parent.Find("DeveloperPanelRoot");
            var modal = root.GetComponent<Canvas>();
            var parent = root.parent.GetComponentInParent<Canvas>();
            foreach (var mode in new[] { RenderMode.ScreenSpaceOverlay, RenderMode.ScreenSpaceCamera })
            {
                panel.Close();
                parent.renderMode = mode;
                parent.worldCamera = mode == RenderMode.ScreenSpaceCamera ? Camera.main : null;
                parent.planeDistance = 0.5f;
                modal.renderMode = RenderMode.WorldSpace;
                panel.Open();
                Assert.That(modal, Is.Not.Null);
                Assert.That(modal.renderMode, Is.EqualTo(mode));
                Assert.That(modal.worldCamera, Is.EqualTo(parent.worldCamera));
                Assert.That(modal.planeDistance, Is.EqualTo(parent.planeDistance));
                Assert.That(modal.overrideSorting, Is.True);
                Assert.That(modal.sortingOrder, Is.GreaterThan(parent.sortingOrder));
                Assert.That(root.GetComponent<GraphicRaycaster>(), Is.Not.Null);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator CreamSettings_NavigationScrollAndFooterSurviveReopening()
        {
            var root=panel.transform.parent.Find("DeveloperPanelRoot");
            var window=root.Find("Window");
            foreach(string name in new[]{"OverviewNav","ModelNav","NpcNav","DebugNav","TraceNav"})
            {
                CafeSettingsLayout.FindRuntime(window,name).GetComponent<Button>().onClick.Invoke();
                yield return null;
                int active=0;
                foreach(string page in new[]{"OverviewPage","ModelPage","NpcPage","DebugPage","TracePage"})
                    if(window.Find(page).gameObject.activeSelf)active++;
                Assert.That(active,Is.EqualTo(1),name);
            }
            panel.ShowModel();yield return null;
            var modelPage=window.Find("ModelPage");
            var scroll=modelPage.GetComponentInChildren<ScrollRect>();
            Canvas.ForceUpdateCanvases();
            Assert.That(scroll.content.rect.height,Is.GreaterThan(scroll.viewport.rect.height));
            var footer=modelPage.Find("SettingsFooter");
            Assert.That(footer,Is.Not.Null);
            Assert.That(footer.IsChildOf(scroll.content),Is.False);
            Vector3 footerPosition=footer.position;
            scroll.verticalNormalizedPosition=0;yield return null;
            Assert.That(footer.position,Is.EqualTo(footerPosition));
            int rows=root.GetComponentsInChildren<LayoutElement>(true).Length;
            panel.Close();panel.Open();panel.ShowModel();yield return null;
            Assert.That(root.GetComponentsInChildren<SettingsPanelTheme>(true).Length,Is.EqualTo(1));
            Assert.That(root.GetComponentsInChildren<LayoutElement>(true).Length,Is.EqualTo(rows));
            foreach(var icon in root.GetComponentsInChildren<SettingsNavigationIcon>(true))
                Assert.That(icon.GetComponent<CanvasRenderer>(),Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator CreamSettings_PreservesProviderVisibilityAndLanguageControls()
        {
            var root=panel.transform.parent.Find("DeveloperPanelRoot");
            var form=root.GetComponent<FirstRunModelSetup>();
            var serialized=new SerializedObject(form);
            var provider=(TMP_Dropdown)serialized.FindProperty("mainProvider").objectReferenceValue;
            var key=(TMP_InputField)serialized.FindProperty("mainApiKey").objectReferenceValue;
            provider.value=1;yield return null;
            Assert.That(key.transform.parent.gameObject.activeSelf,Is.False);
            provider.value=0;yield return null;
            Assert.That(key.transform.parent.gameObject.activeSelf,Is.True);
            Assert.That(key.contentType,Is.EqualTo(TMP_InputField.ContentType.Password));
            var temperature=(TMP_InputField)serialized.FindProperty("temperature").objectReferenceValue;
            var slider=CafeSettingsLayout.FindRuntime(root,"TemperatureSlider").GetComponent<Slider>();
            slider.value=.75f;
            Assert.That(temperature.text,Is.EqualTo("0.75"));
            temperature.text="1.25";
            Assert.That(slider.value,Is.EqualTo(1.25f));
            bool previous=SettingsUiLanguage.IsEnglish;
            try
            {
                panel.ShowOverview();yield return null;
                var language=CafeSettingsLayout.FindRuntime(root,"LanguageDropdown").GetComponent<TMP_Dropdown>();
                language.value=previous?0:1;yield return null;
                Assert.That(SettingsUiLanguage.IsEnglish,Is.EqualTo(!previous));
                panel.ShowNpc();yield return null;
                Assert.That(CafeSettingsLayout.FindRuntime(root,"NpcTitle").GetComponent<TMP_Text>().text,
                    Is.EqualTo(SettingsUiLanguage.IsEnglish?"NPC Character":"NPC 人设"));
            }
            finally { SettingsUiLanguage.SetEnglish(previous); }
        }

        [UnityTest]
        public IEnumerator ModelSettingsOnFirstRun_ReleasesCursorAndBlocksWorldInput()
        {
            Assert.That(panel.IsOpen, Is.True);
            Assert.That(panel.transform.parent.Find("DeveloperPanelRoot/Window/ModelPage").gameObject.activeSelf, Is.True);
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
            Assert.That(Cursor.visible, Is.True);
            Assert.That(orbit.PointerCaptured, Is.False);
            Assert.That(player.IsInputSuspended, Is.True);
            Assert.That(orbit.IsInputSuspended, Is.True);
            float yaw = orbit.Yaw;
            Vector3 position = player.transform.position;
            InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(250, 0) });
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
            yield return null;
            yield return null;
            Assert.That(orbit.Yaw, Is.EqualTo(yaw).Within(.01f));
            Assert.That(Vector3.ProjectOnPlane(player.transform.position - position, Vector3.up).magnitude,
                Is.LessThan(.01f));
        }

        [UnityTest]
        public IEnumerator Escape_OpensSettingsDuringGameplay_AndClosesBackToCapturedGameplay()
        {
            panel.ApplyStartupConfig(new ModelConfigView { configured = true });
            panel.Close();
            yield return null;
            Assert.That(panel.IsOpen, Is.False);
            Assert.That(orbit.PointerCaptured, Is.True);
            SetEscape(true);
            Assert.That(panel.IsOpen, Is.True);
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
            Assert.That(Cursor.visible, Is.True);
            Assert.That(player.IsInputSuspended, Is.True);
            Assert.That(orbit.IsInputSuspended, Is.True);
            float yaw = orbit.Yaw;
            InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(250, 0) });
            yield return null;
            Assert.That(orbit.Yaw, Is.EqualTo(yaw).Within(.01f));

            SetEscape(false);
            SetEscape(true);
            Assert.That(panel.IsOpen, Is.False);
            Assert.That(orbit.PointerCaptured, Is.True);
            Assert.That(player.IsInputSuspended, Is.False);
            Assert.That(orbit.IsInputSuspended, Is.False);
        }
    }
}
