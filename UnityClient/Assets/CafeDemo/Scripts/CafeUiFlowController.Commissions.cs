using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CafeDemo
{
    public sealed partial class CafeUiFlowController
    {
        private Button commissionEntry, taskCancel, commissionBack;
        private GameObject commissionSurface;
        private TMP_Text commissionHeading, taskStatus;
        private string commissionAgent, commissionTaskId, confirmationAttempt, commissionNotice = "";
        private int confirmationVersion;
        private bool commissionConfirming;
        private TaskStatusDto displayedCommission;
        private bool mayCancelCommission;

        private void CreateCommissionControls()
        {
            commissionEntry = Instantiate(helpButton, helpButton.transform.parent);
            commissionEntry.name = "CurrentCommission";
            commissionEntry.onClick = new Button.ButtonClickedEvent();
            commissionEntry.onClick.AddListener(OpenCommissions);
            var entryRect = (RectTransform)commissionEntry.transform;
            entryRect.anchorMin = entryRect.anchorMax = new Vector2(.5f, 1);
            entryRect.anchoredPosition = new Vector2(0, -590); entryRect.sizeDelta = new Vector2(280, 40);
            var image = commissionEntry.GetComponent<Image>();
            if (image != null) { var color = image.color; color.a *= .45f; image.color = color; }
            commissionEntry.GetComponentInChildren<TMP_Text>(true).fontSize = 22;
            commissionEntry.gameObject.SetActive(false);

            commissionSurface = Instantiate(helpSurface, helpSurface.transform.parent);
            commissionSurface.name = "CommissionManagement";
            taskStatus = commissionSurface.GetComponentsInChildren<TMP_Text>(true).Single(t => t.name == helpText.name);
            taskStatus.name = "CommissionStatus";
            var card = (RectTransform)taskStatus.transform.parent;
            card.sizeDelta = new Vector2(680, 500);
            commissionHeading = Instantiate(heading, card); commissionHeading.name = "CommissionHeading";
            ((RectTransform)commissionHeading.transform).anchoredPosition = new Vector2(0, -60);
            commissionHeading.fontSize = 34;
            var body = (RectTransform)taskStatus.transform;
            body.anchoredPosition = new Vector2(0, -236); body.sizeDelta = new Vector2(-88, 264);
            taskStatus.fontSize = 23; taskStatus.alignment = TextAlignmentOptions.TopLeft;
            taskStatus.enableAutoSizing = true; taskStatus.fontSizeMin = 18; taskStatus.fontSizeMax = 23;
            commissionBack = commissionSurface.GetComponentInChildren<Button>(true);
            commissionBack.name = "CommissionBack";
            taskCancel = Instantiate(commissionBack, card); taskCancel.name = "CancelPlayerCommission";
            commissionBack.onClick = new Button.ButtonClickedEvent(); commissionBack.onClick.AddListener(BackFromCommissions);
            taskCancel.onClick = new Button.ButtonClickedEvent(); taskCancel.onClick.AddListener(CancelPlayerCommission);
            foreach (var pair in new[] { (commissionBack, -144f), (taskCancel, 144f) })
            {
                var rect = (RectTransform)pair.Item1.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0); rect.pivot = new Vector2(.5f, .5f);
                rect.anchoredPosition = new Vector2(pair.Item2, 58); rect.sizeDelta = new Vector2(264, 64);
                pair.Item1.GetComponentInChildren<TMP_Text>(true).fontSize = 24;
            }
            taskCancel.gameObject.SetActive(false); commissionSurface.SetActive(false);
        }

        public void RefreshTaskFeedback()
        {
            if (taskRuntime == null) taskRuntime = FindAnyObjectByType<TaskRuntimeBridge>();
            TaskStatusDto candidate = null; string agent = null;
            bool cancellable = taskRuntime != null && taskRuntime.TryCancellationCandidate(out agent, out candidate);
            // Keep an open panel bound to its original task. Never silently confirm a different candidate.
            if (!commissionSurface.activeSelf)
            {
                commissionAgent = agent; displayedCommission = candidate;
                if (displayedCommission == null && taskRuntime != null)
                    foreach (string owner in taskRuntime.StatusAgentIds)
                    {
                        var active = taskRuntime.StatusFor(owner).FirstOrDefault(t => t.source == "player_request" &&
                            (t.status == "waiting" || t.status == "executing" || t.status == "blocked"));
                        if (active == null) continue;
                        commissionAgent = owner; displayedCommission = active; break;
                    }
                commissionTaskId = displayedCommission?.task_id;
            }
            else displayedCommission = taskRuntime?.StatusFor(commissionAgent ?? "").FirstOrDefault(t => t.task_id == commissionTaskId);
            mayCancelCommission = cancellable && commissionAgent == agent && displayedCommission != null &&
                displayedCommission.task_id == candidate.task_id;
            if (commissionConfirming && (!mayCancelCommission || displayedCommission.version != confirmationVersion ||
                displayedCommission.attempt_id != confirmationAttempt))
            {
                commissionConfirming = false; commissionNotice = "changed";
                EventSystem.current?.SetSelectedGameObject(commissionBack.gameObject);
            }
            bool activeTask = displayedCommission != null && displayedCommission.status != "completed" && displayedCommission.status != "cancelled";
            bool issue = taskRuntime != null && TaskFeedbackText(taskRuntime.FeedbackCode).Length > 0;
            commissionEntry.gameObject.SetActive(activeTask || issue);
            if (!commissionEntry.gameObject.activeSelf && EventSystem.current?.currentSelectedGameObject == commissionEntry.gameObject)
                EventSystem.current.SetSelectedGameObject(resumeButton.gameObject);
            taskCancel.gameObject.SetActive(mayCancelCommission);
            if (!mayCancelCommission && EventSystem.current?.currentSelectedGameObject == taskCancel.gameObject)
                EventSystem.current.SetSelectedGameObject(commissionBack.gameObject);
            RefreshCommissionLanguage(SettingsUiLanguage.IsEnglish);
        }

        private void RefreshCommissionLanguage(bool english)
        {
            if (commissionEntry == null) return;
            string L(string zh, string en) => english ? en : zh;
            commissionEntry.GetComponentInChildren<TMP_Text>(true).text = L("当前委托", "Current commission") + "  ›";
            commissionHeading.text = commissionConfirming ? L("取消这项委托？", "Cancel this commission?") : L("当前委托", "Current commission");
            string text = displayedCommission == null ? L("当前没有可管理的委托。", "No manageable commission.") :
                SettingsUiLanguage.NpcDisplayName(commissionAgent, taskRuntime.CancellationOwnerLabel(commissionAgent)) + "\n" +
                L("状态：", "Status: ") + CommissionStateText(displayedCommission, english) + "\n" +
                (mayCancelCommission ? L("可请求取消。", "You may request cancellation.") : L("当前不可重复请求取消。", "Cancellation is currently unavailable."));
            string feedback = taskRuntime == null ? "" : TaskFeedbackText(taskRuntime.FeedbackCode, english);
            if (feedback.Length > 0) text += "\n\n" + L("最近反馈：", "Latest feedback: ") + feedback;
            if (commissionConfirming) text += "\n\n" + L("取消需等待确认；执行中的动作不保证立即停止。", "Cancellation needs confirmation; an action in progress may not stop immediately.");
            else if (commissionNotice == "changed") text += "\n\n" + L("委托状态已更新，请重新检查。", "Commission state changed. Please review it again.");
            else if (commissionNotice == "unavailable") text += "\n\n" + L("暂时无法提交取消，请检查最新状态。", "Cancellation could not be submitted. Check the latest state.");
            if (taskStatus.text != text) taskStatus.text = text;
            commissionBack.GetComponentInChildren<TMP_Text>(true).text = commissionConfirming ? L("保留委托", "Keep commission") : L("返回游戏菜单", "Back to menu");
            taskCancel.GetComponentInChildren<TMP_Text>(true).text = commissionConfirming ? L("确认取消", "Confirm cancellation") : L("取消委托", "Cancel commission");
        }

        private static string CommissionStateText(TaskStatusDto task, bool english)
        {
            if (task.cancel_requested) return english ? "Cancellation pending" : "取消处理中";
            switch (task.status)
            {
                case "waiting": return english ? "Waiting" : "等待中";
                case "executing": return english ? "In progress" : "执行中";
                case "blocked": return english ? "Needs verification" : "待核实";
                case "completed": return english ? "Completed" : "已完成";
                case "cancelled": return english ? "Cancelled" : "已取消";
                default: return english ? "Awaiting synchronization" : "待同步";
            }
        }

        private void OpenCommissions()
        {
            if (!ownsInput || (settings != null && settings.IsOpen) || IsExitConfirmationOpen) return;
            RefreshTaskFeedback(); if (!commissionEntry.gameObject.activeSelf) return;
            commissionConfirming = false; commissionNotice = "";
            menuSurface.SetActive(false); commissionSurface.SetActive(true); RefreshTaskFeedback();
            EventSystem.current?.SetSelectedGameObject(commissionBack.gameObject);
        }
        private void BackFromCommissions()
        {
            if (commissionConfirming) { commissionConfirming = false; commissionNotice = ""; RefreshTaskFeedback(); return; }
            commissionSurface.SetActive(false); if (ownsInput) menuSurface.SetActive(true);
            RefreshTaskFeedback();
            EventSystem.current?.SetSelectedGameObject(commissionEntry.gameObject.activeSelf ? commissionEntry.gameObject : resumeButton.gameObject);
        }
        private bool CloseCommissionLayer()
        {
            if (!commissionSurface.activeSelf) return false;
            BackFromCommissions(); return true;
        }
        private void CancelPlayerCommission()
        {
            if (!ownsInput || !commissionSurface.activeSelf || taskRuntime == null) return;
            bool confirming = commissionConfirming;
            RefreshTaskFeedback();
            if (!mayCancelCommission || (confirming && !commissionConfirming)) return;
            if (!confirming)
            {
                confirmationVersion = displayedCommission.version; confirmationAttempt = displayedCommission.attempt_id;
                commissionConfirming = true; commissionNotice = ""; RefreshTaskFeedback();
                EventSystem.current?.SetSelectedGameObject(commissionBack.gameObject); return;
            }
            // Reacquire the trusted DTO after refresh; never submit the snapshot that opened confirmation.
            if (!taskRuntime.TryCancellationCandidate(out var agent, out var current) || agent != commissionAgent ||
                current.task_id != commissionTaskId || current.version != confirmationVersion || current.attempt_id != confirmationAttempt) return;
            commissionConfirming = false;
            commissionNotice = taskRuntime.RequestPlayerCancellation(agent, current) ? "" : "unavailable";
            RefreshTaskFeedback(); EventSystem.current?.SetSelectedGameObject(commissionBack.gameObject);
        }
    }
}
