using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using EmbodiedNPC.Settings;

namespace CafeDemo
{
    // Keep the existing ScrollRect in charge of scrolling; TMP provides selection and Ctrl+C.
    public sealed class RuntimeTraceSelectionField : TMP_InputField
    {
        public override void OnScroll(PointerEventData eventData) =>
            GetComponentInParent<ScrollRect>()?.OnScroll(eventData);
    }

    /// <summary>A bounded, session-only projection of lines already written by RuntimeTrace.</summary>
    public sealed class RuntimeTraceLiveBuffer
    {
        public const int Capacity = 300;
        private readonly List<Entry> entries = new();

        public int Count => entries.Count;
        public bool Paused { get; set; }
        public string LatestRawLine => entries.Count == 0 ? string.Empty : entries[^1].RawLine;

        public bool Add(string line)
        {
            if (Paused || string.IsNullOrEmpty(line)) return false;
            try
            {
                // Preserve the exact UTC/offset and fractional seconds. JToken's implicit
                // DateTime -> display-string conversion loses this information.
                using var reader=new JsonTextReader(new StringReader(line)){DateParseHandling=DateParseHandling.None};
                JObject json = JObject.Load(reader);
                string category = Value(json, "category");
                string kind = Value(json, "kind");
                string time = LocalTime(Value(json, "utc"));
                string entity = Value(json, "entity_id");
                JToken data = json["data"];
                string detail = Detail(kind, json, data);
                if (data?["codes"] is JArray codes) detail += " " + string.Join(" · ",codes);
                string summary = $"{time}  {category}  {kind}  {entity}  {detail}"
                    .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
                if (summary.Length > 220) summary = summary.Substring(0, 219) + "…";
                bool error = IsErrorEntry(kind, data);
                if (entries.Count == Capacity) entries.RemoveAt(0);
                entries.Add(new Entry(line, category, summary, error));
                return true;
            }
            catch (Exception)
            {
                // A malformed observer line never reaches gameplay or disables the recorder.
                return false;
            }
        }

        public void Clear() => entries.Clear();

        public static string LocalTime(string utc) => DateTimeOffset.TryParse(utc,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) : "—";

        public string VisibleText(string filter)
        {
            var text = new StringBuilder();
            foreach (Entry entry in entries)
            {
                if (filter == "Errors" ? !entry.Error :
                    !string.IsNullOrEmpty(filter) && filter != "All" && entry.Category != filter) continue;
                if (text.Length > 0) text.AppendLine();
                text.Append(entry.Summary);
            }
            return text.ToString();
        }

        private static string Value(JToken token, string key) => token?[key]?.Type == JTokenType.Null
            ? string.Empty : token?[key]?.ToString() ?? string.Empty;

        private static string Detail(string kind, JToken json, JToken data)
        {
            string run = Value(json, "run_id");
            string request = Value(json, "request_id");
            string proposal = Value(json, "proposal_id");
            switch (kind)
            {
                case "observation_applied":
                    return $"→ {Value(data, "subject_entity_id")} · {Value(data, "lifecycle_state")}";
                case "cognitive_run_started":
                case "cognitive_run_terminal":
                    return $"run={run} {Value(data, "state")}{Value(data, "trigger_type")}";
                case "cognitive_request_created":
                    return $"run={run} request={request}";
                case "cognitive_response":
                    return $"{Value(data, "decision_kind")} run={run} request={request} {Value(data, "utterance")}";
                case "utterance_presented":
                    return $"run={run} {Value(data, "utterance")}";
                case "goal_plan_presentation":
                    return $"proposal={proposal} {Value(data, "presentation_kind")} {Value(data, "code")}";
                case "goal_outcome":
                    return $"proposal={proposal} {Value(data, "status")} {Value(data, "failure_code")}";
                case "skill_result":
                    return $"{Value(data, "skill_type")} {Value(data, "status")} {Value(data, "failure_code")}";
                case "connection_status":
                case "experience_append_result":
                    return Value(data, "status");
                case "cognitive_transport_failure":
                    return $"request={request} {Value(data, "reason")}";
                default:
                    return string.IsNullOrEmpty(run) ? string.Empty : $"run={run}";
            }
        }

        private static bool IsErrorEntry(string kind, JToken data)
        {
            if (kind == "cognitive_transport_failure") return true;
            if (kind != "goal_outcome" && kind != "skill_result" &&
                kind != "experience_append_result" && kind != "connection_status") return false;
            string status = Value(data, "status").ToLowerInvariant();
            return status.Contains("fail") || status.Contains("error") || status.Contains("reject") ||
                status.Contains("unavailable") || status.Contains("disconnect");
        }

        private readonly struct Entry
        {
            public Entry(string rawLine, string category, string summary, bool error)
            { RawLine = rawLine; Category = category; Summary = summary; Error = error; }
            public string RawLine { get; }
            public string Category { get; }
            public string Summary { get; }
            public bool Error { get; }
        }
    }

