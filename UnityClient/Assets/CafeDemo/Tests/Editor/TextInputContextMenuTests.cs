using System.Collections;
using System.Linq;
using EmbodiedNPC.Settings;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class TextInputContextMenuTests
    {
        private string previousScene, clipboard;
        private InputSettings.EditorInputBehaviorInPlayMode previousInputBehavior;
        private InputSettings.BackgroundBehavior previousBackgroundBehavior;
        private Canvas canvas;
        private TMP_FontAsset font;
        private Mouse mouse;
        private TMP_InputField field;
        private static TextInputContextMenu Menu => Object.FindAnyObjectByType<TextInputContextMenu>();
        private static Button Action(string name) => Menu.GetComponentsInChildren<Button>(true).Single(b => b.name == name);
        private Vector2 Position => RectTransformUtility.WorldToScreenPoint(null, field.transform.position);

        [UnitySetUp] public IEnumerator Setup()
        {
            Assert.IsFalse(UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "Preserve unsaved scene work");
            previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            clipboard = GUIUtility.systemCopyBuffer;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var source = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/NotoSansCJKsc/NotoSansCJKsc-Dynamic.asset");
            font = TMP_FontAsset.CreateFontAsset(source.sourceFontFile); font.isMultiAtlasTexturesEnabled = true;
            canvas = new GameObject("ContextMenuTestCanvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            new GameObject("ContextMenuTestEvents", typeof(EventSystem), typeof(InputSystemUIInputModule));
            previousInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            mouse = InputSystem.AddDevice<Mouse>("ContextMenuTestMouse");
            Cursor.lockState = CursorLockMode.None;
            field = NewField();
            GUIUtility.systemCopyBuffer = "synthetic clipboard";
            yield return null;
        }
        private TMP_InputField NewField()
        {
            var node = TMP_DefaultControls.CreateInputField(default); node.transform.SetParent(canvas.transform, false);
            ((RectTransform)node.transform).sizeDelta = new Vector2(500, 100);
            var input = node.GetComponent<TMP_InputField>(); input.textComponent.font = font;
            if (input.placeholder is TMP_Text hint) hint.font = font;
            input.richText = false; input.text = "Hello world"; return input;
        }
        private IEnumerator Select(int anchor, int focus)
        {
            field.Select(); field.ActivateInputField(); yield return null; yield return null;
            field.selectionStringAnchorPosition = anchor; field.selectionStringFocusPosition = focus; field.ForceLabelUpdate();
        }
        private void Open()
        { Canvas.ForceUpdateCanvases(); Assert.IsTrue(TextInputContextMenu.TryOpenAt(Position)); Assert.IsTrue(TextInputContextMenu.IsOpen); }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            GUIUtility.systemCopyBuffer = clipboard;
            InputSystem.settings.editorInputBehaviorInPlayMode = previousInputBehavior;
            InputSystem.settings.backgroundBehavior = previousBackgroundBehavior;
            if (mouse != null) InputSystem.RemoveDevice(mouse);
            if (Application.isPlaying) yield return new ExitPlayMode();
            if (font != null) Object.DestroyImmediate(font);
            EditorSceneManager.OpenScene(string.IsNullOrEmpty(previousScene) ? "Assets/Scenes/TitleScene.unity" : previousScene);
        }

        [UnityTest] public IEnumerator RightClick_NewRuntimeField_SelectCopyCutPaste_PreservesSelectionAndDoesNotSubmit()
        {
            int submitted = 0, changed = 0;
            field.onSubmit.AddListener(_ => submitted++); field.onValueChanged.AddListener(_ => changed++);
            yield return Select(0, 5);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = Position }.WithButton(MouseButton.Right));
            yield return null; yield return null;
            Assert.IsTrue(TextInputContextMenu.IsOpen, "A field created after scene load must support right click");
            Action("Copy").onClick.Invoke(); Assert.AreEqual("Hello", GUIUtility.systemCopyBuffer);
            yield return null; yield return null; yield return null;
            Assert.AreEqual(0, field.selectionStringAnchorPosition); Assert.AreEqual(5, field.selectionStringFocusPosition);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = Position }); yield return null;
            Open(); Action("Cut").onClick.Invoke(); Assert.AreEqual(" world", field.text);
            yield return null; yield return null; yield return null;
            Open(); Action("Paste").onClick.Invoke(); Assert.AreEqual("Hello world", field.text);
            yield return null; yield return null; yield return null;
            Open(); Action("SelectAll").onClick.Invoke();
            yield return null; yield return null; yield return null;
            Assert.AreEqual(field.text.Length, Mathf.Abs(field.selectionStringFocusPosition - field.selectionStringAnchorPosition));
            Assert.IsTrue(field.isFocused); Assert.Greater(changed, 0); Assert.AreEqual(0, submitted);
        }
        [UnityTest] public IEnumerator Paste_UsesExistingNumericValidationLimitAndMultilineRules()
        {
            field.contentType = TMP_InputField.ContentType.IntegerNumber; field.characterLimit = 4;
            yield return Select(0, field.text.Length); GUIUtility.systemCopyBuffer = "1a23\n45";
            Open(); Action("Paste").onClick.Invoke(); Assert.AreEqual("1234", field.text);
            yield return null; yield return null; yield return null;
            field.contentType = TMP_InputField.ContentType.Standard; field.lineType = TMP_InputField.LineType.MultiLineNewline;
            field.characterLimit = 160; field.text = "prefix old suffix";
            int submitted = 0; field.onSubmit.AddListener(_ => submitted++);
            yield return Select(7, 10); GUIUtility.systemCopyBuffer = "黑发\nWhite shirt";
            Open(); Action("Paste").onClick.Invoke();
            Assert.AreEqual("prefix 黑发\nWhite shirt suffix", field.text); Assert.AreEqual(0, submitted);
        }
        [UnityTest] public IEnumerator PasswordAndReadOnlyFields_KeepTheirExistingEditingProtection()
        {
            field.contentType = TMP_InputField.ContentType.Password; field.text = "synthetic-secret";
            yield return Select(0, field.text.Length);
            Open(); Assert.IsFalse(Action("Copy").interactable); Assert.IsFalse(Action("Cut").interactable);
            Assert.IsTrue(Action("Paste").interactable); Assert.IsTrue(Action("SelectAll").interactable);
            GUIUtility.systemCopyBuffer = "synthetic-replacement"; Action("Paste").onClick.Invoke();
            Assert.AreEqual("synthetic-replacement", field.text);
            Assert.IsFalse(field.textComponent.text.Contains("synthetic-replacement"));
            yield return null; yield return null; yield return null;
            field.contentType = TMP_InputField.ContentType.Standard; field.readOnly = true; field.text = "diagnostic text";
            yield return Select(0, field.text.Length); Open();
            Assert.IsFalse(Action("Cut").interactable); Assert.IsFalse(Action("Paste").interactable);
            Assert.IsTrue(Action("Copy").interactable); Action("Copy").onClick.Invoke();
            Assert.AreEqual("diagnostic text", GUIUtility.systemCopyBuffer); Assert.AreEqual("diagnostic text", field.text);
        }
        [UnityTest] public IEnumerator Menu_StaysOnScreenAndCancelConsumesOnlyItsOwnLayer()
        {
            field.text = "unsaved draft"; yield return Select(2, 5);
            ((RectTransform)field.transform).position = new Vector3(Screen.width - 8, 8);
            Open();
            var card = Menu.transform.Find("TextEditMenu/TextEditActions") as RectTransform;
            var corners = new Vector3[4]; card.GetWorldCorners(corners);
            foreach (var point in corners)
            { Assert.GreaterOrEqual(point.x, 0); Assert.LessOrEqual(point.x, Screen.width); Assert.GreaterOrEqual(point.y, 0); Assert.LessOrEqual(point.y, Screen.height); }
            Assert.IsTrue(TextInputContextMenu.ConsumeCancel()); Assert.IsFalse(TextInputContextMenu.IsOpen);
            Assert.IsTrue(TextInputContextMenu.ConsumeCancel(), "Do not close a parent UI on the same ESC");
            yield return null; yield return null; yield return null;
            Assert.IsFalse(TextInputContextMenu.ConsumeCancel()); Assert.AreEqual("unsaved draft", field.text);
            Open(); field.interactable = false; Menu.SendMessage("LateUpdate"); Assert.IsFalse(TextInputContextMenu.IsOpen);
        }
        [UnityTest] public IEnumerator Menu_RejectsOccludedFieldsAndInvalidatesChangedOrClosedTargets()
        {
            yield return Select(0, 5); Open(); field.text = "new draft from another action";
            Menu.SendMessage("LateUpdate"); Assert.IsFalse(TextInputContextMenu.IsOpen);
            Assert.AreEqual("new draft from another action", field.text);
            var modal = new GameObject("Modal", typeof(RectTransform), typeof(Image)); modal.transform.SetParent(canvas.transform, false);
            ((RectTransform)modal.transform).sizeDelta = new Vector2(600, 200); Canvas.ForceUpdateCanvases();
            yield return null; Canvas.ForceUpdateCanvases();
            Assert.IsFalse(TextInputContextMenu.TryOpenAt(Position), "Do not open a menu through a modal");
            Object.Destroy(modal); yield return null; Open(); field.gameObject.SetActive(false);
            Menu.SendMessage("LateUpdate"); Assert.IsFalse(TextInputContextMenu.IsOpen);
        }
    }
}
