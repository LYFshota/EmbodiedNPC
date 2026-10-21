using System;
using System.Collections.Generic;
using System.Text;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Routines;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Read-only projection of the NPC selected by the existing character settings page.</summary>
    public sealed class CafeNpcDebugPanel : MonoBehaviour
    {
        [SerializeField] private CafeNpcCharacterSettings npcSettings;
        [SerializeField] private GameObject debugPage;
        [SerializeField] private TMP_Dropdown npcDropdown;
        [SerializeField] private TMP_Text selectedNpcText;
        [SerializeField] private TMP_Text perceptionText;
        [SerializeField] private Toggle visionToggle;
        [SerializeField] private Toggle proximityToggle;
        [SerializeField] private Toggle entityIdsToggle;
        [SerializeField] private Toggle perceivedEntitiesToggle;
        [SerializeField] private Toggle hearingToggle;
        [SerializeField] private TMP_Text sensorSummary;
        [SerializeField] private CafePerceptionDebugVisualizer visualizer;

        private CognitiveContextComposer selected;
        private WorldEntity entity;
        private AgentOrchestrator orchestrator;
        private CognitiveRunDriver runDriver;
        private CurrentBehaviorMode behaviorMode;
        private DailyBehaviorController dailyBehavior;
        private AgentFeatureProfileBinding featureBinding;
        private AgentActionCapabilityProfile actionCapabilities;
        private PerceivedStateStore perception;
        private double nextRefreshAt;

        public string CurrentSelectedText => selectedNpcText == null ? string.Empty : selectedNpcText.text;
        public string CurrentPerceptionText => perceptionText == null ? string.Empty : perceptionText.text;
        public string SelectedEntityId => entity == null ? null : entity.EntityId;

        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshLanguage;
            if (npcSettings != null) npcSettings.SelectionChanged += OnSelectionChanged;
            if (npcDropdown != null) npcDropdown.onValueChanged.AddListener(OnDropdownChanged);
            if (visionToggle != null) visionToggle.onValueChanged.AddListener(OnVisualizationChanged);
            if (proximityToggle != null) proximityToggle.onValueChanged.AddListener(OnVisualizationChanged);
            if (entityIdsToggle != null) entityIdsToggle.onValueChanged.AddListener(OnVisualizationChanged);
            if (perceivedEntitiesToggle != null) perceivedEntitiesToggle.onValueChanged.AddListener(OnVisualizationChanged);
            if (hearingToggle != null) hearingToggle.onValueChanged.AddListener(OnVisualizationChanged);
            visualizer?.SetToolActive(true);
            OnSelectionChanged(npcSettings == null ? null : npcSettings.SelectedComposer);
        }

        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshLanguage;
            if (npcSettings != null) npcSettings.SelectionChanged -= OnSelectionChanged;
            if (npcDropdown != null) npcDropdown.onValueChanged.RemoveListener(OnDropdownChanged);
            if (visionToggle != null) visionToggle.onValueChanged.RemoveListener(OnVisualizationChanged);
            if (proximityToggle != null) proximityToggle.onValueChanged.RemoveListener(OnVisualizationChanged);
            if (entityIdsToggle != null) entityIdsToggle.onValueChanged.RemoveListener(OnVisualizationChanged);
            if (perceivedEntitiesToggle != null) perceivedEntitiesToggle.onValueChanged.RemoveListener(OnVisualizationChanged);
            if (hearingToggle != null) hearingToggle.onValueChanged.RemoveListener(OnVisualizationChanged);
            visualizer?.SetToolActive(false);
        }

        private void Update()
        {
            if (debugPage == null || !debugPage.activeInHierarchy || Time.unscaledTimeAsDouble < nextRefreshAt)
                return;
            nextRefreshAt = Time.unscaledTimeAsDouble + 0.3d;
            RefreshNow();
        }

        private void OnDropdownChanged(int index) => npcSettings?.SelectCandidate(index);

        private void OnSelectionChanged(CognitiveContextComposer composer)
        {
            selected = composer;
            entity = selected == null ? null : selected.GetComponent<WorldEntity>();
            orchestrator = selected == null ? null : selected.GetComponent<AgentOrchestrator>();
            runDriver = selected == null ? null : selected.GetComponent<CognitiveRunDriver>();
            behaviorMode = selected == null ? null : selected.GetComponent<CurrentBehaviorMode>();
            dailyBehavior = selected == null ? null : selected.GetComponent<DailyBehaviorController>();
            featureBinding = selected == null ? null : selected.GetComponent<AgentFeatureProfileBinding>();
            actionCapabilities = selected == null ? null : selected.GetComponent<AgentActionCapabilityProfile>();
            perception = selected == null ? null : selected.GetComponent<PerceivedStateStore>();
            visualizer?.SetSelection(entity, perception, selected == null ? null : selected.GetComponent<VisionSensor>(),
                selected == null ? null : selected.GetComponent<ProximitySensor>());
            SyncDropdown();
            SyncVisualizationControls();
            RefreshNow();
        }

        private void SyncDropdown()
        {
            if (npcDropdown == null || npcSettings == null) return;
            npcDropdown.ClearOptions();
            var names = new List<string>(npcSettings.CandidateCount);
            for (int index = 0; index < npcSettings.CandidateCount; index++)
            {
                CharacterDefinition definition = npcSettings.GetCandidate(index)?.CharacterDefinition;
                names.Add(SettingsUiLanguage.NpcDisplayName(definition?.CharacterId,
                    CharacterProfileResolver.Resolve(definition)?.display_name) ?? "Unknown NPC");
            }
            npcDropdown.AddOptions(names);
            if (npcSettings.SelectedIndex >= 0)
                npcDropdown.SetValueWithoutNotify(npcSettings.SelectedIndex);
            npcDropdown.RefreshShownValue();
            npcDropdown.interactable = names.Count > 0;
        }

        private void OnVisualizationChanged(bool _)
        {
            visualizer?.SetVisibility(visionToggle != null && visionToggle.isOn,
                proximityToggle != null && proximityToggle.isOn,
                entityIdsToggle != null && entityIdsToggle.isOn,
                perceivedEntitiesToggle != null && perceivedEntitiesToggle.isOn,
                hearingToggle != null && hearingToggle.isOn);
            RefreshNow();
        }

        // Build and Bind both call this. Remove our listener before rebinding the same control.
        public void BindVisualizationControls(Toggle hearingControl, TMP_Text summary)
        {
            if (hearingToggle != null) hearingToggle.onValueChanged.RemoveListener(OnVisualizationChanged);
            hearingToggle = hearingControl; sensorSummary = summary;
            if (isActiveAndEnabled && hearingToggle != null) hearingToggle.onValueChanged.AddListener(OnVisualizationChanged);
            SyncVisualizationControls(); RefreshNow();
        }

        private void SyncVisualizationControls()
        {
            var state = visualizer == null ? default : visualizer.SelectionState;
            void Sync(Toggle toggle, bool value, bool available)
            {
                if (toggle == null) return;
                toggle.SetIsOnWithoutNotify(value);
                toggle.interactable = entity != null && available;
                toggle.GetComponent<SettingsToggleFeedback>()?.Refresh();
            }
            Sync(visionToggle, state.Vision, visualizer != null && visualizer.CanShowVision);
            Sync(proximityToggle, state.Proximity, visualizer != null && visualizer.CanShowProximity);
            Sync(entityIdsToggle, state.EntityIds, true);
            Sync(perceivedEntitiesToggle, state.PerceivedEntities, perception != null);
            Sync(hearingToggle, state.Hearing, visualizer != null && visualizer.CanShowHearing);
            var proximityLabel = proximityToggle == null ? null : proximityToggle.transform.parent.Find("ProximityToggleLabel")?.GetComponent<TMP_Text>();
            if (proximityLabel != null)
            {
                var sensor = selected == null ? null : selected.GetComponent<ProximitySensor>();
                proximityLabel.text = sensor == null ? SettingsUiLanguage.T("近距范围 · 未配置传感器", "Proximity · No sensor configured") :
                    !sensor.isActiveAndEnabled ? SettingsUiLanguage.T("近距范围 · 传感器已禁用", "Proximity · Sensor disabled") :
                    SettingsUiLanguage.T("显示近距范围", "Show proximity range");
            }
        }

        private void RefreshLanguage() { SyncDropdown(); RefreshNow(); }
        public void RefreshNow()
        {
            SyncVisualizationControls();
            if (sensorSummary != null) sensorSummary.text = visualizer == null ? string.Empty : visualizer.SensorSummary;
            if (selectedNpcText != null) selectedNpcText.text = BuildSelectedText();
            if (perceptionText != null) perceptionText.text = BuildPerceptionText();
        }

        private string BuildSelectedText()
        {
            if (selected == null || entity == null)
                return SettingsUiLanguage.T("当前 NPC\n未选择 NPC", "Selected NPC\nNo NPC selected");
            CognitiveCharacterDto character = CharacterProfileResolver.Resolve(selected.CharacterDefinition);
            CognitiveRun run = orchestrator == null ? null : orchestrator.CurrentRun;
            var text = new StringBuilder(350);
            text.AppendLine(SettingsUiLanguage.T("当前 NPC", "Selected NPC"));
            text.Append(SettingsUiLanguage.T("显示名称：", "Display Name: ")).AppendLine(SettingsUiLanguage.NpcDisplayName(character?.character_id, character?.display_name) ?? "N/A");
            text.Append(SettingsUiLanguage.T("角色 ID：", "CharacterId: ")).AppendLine(character?.character_id ?? "N/A");
            text.Append(SettingsUiLanguage.T("实体 ID：", "EntityId: ")).AppendLine(entity.EntityId);
            text.Append(SettingsUiLanguage.T("Agent 身份：", "AgentRole: ")).AppendLine(entity.AgentRole.ToString());
            text.Append(SettingsUiLanguage.T("认知行为模式：", "Cognitive Behavior Mode: ")).AppendLine(behaviorMode == null ? "N/A" : behaviorMode.CurrentModeId);
            text.Append(SettingsUiLanguage.T("生活行为模式：", "Daily Behavior Mode: ")).AppendLine(dailyBehavior == null ? "N/A" : dailyBehavior.CurrentMode.ToString());
            if (dailyBehavior != null)
            {
                text.Append(SettingsUiLanguage.T("主要日常活动：", "Daily Activity: ")).AppendLine(dailyBehavior.CurrentActivity?.ToString() ?? "Idle");
                text.Append(SettingsUiLanguage.T("活动状态：", "Activity State: ")).AppendLine(dailyBehavior.ActivityReason);
                text.Append(SettingsUiLanguage.T("正式任务占用：", "Formal Task: ")).AppendLine(dailyBehavior.IsFormalTaskOccupied.ToString());
                text.Append(SettingsUiLanguage.T("活动失败冷却：", "Activity Retry Cooldown: ")).AppendLine(dailyBehavior.IsActivityFailureCoolingDown.ToString());
            }
            bool? cognition = featureBinding?.Configuration?.CognitionEnabled;
            text.Append(SettingsUiLanguage.T("认知已启用：", "Cognition Enabled: ")).AppendLine(cognition == null ? "N/A" :
                cognition.Value ? SettingsUiLanguage.T("是", "Yes") : SettingsUiLanguage.T("否", "No"));
            text.Append(SettingsUiLanguage.T("当前运行：", "Current Run: ")).AppendLine(run == null ?
                SettingsUiLanguage.T("空闲", "Idle") : run.State.ToString());
            if (run != null) text.Append(SettingsUiLanguage.T("运行 ID：", "RunId: ")).AppendLine(run.RunId);
            text.Append(SettingsUiLanguage.T("当前任务：", "Current Task: ")).AppendLine(runDriver?.CurrentTaskEventType ??
                SettingsUiLanguage.T("无", "None"));
            text.Append(SettingsUiLanguage.T("可用动作：", "Available Actions: "));
            if (actionCapabilities == null || !actionCapabilities.TryBuildManifest(out CognitiveCapabilityManifestDto manifest))
                text.AppendLine("N/A");
            else if (manifest.available_actions.Length == 0) text.AppendLine("[]");
            else
            {
                for (int index = 0; index < manifest.available_actions.Length; index++)
                {
                    if (index > 0) text.Append(", ");
                    text.Append(manifest.available_actions[index].action_id);
                }
                text.AppendLine();
            }
            text.AppendLine(SettingsUiLanguage.T("可视化仅显示当前所选 NPC；其他角色可从上方列表选择。", "Overlays show the selected NPC; choose other characters above."));
            return text.ToString();
        }

        private string BuildPerceptionText()
        {
            var text = new StringBuilder(350);
            text.AppendLine(SettingsUiLanguage.T("感知", "Perception"));
            PerceptionAggregator state = perception?.Aggregator;
            if (state == null)
            {
                text.Append(SettingsUiLanguage.T("暂无感知状态", "No perception state"));
                return text.ToString();
            }
            AppendEntities(text, "Visible", state.Entities, "visibility_state", "current_visible");
            AppendEntities(text, "Nearby", state.Entities, "proximity_state", "nearby");
            AppendEntities(text, "Remembered", state.Entities, "visibility_state", "remembered");
            Observation last = null;
            foreach (Observation observation in state.Observations)
                if (last == null || observation.PerceptionRevision > last.PerceptionRevision) last = observation;
            text.AppendLine(SettingsUiLanguage.T("最近观察", "Last Observation"));
            if (last == null) text.AppendLine(SettingsUiLanguage.T("无", "None"));
            else
            {
                string status = last.KnownFields.TryGetValue("visibility_state", out string visual) ? visual :
                    last.KnownFields.TryGetValue("proximity_state", out string nearby) ? nearby :
                    last.LifecycleState.ToString().ToLowerInvariant();
                string name = state.Entities.TryGetValue(last.SubjectEntityId, out PerceivedEntityRecord record)
                    ? DisplayName(record) : last.SubjectEntityId;
                text.Append(last.Channel.ToString().ToLowerInvariant()).Append(" · ").Append(status)
                    .Append(" · ").AppendLine(name);
            }
            return text.ToString();
        }

        private static void AppendEntities(StringBuilder text, string title,
            IReadOnlyDictionary<string, PerceivedEntityRecord> entities, string field, string expected)
        {
            text.AppendLine(title == "Visible" ? SettingsUiLanguage.T("当前可见", "Visible") :
                title == "Nearby" ? SettingsUiLanguage.T("附近", "Nearby") :
                SettingsUiLanguage.T("记忆中", "Remembered"));
            int count = 0;
            foreach (PerceivedEntityRecord record in entities.Values)
            {
                if (!record.KnownFields.TryGetValue(field, out string value) ||
                    !string.Equals(value, expected, StringComparison.Ordinal)) continue;
                text.Append("• ").Append(DisplayName(record)).Append(" · ").Append(record.EntityId);
                if (title == "Remembered") text.Append(SettingsUiLanguage.T(" · 记忆中", " · remembered"));
                text.AppendLine();
                count++;
            }
            if (count == 0) text.AppendLine(SettingsUiLanguage.T("无", "None"));
            text.AppendLine();
        }

        private static string DisplayName(PerceivedEntityRecord record) =>
            record.KnownFields.TryGetValue("display_name", out string value) && !string.IsNullOrWhiteSpace(value)
                ? SettingsUiLanguage.NpcDisplayName(record.EntityId, value) : record.EntityId;
    }
}
