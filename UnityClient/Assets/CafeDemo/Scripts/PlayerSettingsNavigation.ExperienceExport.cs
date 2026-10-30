using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    public sealed partial class PlayerSettingsNavigation
    {
        private ExperienceDataExport experienceExport;
        private GameObject experienceConfirmation;
        private Button experienceExportButton;

        private void BuildExperienceExport(Transform parent, TMP_Text status)
        {
            experienceExport = window.gameObject.AddComponent<ExperienceDataExport>();
            status.richText = false;
            var statusSize = status.GetComponent<LayoutElement>();
            statusSize.minHeight = 64; statusSize.preferredHeight = -1;
            status.textWrappingMode = TextWrappingModes.Normal;
            experienceExportButton = ActionButton(parent, "ExportAllExperienceData", "导出全部体验数据", "Export all experience data", () =>
            {
                if (experienceExport.IsBusy) return;
                if (experienceConfirmation == null)
                {
                    experienceConfirmation = Node("ExperienceExportConfirmation", window, typeof(Image));
                    Place(experienceConfirmation.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                    experienceConfirmation.GetComponent<Image>().color = new Color(.12f, .10f, .08f, .86f);
                    var box = Node("ConfirmationBox", experienceConfirmation.transform, typeof(Image));
                    Place(box.transform, new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(-430,-180),new Vector2(430,180));
                    SettingsPanelTheme.Surface(box.GetComponent<Image>(), SettingsPanelTheme.Paper);
                    Label(box.transform,
                        "将导出当前玩家已保存的聊天、记忆、关系、任务、人设、世界知识和脱敏诊断。\n\n你填写的文本会完整保留，可能包含私人信息。请检查 ZIP 后再自行发送给开发者；游戏不会自动上传。\n\n不包含 API 密钥、认证文件、模型权重或角色模型文件。",
                        "Export this player's saved conversations, memories, relationships, tasks, Persona, world knowledge and sanitized diagnostics.\n\nYour authored text is preserved and may contain personal information. Review the ZIP before sending it to the developer. Nothing is uploaded automatically.\n\nAPI keys, authentication files, model weights and avatar binaries are excluded.",
                        22, new Vector2(.04f,.28f),new Vector2(.96f,.96f));
                    var confirm = ActionButton(box.transform,"ConfirmExperienceExport","确认导出","Export",()=>
                    {
                        experienceConfirmation.SetActive(false);
                        experienceExport.Begin(text => {
                            if (status != null) status.text = text;
                            if (status != null && status.gameObject.activeInHierarchy) {
                                Canvas.ForceUpdateCanvases();
                                var scroll = status.GetComponentInParent<ScrollRect>();
                                if (scroll != null) { LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content); scroll.verticalNormalizedPosition = 0; }
                            }
                        });
                    });
                    Place(confirm.transform,Vector2.zero,new Vector2(.5f,0),new Vector2(24,20),new Vector2(-12,76));
                    var cancel=ActionButton(box.transform,"CancelExperienceExport","取消","Cancel",()=>experienceConfirmation.SetActive(false));
                    Place(cancel.transform,new Vector2(.5f,0),Vector2.right,new Vector2(12,20),new Vector2(-24,76));
                }
                experienceConfirmation.SetActive(true); experienceConfirmation.transform.SetAsLastSibling();
            });
            Height(experienceExportButton.gameObject,56);
            experienceExportButton.transform.SetSiblingIndex(status.transform.GetSiblingIndex());
        }
        private void DisposeExperienceExport()
        {
            if (experienceExport != null) { if (Application.isPlaying) Object.Destroy(experienceExport); else Object.DestroyImmediate(experienceExport); }
            if (experienceConfirmation != null) { if (Application.isPlaying) Object.Destroy(experienceConfirmation); else Object.DestroyImmediate(experienceConfirmation); }
        }
    }
}
