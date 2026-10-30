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
        private string descriptionModelKey, observedDescription;
        private void BuildAppearanceDescription(Transform parent)
        {
            Height(Heading(parent, "外观描述（选填）", "Appearance description (optional)").gameObject, 36);
            var root = TMP_DefaultControls.CreateInputField(default); root.name = "AppearanceDescriptionInput"; root.transform.SetParent(parent,false);
            Height(root, 120);
            foreach(var label in root.GetComponentsInChildren<TMP_Text>(true))label.font=font;
            appearanceDescription = root.GetComponent<TMP_InputField>();
            appearanceDescription.contentType = TMP_InputField.ContentType.Standard;
            appearanceDescription.lineType = TMP_InputField.LineType.MultiLineNewline;
            appearanceDescription.richText = false; appearanceDescription.resetOnDeActivation = false;
            Place(appearanceDescription.textViewport, Vector2.zero, Vector2.one, new Vector2(14,8), new Vector2(-14,-8));
            CompactPlayerText(appearanceDescription.textComponent);
            appearanceDescription.textComponent.fontSize = 21;
            if(appearanceDescription.placeholder is TMP_Text placeholder)
            { CompactPlayerText(placeholder); labels.Add((placeholder,"例如：银白色长发，穿着浅蓝色外套和黑色短裙。","For example: long silver hair, a light blue jacket and a black skirt."));placeholder.text=SettingsUiLanguage.T(labels[labels.Count-1].zh,labels[labels.Count-1].en); }
            appearanceDescription.scrollSensitivity = 2;
            SettingsPanelTheme.Apply(root);
            var actions = Node("AppearanceDescriptionActions", parent, typeof(HorizontalLayoutGroup), typeof(LayoutElement)); Height(actions, 56);
            var row = actions.GetComponent<HorizontalLayoutGroup>(); row.spacing = 12;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = true; row.childForceExpandHeight = false;
            appearanceSave = ActionButton(actions.transform, "SaveAppearanceDescription", "保存描述", "Save description", () => SaveAppearanceDescription(appearanceDescription.text));
            appearanceClear = ActionButton(actions.transform, "ClearAppearanceDescription", "清除描述", "Clear description", () => SaveAppearanceDescription(""));
            var hint = Label(parent, "NPC 看见你时，会参考这里的描述。最多160字，换角色后请重新填写。",
                "NPCs can refer to this when they see you. Up to 160 characters; fill it in again after changing characters.", 19); CompactPlayerText(hint); FlexibleText(hint, 28);
            appearanceDescriptionStatus = Label(parent, "", "", 19); CompactPlayerText(appearanceDescriptionStatus); FlexibleText(appearanceDescriptionStatus, 0);
            labels.RemoveAll(x => x.text == appearanceDescriptionStatus);
        }
        private void RefreshAppearanceDescription(PlayerVrmAppearance service, bool available)
        {
            if (appearanceDescription == null) return;
            appearanceDescription.interactable = available; appearanceSave.interactable = appearanceClear.interactable = available;
            string key = service?.Store?.Selected ?? "builtin:default";
            string current = service?.AppearanceDescription;
            if (key == descriptionModelKey && current == observedDescription) return;
            descriptionModelKey = key; observedDescription = current; appearanceDescription.SetTextWithoutNotify(current ?? "");
            appearanceDescription.MoveTextStart(false); appearanceDescription.textComponent.rectTransform.anchoredPosition = Vector2.zero; appearanceDescription.ForceLabelUpdate();
            appearanceDescriptionStatus.text = "";
        }
        private void SaveAppearanceDescription(string value)
        {
            if (!PortablePreferenceFile.TryValidateAppearanceDescription(value, out var description))
            { appearanceDescriptionStatus.text = SettingsUiLanguage.T("请填写160字以内的文字。", "Please keep the description within 160 characters."); return; }
            bool saved = Appearance?.SaveAppearanceDescription(description) == true;
            if (saved) { appearanceDescription.SetTextWithoutNotify(description ?? ""); appearanceDescription.MoveTextStart(false); appearanceDescription.textComponent.rectTransform.anchoredPosition = Vector2.zero; appearanceDescription.ForceLabelUpdate(); }
            appearanceDescriptionStatus.text = saved ? SettingsUiLanguage.T("外观描述已保存。", "Appearance description saved.") :
                SettingsUiLanguage.T("暂时无法保存，请稍后重试。", "Could not save right now. Please try again shortly.");
        }
    }
}
