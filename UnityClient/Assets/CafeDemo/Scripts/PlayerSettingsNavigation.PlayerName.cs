using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    public sealed partial class PlayerSettingsNavigation
    {
        private TMP_InputField playerName;
        private TMP_Text playerNameStatus;
        private string playerNameResult;
        private void BuildPlayerName(Transform parent)
        {
            Height(Heading(parent, "玩家昵称", "Player name").gameObject, 40);
            var root = Node("PlayerNameInput", parent, typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
            Height(root, 56); SettingsPanelTheme.Surface(root.GetComponent<Image>(), SettingsPanelTheme.Row);
            var viewport = Node("Viewport", root.transform, typeof(RectMask2D));
            Place(viewport.transform, Vector2.zero, Vector2.one, new Vector2(14, 4), new Vector2(-14, -4));
            var text = Label(viewport.transform, "", "", 23, Vector2.zero, Vector2.one);
            labels.RemoveAll(x => x.text == text); text.richText = false;
            playerName = root.GetComponent<TMP_InputField>(); playerName.targetGraphic = root.GetComponent<Image>();
            playerName.textViewport = (RectTransform)viewport.transform; playerName.textComponent = (TextMeshProUGUI)text;
            playerName.lineType = TMP_InputField.LineType.SingleLine;
            playerName.characterLimit = 0; // Validation reports overflow; never silently truncate a pasted nickname.
            var row = Node("PlayerNameActions", parent, typeof(HorizontalLayoutGroup), typeof(LayoutElement)); Height(row, 56);
            var layout = row.GetComponent<HorizontalLayoutGroup>(); layout.spacing = 12;
            layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true;
            ActionButton(row.transform, "SavePlayerName", "保存昵称", "Save name", () => SavePlayerName(playerName.text));
            ActionButton(row.transform, "ResetPlayerName", "恢复默认昵称", "Restore default", () => SavePlayerName(PlayerNameSettings.DefaultName));
            playerNameStatus = Label(parent, "", "", 20); CompactPlayerText(playerNameStatus); FlexibleText(playerNameStatus, 28);
            labels.RemoveAll(x => x.text == playerNameStatus); RefreshPlayerName();
        }
        private void RefreshPlayerName()
        {
            if (playerName == null) return;
            playerName.SetTextWithoutNotify(PlayerNameSettings.DisplayName); playerNameResult = PlayerNameSettings.Error;
            RefreshPlayerNameStatus();
        }
        private void SavePlayerName(string value)
        {
            if (PlayerNameSettings.TrySave(value, out string error))
            { playerName.SetTextWithoutNotify(PlayerNameSettings.DisplayName); playerNameResult = "SAVED"; }
            else playerNameResult = error;
            RefreshPlayerNameStatus();
        }
        private void RefreshPlayerNameStatus()
        {
            if (playerNameStatus == null) return;
            playerNameStatus.text = playerNameResult == "SAVED" ? SettingsUiLanguage.T("昵称已保存，后续对话生效。", "Name saved for subsequent conversations.") :
                playerNameResult == "PLAYER_NAME_INVALID" ? SettingsUiLanguage.T("请输入 1–24 个 Unicode 字符；不能包含换行、控制或隐藏格式字符。", "Use 1–24 Unicode characters without line breaks, control or hidden format characters.") :
                !string.IsNullOrEmpty(playerNameResult) ? SettingsUiLanguage.T("无法读取或保存昵称，请检查本地设置权限。", "Cannot read or save the name. Check local settings permissions.") :
                SettingsUiLanguage.T("1–24 个 Unicode 字符；昵称不会改变玩家身份或双方约定的称呼。", "1–24 Unicode characters. Identity and agreed forms of address stay the same.");
        }
    }
}
