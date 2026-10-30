using System;
using System.Collections.Generic;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    public sealed partial class PlayerSettingsNavigation
    {
        private GameObject appearance;
        private TMP_Text avatarState;
        private TMP_Dropdown avatarChoice;
        private Button avatarImport, avatarDefault, avatarScan, avatarFolder;
        private string[] avatarFiles = Array.Empty<string>();
        private PlayerVrmAppearance Appearance => UnityEngine.Object.FindAnyObjectByType<PlayerVrmAppearance>();
        private void BuildAppearance()
        {
            appearance = Page("PlayerAppearancePage");
            var viewport = Node("AppearanceViewport", appearance.transform, typeof(RectMask2D), typeof(Image));
            viewport.GetComponent<Image>().color = Color.clear;
            Place(viewport.transform, Vector2.zero, Vector2.one, new Vector2(0,100), Vector2.zero);
            var content = Node("AppearanceContent", viewport.transform, typeof(ContentSizeFitter));
            Place(content.transform, new Vector2(0,1), Vector2.one, Vector2.zero, Vector2.zero);
            ((RectTransform)content.transform).pivot = new Vector2(.5f,1);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scroll = appearance.AddComponent<ScrollRect>(); scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = (RectTransform)content.transform; scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 32;
            var stack = content.AddComponent<VerticalLayoutGroup>(); stack.padding = new RectOffset(36,36,20,20);
            stack.spacing = 12; stack.childForceExpandHeight = false; stack.childControlHeight = stack.childControlWidth = true;
            Height(Heading(content.transform, "玩家设定", "Player settings").gameObject, 44);
            BuildPlayerName(content.transform);
            BuildAppearanceDescription(content.transform);
            Height(Heading(content.transform, "角色外观", "Player appearance").gameObject, 36);
            var instructions = Label(content.transform,
                "先把 VRM 文件放进角色目录，再扫描、选择并使用。换角色前，请先结束当前动作或对话。",
                "Open the avatar folder, copy your own .vrm file into it, then scan, select and use it. Up to 64 MiB; stored locally. Finish movement, emotes and interaction first.", 21);
            CompactPlayerText(instructions); FlexibleText(instructions, 28);
            avatarState = Label(content.transform, "", "", 21); CompactPlayerText(avatarState); FlexibleText(avatarState, 28);
            labels.RemoveAll(x => x.text == avatarState);
            avatarChoice = UnityEngine.Object.Instantiate(language, content.transform);
            avatarChoice.name = "PlayerAvatarChoice"; avatarChoice.onValueChanged = new TMP_Dropdown.DropdownEvent();
            avatarChoice.gameObject.SetActive(true);
            if (avatarChoice.TryGetComponent<LayoutElement>(out var existing)) existing.ignoreLayout = false;
            Height(avatarChoice.gameObject, 56);
            var footer = Node("AvatarFooter", appearance.transform, typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var footerSize = footer.GetComponent<LayoutElement>(); footerSize.ignoreLayout = true;
            Place(footer.transform, Vector2.zero, Vector2.right, new Vector2(36,20), new Vector2(-36,76));
            var row = footer.GetComponent<HorizontalLayoutGroup>(); row.spacing = 12;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = true; row.childForceExpandHeight = false;
            avatarFolder = ActionButton(footer.transform, "AvatarOpenFolder", "打开角色目录", "Open folder", OpenAvatarFolder);
            avatarScan = ActionButton(footer.transform, "AvatarScan", "扫描 VRM", "Scan VRMs", ScanAvatars);
            avatarImport = ActionButton(footer.transform, "AvatarImport", "导入并使用", "Import and use", ImportAvatar);
            avatarDefault = ActionButton(footer.transform, "AvatarDefault", "恢复默认角色", "Restore default", () => { Appearance?.RestoreDefault(); RefreshAppearance(); });
            RefreshAppearance();
        }
        public void ShowAppearance() => Navigate(() => { appearance.SetActive(true); RefreshPlayerName(); ScanAvatars(); }, "AppearanceSettingsNav", false);
        private static void CompactPlayerText(TMP_Text text)
        {
            // Let wrapped text determine its height instead of centering it in an oversized reserved row.
            text.alignment = TextAlignmentOptions.TopLeft;
            text.characterSpacing = text.wordSpacing = text.lineSpacing = text.paragraphSpacing = 0;
            text.margin = Vector4.zero;
            text.textWrappingMode = TextWrappingModes.Normal;
        }
        private void RefreshAppearance()
        {
            if (avatarState == null) return;
            var service = Appearance;
            bool available = service != null && !service.IsLoading;
            avatarImport.interactable = available && avatarFiles.Length > 0;
            avatarDefault.interactable = available; avatarScan.interactable = available; avatarFolder.interactable = available;
            avatarChoice.interactable = available && avatarFiles.Length > 0;
            RefreshAppearanceDescription(service, available);
            avatarState.text = service == null ? SettingsUiLanguage.T("进入游戏后可更换玩家外观。", "Enter the game to change player appearance.") :
                service.IsLoading ? SettingsUiLanguage.T("正在验证并加载 VRM…原外观保持显示。", "Validating and loading VRM… Current appearance remains visible.") :
                (service.IsCustom ? SettingsUiLanguage.T("当前：自定义角色", "Current: custom avatar") : SettingsUiLanguage.T("当前：默认角色", "Current: default avatar")) +
                (string.IsNullOrEmpty(service.Diagnostic) ? "" : "\n" + SettingsUiLanguage.T("操作未完成：", "Operation incomplete: ") + service.Diagnostic);
        }
        private void ScanAvatars()
        {
            try
            {
                avatarFiles = Appearance?.Store.Scan() ?? Array.Empty<string>();
                avatarChoice.ClearOptions(); avatarChoice.AddOptions(new List<string>(avatarFiles.Length == 0 ?
                    new[] { SettingsUiLanguage.T("目录中没有 VRM", "No VRM files in folder") } : avatarFiles));
                avatarChoice.SetValueWithoutNotify(0); avatarChoice.RefreshShownValue(); RefreshAppearance();
            }
            catch (Exception) { avatarState.text = SettingsUiLanguage.T("无法扫描角色目录，请检查 UserData 权限或目录链接。", "Cannot scan avatar folder. Check UserData permissions and directory links."); }
        }
        private void OpenAvatarFolder()
        {
            try
            {
                var store = Appearance?.Store; if (store == null) return;
                store.EnsureDirectory();
                Application.OpenURL(new Uri(store.DirectoryPath + System.IO.Path.DirectorySeparatorChar).AbsoluteUri);
            }
            catch (Exception) { avatarState.text = SettingsUiLanguage.T("无法打开角色目录，请检查 UserData 权限。", "Cannot open avatar folder. Check UserData permissions."); }
        }
        private async void ImportAvatar()
        {
            var service = Appearance;
            if (service == null || avatarChoice.value < 0 || avatarChoice.value >= avatarFiles.Length) return;
            var pending = service.ImportScannedAsync(avatarFiles[avatarChoice.value]); RefreshAppearance();
            await pending;
            if (avatarState != null) RefreshAppearance();
        }
    }
}