    /// <summary>Read-only Developer Panel view; subscribes while its page is closed.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class CafeRuntimeTracePanel : MonoBehaviour
    {
        [SerializeField] private RuntimeTraceRecorder recorder;
        [SerializeField] private GameObject tracePage;
        [SerializeField] private TMP_Text entriesText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text pauseButtonText;
        [SerializeField] private ScrollRect scrollRect;
        [SerializeField] private Button pauseButton;
        [SerializeField] private Button clearButton;
        [SerializeField] private Button folderButton;
        [SerializeField] private Toggle autoScrollToggle;
        [SerializeField] private TMP_Dropdown categoryDropdown;

        private readonly RuntimeTraceLiveBuffer buffer = new();
        private RuntimeTraceSelectionField selectionField;
        private bool refreshAfterSelection;
        public RuntimeTraceLiveBuffer Buffer => buffer;

        private void EnsureSelectableText()
        {
            if (selectionField != null || entriesText == null) return;
            selectionField = entriesText.GetComponent<RuntimeTraceSelectionField>();
            if (selectionField == null) selectionField = entriesText.gameObject.AddComponent<RuntimeTraceSelectionField>();
            selectionField.textViewport = entriesText.rectTransform;
            selectionField.textComponent = entriesText;
            selectionField.readOnly = true;
            selectionField.lineType = TMP_InputField.LineType.MultiLineNewline;
            selectionField.richText = false;
            selectionField.onFocusSelectAll = false;
            selectionField.caretColor = Color.clear;
            selectionField.customCaretColor = true;
            selectionField.selectionColor = new Color(0.35f, 0.55f, 0.85f, 0.4f);
            entriesText.richText = false;
            entriesText.raycastTarget = true;
            selectionField.SetTextWithoutNotify(entriesText.text);
        }

        private void LateUpdate()
        {
            if (refreshAfterSelection && selectionField != null && !selectionField.isFocused)
            {
                refreshAfterSelection = false;
                RefreshView();
            }
        }

        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshView;
            if (recorder != null) recorder.TraceLineRecorded += OnTraceLine;
            pauseButton?.onClick.AddListener(TogglePause);
            clearButton?.onClick.AddListener(ClearView);
            folderButton?.onClick.AddListener(OpenLogFolder);
            categoryDropdown?.onValueChanged.AddListener(_ => RefreshView());
            autoScrollToggle?.onValueChanged.AddListener(_ => RefreshView());
        }

        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshView;
            if (recorder != null) recorder.TraceLineRecorded -= OnTraceLine;
            pauseButton?.onClick.RemoveListener(TogglePause);
            clearButton?.onClick.RemoveListener(ClearView);
            folderButton?.onClick.RemoveListener(OpenLogFolder);
            categoryDropdown?.onValueChanged.RemoveAllListeners();
            autoScrollToggle?.onValueChanged.RemoveAllListeners();
        }

        private void OnTraceLine(string line)
        {
            try { if (buffer.Add(line) && tracePage != null && tracePage.activeInHierarchy) RefreshView(); }
            catch (Exception) { /* Presentation is never a recorder dependency. */ }
        }

        public void TogglePause()
        {
            buffer.Paused = !buffer.Paused;
            RefreshView();
        }

        public void ClearView()
        {
            buffer.Clear();
            RefreshView();
        }

        public void RefreshView()
        {
            try
            {
                EnsureSelectableText();
                string filter = categoryDropdown != null && categoryDropdown.value < categoryDropdown.options.Count
                    ? categoryDropdown.value == 0 ? "All" : categoryDropdown.value == categoryDropdown.options.Count - 1 ? "Errors" :
                        categoryDropdown.options[categoryDropdown.value].text : "All";
                if (categoryDropdown != null && categoryDropdown.options.Count >= 2)
                {
                    categoryDropdown.options[0].text = SettingsUiLanguage.T("全部", "All");
                    categoryDropdown.options[^1].text = SettingsUiLanguage.T("仅错误", "Errors");
                    categoryDropdown.RefreshShownValue();
                }
                bool selecting = selectionField != null && selectionField.isFocused;
                if (selecting) refreshAfterSelection = true;
                else if (selectionField != null) selectionField.SetTextWithoutNotify(buffer.VisibleText(filter));
                if (pauseButtonText != null) pauseButtonText.text = buffer.Paused ? SettingsUiLanguage.T("继续显示", "Resume view") : SettingsUiLanguage.T("暂停显示", "Pause view");
                if (statusText != null) statusText.text = recorder == null || !recorder.IsRecording
                    ? recorder?.UnavailableReason == "TRACE_SIZE_LIMIT" ? SettingsUiLanguage.T("技术追踪已达存储上限；游戏运行不受影响。", "Technical trace storage limit reached; gameplay continues.") :
                        SettingsUiLanguage.T("运行追踪不可用", "Runtime Trace unavailable") :
                    SettingsUiLanguage.T($"最近 {buffer.Count}/{RuntimeTraceLiveBuffer.Capacity} 条 · {(buffer.Paused ? "视图已暂停" : "实时")}",
                        $"Latest {buffer.Count}/{RuntimeTraceLiveBuffer.Capacity} entries · {(buffer.Paused ? "Paused" : "Live")}");
                if (folderButton != null) folderButton.interactable = recorder != null &&
                    !string.IsNullOrEmpty(recorder.CurrentTracePath);
                if (!selecting && autoScrollToggle != null && autoScrollToggle.isOn && scrollRect != null)
                {
                    Canvas.ForceUpdateCanvases();
                    scrollRect.verticalNormalizedPosition = 0f;
                }
            }
            catch (Exception) { /* Debug UI must fail open. */ }
        }

        public void OpenLogFolder()
        {
            try
            {
                string directory = recorder == null ? null : Path.GetDirectoryName(recorder.CurrentTracePath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception) { if (statusText != null) statusText.text = SettingsUiLanguage.T("无法打开日志目录", "Cannot open the log folder"); }
        }
    }
}
