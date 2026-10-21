using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CafeDemo.Tests
{
    public sealed class SettingsP1FocusTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private CafeDemoDeveloperPanel panel;
        private DialogueController dialogue;
        private TMP_InputField input;
        private Button entry;
        private EventSystem events, previousEvents;
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static object Get(object target, string field) => target.GetType().GetField(field, Private).GetValue(target);
        private void Call(string name) => typeof(CafeDemoDeveloperPanel).GetMethod(name, Private).Invoke(panel, null);
        private T Add<T>(string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(root.transform);
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        [SetUp] public void Setup()
        {
            root = new GameObject("IsolatedSettingsFocus", typeof(RectTransform)); root.SetActive(false);
            dialogue = Add<DialogueController>("Dialogue"); dialogue.enabled = false;
            var backend = Add<EmbodiedNPC.Communication.BackendWebSocketClient>("NoNetwork"); backend.enabled = false;
            Set(backend, "connectOnStart", false); Set(dialogue, "backendClient", backend);
            var adapter = Add<TextUtteranceInput>("TextInput");
            input = Add<TMP_InputField>("CurrentDialogueInput"); Set(adapter, "inputField", input); Set(dialogue, "textInput", adapter);
            panel = Add<CafeDemoDeveloperPanel>("SettingsOwner"); Set(panel, "dialogue", dialogue);
            foreach (string field in new[] { "entryButton", "closeButton", "overviewButton", "modelButton", "npcButton" }) Set(panel, field, Add<Button>(field));
            entry = (Button)Get(panel, "entryButton");
            Set(panel, "languageDropdown", Add<TMP_Dropdown>("Language"));
            Set(panel, "panelRoot", Add<RectTransform>("SettingsModal").gameObject);
            ((GameObject)Get(panel, "panelRoot")).SetActive(false);
            previousEvents = EventSystem.current; events = Add<EventSystem>("FocusEvents");
            root.SetActive(true);
            typeof(EventSystem).GetMethod("OnEnable", Private).Invoke(events, null);
            EventSystem.current = events;
            Set(dialogue, "activeContext", new DialogueContext { conversationId = "synthetic-conversation", speakerEntityId = "synthetic-player", targetAgentId = "synthetic-npc" });
            Set(dialogue, "selectedTarget", Add<DialogueParticipant>("CurrentTarget"));
            Set(dialogue, "presentationVisible", true);
            input.interactable = true;
        }

        private void FocusForPolicyTest()
        {
            events.SetSelectedGameObject(input.gameObject);
            // Deterministic focus policy only; actual TMP activation is checked in isolated Play Mode.
            Set(input, "m_AllowInput", true);
        }

        [TearDown] public void Cleanup()
        {
            if (events != null) typeof(EventSystem).GetMethod("OnDisable", Private).Invoke(events, null);
            Object.DestroyImmediate(root);
            if (previousEvents != null && previousEvents.isActiveAndEnabled) EventSystem.current = previousEvents;
        }

        [Test] public void SameVisibleConversation_RestoresOnlyPreviouslyFocusedInput()
        {
            FocusForPolicyTest(); Call("CaptureDialogueFocus");
            events.SetSelectedGameObject(null); Call("RestoreDialogueFocus");
            Assert.AreSame(input.gameObject, events.currentSelectedGameObject);
            Assert.IsNull(Get(panel, "dialogueFocusToRestore"));
        }

        [Test] public void EntryPointerSelection_PreservesImmediatelyPrecedingDialogueFocus()
        {
            FocusForPolicyTest(); Call("LateUpdate");
            events.SetSelectedGameObject(entry.gameObject); Set(input, "m_AllowInput", false);
            Call("CaptureDialogueFocus");
            Assert.AreSame(input, Get(panel, "dialogueFocusToRestore"));
            events.SetSelectedGameObject(null); Call("RestoreDialogueFocus");
            Assert.AreSame(input.gameObject, events.currentSelectedGameObject);
        }

        [Test] public void HeldEntryPointer_RetainsFocusOnlyDuringContinuousEntrySelection()
        {
            FocusForPolicyTest(); Call("LateUpdate");
            events.SetSelectedGameObject(entry.gameObject); Set(input, "m_AllowInput", false);
            Call("LateUpdate"); Call("LateUpdate"); Call("CaptureDialogueFocus");
            Assert.AreSame(input, Get(panel, "dialogueFocusToRestore"));
            events.SetSelectedGameObject(Add<Button>("UnrelatedSelection").gameObject);
            Call("LateUpdate"); events.SetSelectedGameObject(entry.gameObject);
            Call("CaptureDialogueFocus");
            Assert.IsNull(Get(panel, "dialogueFocusToRestore"));
        }

        [TestCase("hidden")] [TestCase("ended")] [TestCase("conversation")] [TestCase("target")]
        [TestCase("other-selection")] [TestCase("disabled-input")]
        public void ChangedDialogueOrFocus_DoesNotStealSelection(string boundary)
        {
            FocusForPolicyTest(); Call("CaptureDialogueFocus");
            events.SetSelectedGameObject(null);
            if (boundary == "hidden") Set(dialogue, "presentationVisible", false);
            if (boundary == "ended") dialogue.ClearSession();
            if (boundary == "conversation") ((DialogueContext)Get(dialogue, "activeContext")).conversationId = "another-conversation";
            if (boundary == "target") Set(dialogue, "selectedTarget", Add<DialogueParticipant>("AnotherTarget"));
            if (boundary == "other-selection") events.SetSelectedGameObject(Add<Button>("UnrelatedSelection").gameObject);
            if (boundary == "disabled-input") input.interactable = false;
            Call("RestoreDialogueFocus");
            Assert.AreNotSame(input.gameObject, events.currentSelectedGameObject);
            Assert.IsNull(Get(panel, "dialogueFocusToRestore"));
        }

        [Test] public void NoPriorFocus_DoesNotSelectDialogueInput()
        {
            Set(input, "m_AllowInput", false); Call("CaptureDialogueFocus"); Call("RestoreDialogueFocus");
            Assert.IsNull(events.currentSelectedGameObject);
        }
    }
}
