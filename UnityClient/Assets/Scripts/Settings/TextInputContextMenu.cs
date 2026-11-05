using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>One mouse edit menu for all runtime TMP fields, including fields created after scene load.</summary>
    public sealed class TextInputContextMenu : MonoBehaviour
    {
        private static TextInputContextMenu instance;
        private static int cancelledFrame = -1;
        private readonly List<RaycastResult> hits = new();
        private readonly Button[] actions = new Button[4];
        private readonly TMP_Text[] captions = new TMP_Text[4];
        private GameObject surface;
        private RectTransform card;
        private Canvas canvas;
        private TMP_InputField target;
        private string originalText;
        private int anchor, focus;
        private Coroutine restoringFocus;
        public static bool IsOpen => instance != null && instance.target != null && instance.surface.activeSelf;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { instance = null; cancelledFrame = -1; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (instance != null) return;
            var owner = new GameObject("TextInputContextMenu", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            instance = owner.AddComponent<TextInputContextMenu>();
            DontDestroyOnLoad(owner);
        }

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            canvas = GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720); scaler.matchWidthOrHeight = .5f;
            surface = new GameObject("TextEditMenu", typeof(RectTransform));
            surface.transform.SetParent(transform, false); Stretch((RectTransform)surface.transform);
            var outside = new GameObject("DismissTextEditMenu", typeof(RectTransform), typeof(Image), typeof(Button));
            outside.transform.SetParent(surface.transform, false); Stretch((RectTransform)outside.transform);
            outside.GetComponent<Image>().color = Color.clear;
            outside.GetComponent<Button>().onClick.AddListener(() => Close(false));
            card = (RectTransform)new GameObject("TextEditActions", typeof(RectTransform), typeof(Image)).transform;
            card.SetParent(surface.transform, false); card.pivot = new Vector2(0, 1);
            card.sizeDelta = new Vector2(224, 184);
            SettingsPanelTheme.Surface(card.GetComponent<Image>(), SettingsPanelTheme.Paper);
            for (int i = 0; i < actions.Length; i++)
            {
                int action = i;
                var row = new GameObject(new[] { "Cut", "Copy", "Paste", "SelectAll" }[i],
                    typeof(RectTransform), typeof(Image), typeof(Button));
                row.transform.SetParent(card, false);
                var rect = (RectTransform)row.transform; rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
                rect.pivot = new Vector2(0, 1); rect.anchoredPosition = new Vector2(8, -8 - i * 42);
                rect.sizeDelta = new Vector2(208, 42);
                actions[i] = row.GetComponent<Button>(); actions[i].onClick.AddListener(() => Edit(action));
                var label = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                label.transform.SetParent(rect, false); Stretch((RectTransform)label.transform);
                captions[i] = label.GetComponent<TMP_Text>(); captions[i].fontSize = 20;
                captions[i].alignment = TextAlignmentOptions.MidlineLeft; captions[i].margin = new Vector4(16, 0, 8, 0);
                captions[i].raycastTarget = false;
                SettingsPanelTheme.Button(actions[i]);
            }
            RefreshLanguage(); surface.SetActive(false);
        }
        private static void Stretch(RectTransform rect)
        { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
        private void OnEnable()
        { SceneManager.activeSceneChanged += SceneChanged; SettingsUiLanguage.Changed += RefreshLanguage; }
        private void OnDisable()
        { SceneManager.activeSceneChanged -= SceneChanged; SettingsUiLanguage.Changed -= RefreshLanguage; Close(false); }
        private void OnDestroy() { if (instance == this) instance = null; }
        private void OnApplicationFocus(bool focused) { if (!focused) Close(false); }
        private void SceneChanged(Scene before, Scene after) => Close(false);
        private void RefreshLanguage()
        {
            string[] labels = { SettingsUiLanguage.T("剪切", "Cut"), SettingsUiLanguage.T("复制", "Copy"),
                SettingsUiLanguage.T("粘贴", "Paste"), SettingsUiLanguage.T("全选", "Select all") };
            for (int i = 0; i < captions.Length; i++) if (captions[i] != null) captions[i].text = labels[i];
        }
        private static bool EditableTarget(TMP_InputField field) =>
            field != null && field.isActiveAndEnabled && field.IsInteractable() && field.textComponent != null;

        private void LateUpdate()
        {
            if (IsOpen && (!EditableTarget(target) || target.text != originalText)) Close(false);
            if (IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ConsumeCancel();
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (IsOpen && mouse.scroll.ReadValue() != Vector2.zero) Close(false);
            if (mouse.rightButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
                TryOpenAt(mouse.position.ReadValue());
        }

        /// <summary>Resolve the topmost UI hit; never edit a field hidden behind a modal or dropdown.</summary>
        public static bool TryOpenAt(Vector2 screenPosition)
        {
            if (!Application.isPlaying || EventSystem.current == null) return false;
            if (instance == null) Install();
            instance.Close(false);
            instance.hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = screenPosition }, instance.hits);
            if (instance.hits.Count == 0) return false;
            var field = instance.hits[0].gameObject.GetComponentInParent<TMP_InputField>();
            if (!EditableTarget(field)) return false;
            instance.Open(field, screenPosition); return true;
        }
        private void Open(TMP_InputField field, Vector2 position)
        {
            if (restoringFocus != null) { StopCoroutine(restoringFocus); restoringFocus = null; }
            target = field; originalText = field.text;
            anchor = field.selectionStringAnchorPosition; focus = field.selectionStringFocusPosition;
            canvas.targetDisplay = field.GetComponentInParent<Canvas>().targetDisplay;
            foreach (var caption in captions) caption.font = field.textComponent.font;
            bool selected = anchor != focus;
            bool password = field.inputType == TMP_InputField.InputType.Password;
            actions[0].interactable = selected && !password && !field.readOnly;
            actions[1].interactable = selected && !password;
            actions[2].interactable = !field.readOnly && !string.IsNullOrEmpty(GUIUtility.systemCopyBuffer);
            actions[3].interactable = field.text.Length > 0;
            surface.SetActive(true); Canvas.ForceUpdateCanvases();
            var root = (RectTransform)transform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, position, null, out var local);
            local.x = Mathf.Clamp(local.x, root.rect.xMin + 4, root.rect.xMax - card.rect.width - 4);
            local.y = Mathf.Clamp(local.y, root.rect.yMin + card.rect.height + 4, root.rect.yMax - 4);
            card.anchoredPosition = local;
            // Move EventSystem focus off TMP while the menu is open so ESC cannot revert its text.
            EventSystem.current.SetSelectedGameObject(card.gameObject);
        }
        private void Edit(int action)
        {
            if (!EditableTarget(target) || target.text != originalText || !actions[action].interactable)
            { Close(false); return; }
            target.selectionStringAnchorPosition = anchor; target.selectionStringFocusPosition = focus;
            // TMP owns filtering, limits, selection replacement and change notifications. No submit event is sent.
            var key = new[] { KeyCode.X, KeyCode.C, KeyCode.V, KeyCode.A }[action];
            target.ProcessEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers =
                Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor
                    ? EventModifiers.Command : EventModifiers.Control });
            anchor = target.selectionStringAnchorPosition; focus = target.selectionStringFocusPosition;
            target.ForceLabelUpdate(); Close(true);
        }
        public static bool ConsumeCancel()
        {
            if (cancelledFrame == Time.frameCount) return true;
            if (!IsOpen) return false;
            instance.Close(true); cancelledFrame = Time.frameCount; return true;
        }
        private void Close(bool restoreFocus)
        {
            if (surface == null || !surface.activeSelf) return;
            var field = target; target = null; surface.SetActive(false);
            if (restoreFocus && EditableTarget(field))
                restoringFocus = StartCoroutine(RestoreFocus(field, anchor, focus, field.text));
        }
        private IEnumerator RestoreFocus(TMP_InputField field, int start, int end, string expectedText)
        {
            if (!EditableTarget(field) || EventSystem.current == null) yield break;
            field.Select(); field.ActivateInputField();
            // Activation is deferred to TMP's LateUpdate; wait for it before restoring the exact selection.
            yield return null; yield return null;
            if (EditableTarget(field) && EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject == field.gameObject && field.text == expectedText)
            {
                field.selectionStringAnchorPosition = Mathf.Clamp(start, 0, field.text.Length);
                field.selectionStringFocusPosition = Mathf.Clamp(end, 0, field.text.Length);
                field.ForceLabelUpdate();
            }
            restoringFocus = null;
        }
    }
}
