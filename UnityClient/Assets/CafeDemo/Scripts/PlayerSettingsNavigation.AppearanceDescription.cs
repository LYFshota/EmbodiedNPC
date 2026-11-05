using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    public sealed partial class PlayerSettingsNavigation
    {
        private TMP_InputField appearanceDescription;
        private TMP_Text appearanceDescriptionStatus;
        private Button appearanceSave, appearanceClear;
        private string descriptionModelKey, observedDescription, descriptionResult;
        private void RefreshDescriptionStatus()
        {
            if (appearanceDescriptionStatus == null) return;
            appearanceDescriptionStatus.text = descriptionResult switch {
                "dirty" => SettingsUiLanguage.T("描述尚未保存。", "Description has unsaved changes."),
                "saved" => SettingsUiLanguage.T("外观描述已保存。", "Appearance description saved."),
                "invalid" => SettingsUiLanguage.T("请填写 160 字以内的文字。", "Please keep the description within 160 characters."),
                "failed" => SettingsUiLanguage.T("暂时无法保存，请稍后重试。", "Could not save right now. Please try again shortly."),
                _ => ""
            };
        }
        private void BuildAppearanceDescription(Transform parent)
        {
            Height(Heading(parent, "外观描述", "Appearance description").gameObject, 36);
            var root = TMP_DefaultControls.CreateInputField(default); root.name = "AppearanceDescriptionInput"; root.transform.SetParent(parent,false);
            Height(root, 120);
            // TMP_InputField also supplies a preferred height. Keep the editor bounded so long text scrolls inside it.
            root.GetComponent<LayoutElement>().layoutPriority = 2;
            foreach(var label in root.GetComponentsInChildren<TMP_Text>(true))label.font=font;
            appearanceDescription = root.GetComponent<TMP_InputField>();
            appearanceDescription.contentType = TMP_InputField.ContentType.Standard;
            appearanceDescription.lineType = TMP_InputField.LineType.MultiLineNewline;
            appearanceDescription.richText = false; appearanceDescription.resetOnDeActivation = false;
            Place(appearanceDescription.textViewport, Vector2.zero, Vector2.one, new Vector2(14,8), new Vector2(-14,-8));
            CompactPlayerText(appearanceDescription.textComponent);
            appearanceDescription.textComponent.fontSize = 21;
            if(appearanceDescription.placeholder is TMP_Text placeholder)
            { CompactPlayerText(placeholder); labels.Add((placeholder,"描述角色的发色和服装。","Describe your character’s hair and clothing."));placeholder.text=SettingsUiLanguage.T(labels[labels.Count-1].zh,labels[labels.Count-1].en); }
            appearanceDescription.onValueChanged.AddListener(_ => {
                descriptionResult = "dirty"; RefreshDescriptionStatus();
            });
            appearanceDescription.scrollSensitivity = 2;
            SettingsPanelTheme.Apply(root);
            var actions = Node("AppearanceDescriptionActions", parent, typeof(HorizontalLayoutGroup), typeof(LayoutElement)); Height(actions, 56);
            var row = actions.GetComponent<HorizontalLayoutGroup>(); row.spacing = 12;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = true; row.childForceExpandHeight = false;
            appearanceSave = ActionButton(actions.transform, "SaveAppearanceDescription", "保存描述", "Save description", () => SaveAppearanceDescription(appearanceDescription.text));
            appearanceClear = ActionButton(actions.transform, "ClearAppearanceDescription", "清除描述", "Clear description", () => SaveAppearanceDescription(""));
            var hint = Label(parent, "NPC 看见你时，会参考这段描述。最多 160 字；更换角色后，请按新的外观修改。",
                "NPCs can refer to this when they see you. Up to 160 characters; update it to match your new avatar after switching.", 19); CompactPlayerText(hint); FlexibleText(hint, 28);
            appearanceDescriptionStatus = Label(parent, "", "", 19); CompactPlayerText(appearanceDescriptionStatus); FlexibleText(appearanceDescriptionStatus, 0);
            labels.RemoveAll(x => x.text == appearanceDescriptionStatus);
        }
        private void RefreshAppearanceDescription(PlayerVrmAppearance service, bool available)
        {
            if (appearanceDescription == null) return;
            RefreshDescriptionStatus();
            appearanceDescription.interactable = available; appearanceSave.interactable = appearanceClear.interactable = available;
            string key = service?.Store?.Selected ?? PortablePreferences.Current.PlayerAvatar ?? "builtin:default";
            string current = service != null ? service.AppearanceDescription : PortablePreferences.Current.PlayerAppearanceDescription;
            if (key == descriptionModelKey && current == observedDescription) return;
            descriptionModelKey = key; observedDescription = current; appearanceDescription.SetTextWithoutNotify(current ?? "");
            appearanceDescription.MoveTextStart(false); appearanceDescription.textComponent.rectTransform.anchoredPosition = Vector2.zero; appearanceDescription.ForceLabelUpdate();
            descriptionResult = null; RefreshDescriptionStatus();
        }
        private void SaveAppearanceDescription(string value)
        {
            if (!PortablePreferenceFile.TryValidateAppearanceDescription(value, out var description))
            { descriptionResult = "invalid"; RefreshDescriptionStatus(); return; }
            bool saved = Appearance?.SaveAppearanceDescription(description) == true;
            if (saved) { observedDescription = description; appearanceDescription.SetTextWithoutNotify(description ?? ""); appearanceDescription.MoveTextStart(false); appearanceDescription.textComponent.rectTransform.anchoredPosition = Vector2.zero; appearanceDescription.ForceLabelUpdate(); }
            descriptionResult = saved ? "saved" : "failed"; RefreshDescriptionStatus();
        }
    }
}
