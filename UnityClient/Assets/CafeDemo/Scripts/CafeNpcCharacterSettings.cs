using System;
using System.Collections.Generic;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Optional Cafe persona editor; it never edits CharacterDefinition or gameplay capabilities.</summary>
    public sealed class CafeNpcCharacterSettings : MonoBehaviour
    {
        [SerializeField] private DialogueController dialogue;
        [SerializeField] private TMP_Dropdown npcDropdown;
        [SerializeField] private TMP_Text characterIdText;
        [SerializeField] private TMP_InputField displayName;
        [SerializeField] private TMP_InputField roleSummary;
        [SerializeField] private TMP_InputField backstory;
        [SerializeField] private TMP_InputField personality;
        [SerializeField] private TMP_InputField speakingStyle;
        [SerializeField] private TMP_InputField boundaries;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button restoreButton;
        [SerializeField] private Button saveButton;

        private readonly List<CognitiveContextComposer> candidates = new();
        private CharacterOverrideStore store;
        private bool selectionExplicit;
        private string lastChineseStatus;
        private string lastEnglishStatus;
        private TMP_Text backstoryLabel;

        public event Action<CognitiveContextComposer> SelectionChanged;
        public int CandidateCount => candidates.Count;
        public CognitiveContextComposer SelectedComposer => npcDropdown != null &&
            npcDropdown.value >= 0 && npcDropdown.value < candidates.Count ? candidates[npcDropdown.value] : null;
        public CognitiveContextComposer GetCandidate(int index) =>
            index >= 0 && index < candidates.Count ? candidates[index] : null;
        public int SelectedIndex => SelectedComposer == null ? -1 : npcDropdown.value;
        public string SelectedCharacterId => CurrentDefinition == null ? null : CurrentDefinition.CharacterId;
        private CharacterDefinition CurrentDefinition => SelectedComposer?.CharacterDefinition;

        public void SelectCandidate(int index)
        {
            if (GetCandidate(index) == null || npcDropdown == null) return;
            selectionExplicit = true;
            npcDropdown.SetValueWithoutNotify(index);
            npcDropdown.RefreshShownValue();
            LoadSelected();
        }

        public void ResetSelectionPreference() => selectionExplicit = false;

        private void OnEnable()
        {
            SettingsUiLanguage.Changed += RefreshLanguage;
            npcDropdown.onValueChanged.AddListener(OnNpcChanged);
            restoreButton.onClick.AddListener(RestoreDefault);
            saveButton.onClick.AddListener(Save);
            // Keep over-limit drafts intact; report and reject them at the save boundary.
            backstory.characterLimit = 0;
            backstory.onValueChanged.AddListener(OnBackgroundChanged);
            RefreshBackgroundFeedback();
        }

        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshLanguage;
            npcDropdown.onValueChanged.RemoveListener(OnNpcChanged);
            restoreButton.onClick.RemoveListener(RestoreDefault);
            saveButton.onClick.RemoveListener(Save);
            backstory.onValueChanged.RemoveListener(OnBackgroundChanged);
        }

        public void RefreshCandidates()
        {
            store ??= CharacterOverrideStore.Shared;
            string previousEntityId = selectionExplicit ? SelectedComposer?.GetComponent<WorldEntity>()?.EntityId : null;
            candidates.Clear();
            candidates.AddRange(FindObjectsByType<CognitiveContextComposer>(FindObjectsSortMode.None)
                .Where(x => x.gameObject.scene == gameObject.scene && x.CharacterDefinition != null &&
                            x.CharacterDefinition.TryValidateConfiguration(out _))
                .OrderBy(x => x.CharacterDefinition.CharacterId, StringComparer.Ordinal));

            npcDropdown.ClearOptions();
            npcDropdown.AddOptions(candidates.Select(x =>
                SettingsUiLanguage.NpcDisplayName(x.CharacterDefinition.CharacterId,
                    CharacterProfileResolver.Resolve(x.CharacterDefinition, store).display_name)).ToList());
            if (candidates.Count == 0)
            {
                characterIdText.text = SettingsUiLanguage.T("暂无可编辑 NPC", "No editable NPC");
                displayName.text = roleSummary.text = backstory.text = personality.text = speakingStyle.text = boundaries.text = string.Empty;
                saveButton.interactable = restoreButton.interactable = false;
                SetStatus("当前场景没有可编辑 NPC。", "No editable NPC in this scene.");
                SelectionChanged?.Invoke(null);
                return;
            }

            string selectedEntityId = previousEntityId ?? dialogue?.SelectedTarget?.Entity?.EntityId;
            int preferred = candidates.FindIndex(x =>
                string.Equals(x.GetComponent<WorldEntity>()?.EntityId, selectedEntityId,
                    StringComparison.Ordinal));
            if (previousEntityId != null && preferred < 0) selectionExplicit = false;
            npcDropdown.SetValueWithoutNotify(preferred < 0 ? 0 : preferred);
            npcDropdown.RefreshShownValue();
            saveButton.interactable = restoreButton.interactable = true;
            LoadSelected();
        }

        private void OnNpcChanged(int _)
        {
            selectionExplicit = true;
            LoadSelected();
        }

        private void LoadSelected()
        {
            CharacterDefinition definition = CurrentDefinition;
            if (definition == null) return;
            CognitiveCharacterDto effective = CharacterProfileResolver.Resolve(definition, store);
            characterIdText.text = effective.character_id;
            displayName.text = effective.display_name;
            roleSummary.text = effective.role_summary;
            backstory.text = effective.backstory;
            personality.text = effective.personality;
            speakingStyle.text = effective.speaking_style;
            boundaries.text = effective.boundaries;
            if (store.LoadRejected)
                SetStatus("人设文件无法读取，暂用默认值；原文件保留，请备份后检查。", "Character file could not be read; using defaults. The file is preserved. Back up and review it.");
            else if (store.TryGet(definition.CharacterId, out _))
                SetStatus("当前使用本地人设设置。", "Using local character settings.");
            else SetStatus("当前使用内置默认人设。", "Using built-in character defaults.");
            SelectionChanged?.Invoke(SelectedComposer);
        }

        private void Save()
        {
            CharacterDefinition definition = CurrentDefinition;
            if (definition == null) return;
            RefreshBackgroundFeedback();
            if (CharacterDefinition.CountCharacters(backstory.text) > CharacterDefinition.BackstoryLimit) return;
            try
            {
                if (string.Equals(displayName.text.Trim(), definition.DisplayName, StringComparison.Ordinal) &&
                    string.Equals(roleSummary.text.Trim(), definition.RoleSummary, StringComparison.Ordinal) &&
                    string.Equals(backstory.text.Trim(), definition.Backstory, StringComparison.Ordinal) &&
                    string.Equals(personality.text.Trim(), definition.Personality, StringComparison.Ordinal) &&
                    string.Equals(boundaries.text.Trim(), definition.Boundaries, StringComparison.Ordinal) &&
                    string.Equals(speakingStyle.text.Trim(), definition.SpeakingStyle, StringComparison.Ordinal))
                    store.Remove(definition.CharacterId);
                else store.Save(definition.CharacterId, displayName.text, roleSummary.text, backstory.text,
                    personality.text, speakingStyle.text, boundaries.text);
                UpdateSelectedLabel();
                SetStatus("已保存，将从下一次 NPC 思考开始生效。", "Saved. Applies from the next NPC thought.");
            }
            catch (Exception error) when (error is ArgumentException || error is System.IO.IOException ||
                                          error is UnauthorizedAccessException)
            {
                SetStatus("保存失败：请检查必填内容、字符上限和本地文件权限。",
                    "Save failed. Check required fields, character limits and local file permissions.");
                Debug.LogWarning("NPC character override save failed: " + error.GetType().Name);
            }
        }

        private void RestoreDefault()
        {
            CharacterDefinition definition = CurrentDefinition;
            if (definition == null) return;
            try
            {
                store.Remove(definition.CharacterId);
                LoadSelected();
                UpdateSelectedLabel();
                SetStatus("已恢复默认人设，将从下一次 NPC 思考开始生效。",
                    "Character defaults restored. Applies from the next NPC thought.");
            }
            catch (Exception error) when (error is ArgumentException || error is System.IO.IOException || error is UnauthorizedAccessException)
            {
                SetStatus(store.LoadRejected ? "恢复失败：人设文件无法读取，原文件保留，请备份后检查。" : "恢复失败：请检查本地文件权限。",
                    store.LoadRejected ? "Restore failed: character file cannot be read. The file is preserved. Back up and review it." : "Restore failed. Check local file permissions.");
                Debug.LogWarning("NPC character override remove failed: " + error.GetType().Name);
            }
        }

        private void UpdateSelectedLabel()
        {
            int index = npcDropdown.value;
            if (index < 0 || index >= npcDropdown.options.Count) return;
            npcDropdown.options[index].text = SettingsUiLanguage.NpcDisplayName(CurrentDefinition.CharacterId,
                CharacterProfileResolver.Resolve(CurrentDefinition, store).display_name);
            npcDropdown.RefreshShownValue();
        }

        private void SetStatus(string chinese, string english)
        {
            lastChineseStatus = chinese;
            lastEnglishStatus = english;
            RefreshLanguage();
        }

        private void RefreshLanguage()
        {
            // Captions only: never reload the selected editor or overwrite an unsaved name.
            for (int i = 0; i < candidates.Count && i < npcDropdown.options.Count; i++)
                npcDropdown.options[i].text = SettingsUiLanguage.NpcDisplayName(candidates[i].CharacterDefinition.CharacterId,
                    CharacterProfileResolver.Resolve(candidates[i].CharacterDefinition, store).display_name);
            npcDropdown.RefreshShownValue();
            RefreshBackgroundFeedback();
        }

        private void OnBackgroundChanged(string _) => RefreshBackgroundFeedback();

        public void RefreshBackgroundFeedback()
        {
            if (backstory == null) return;
            if (backstoryLabel == null)
                backstoryLabel = GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(x => x.name == "BackstoryLabel");
            int count = CharacterDefinition.CountCharacters(backstory.text);
            int limit = CharacterDefinition.BackstoryLimit;
            bool oversized = count > limit;
            if (backstoryLabel != null)
            {
                backstoryLabel.text = SettingsUiLanguage.T($"人物背景\n（{count} / {limit}）", $"Background\n({count} / {limit})");
                backstoryLabel.color = oversized ? new Color32(164, 57, 42, 255) : SettingsPanelTheme.Ink;
            }
            if (saveButton != null) saveButton.interactable = CurrentDefinition != null && !oversized;
            if (oversized && statusText != null)
                statusText.text = SettingsUiLanguage.T($"人物背景超过 {limit} 字符，请缩短后保存。",
                    $"Background exceeds {limit} characters. Shorten it before saving.");
            else if (statusText != null && lastChineseStatus != null)
                statusText.text = SettingsUiLanguage.T(lastChineseStatus, lastEnglishStatus);
        }
    }
}
