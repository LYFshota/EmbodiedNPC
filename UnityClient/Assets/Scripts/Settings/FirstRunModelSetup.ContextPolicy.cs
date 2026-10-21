using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    public sealed partial class FirstRunModelSetup
    {
        private TMP_Dropdown recentMode, recentPresets;
        private TMP_InputField recentTarget;
        private TMP_Text recentState, recentHelp, recentTitle;
        private ContextPolicyView contextPolicyView;
        private string contextPolicyError;
        private bool contextPolicyConnected;
        private Coroutine contextReload;
        private bool modelSaveConfirmed;
        private bool contextSaveInProgress, completeAfterContextSave;
        private string combinedModelChinese, combinedModelEnglish;

        private void EnsureContextPolicyControls()
        {
            if (recentMode != null || advancedSettings == null) return;
            Transform parent = advancedSettings.transform;
            recentTitle = SectionTitle(parent, "RecentDialogueTitle");
            recentMode = (TMP_Dropdown)AddProfileRow(mainProvider, "RecentDialogueMode", "保留方式", "Retention mode", parent);
            recentMode.ClearOptions(); recentMode.AddOptions(new List<string> { "Auto", "Manual" });
            recentMode.interactable = true;
            recentMode.SetValueWithoutNotify(0);
            recentTarget = (TMP_InputField)AddProfileRow(mainTimeout, "RecentDialogueTarget", "近期原始消息目标", "Raw message target", parent);
            recentTarget.contentType = TMP_InputField.ContentType.IntegerNumber;
            recentTarget.readOnly = false; recentTarget.SetTextWithoutNotify("16");
            recentPresets = Instantiate(mainCandidates, recentTarget.transform);
            recentPresets.name = "RecentDialoguePresets";
            recentPresets.onValueChanged = new TMP_Dropdown.DropdownEvent();
            recentPresets.ClearOptions(); recentPresets.AddOptions(new List<string> { "8", "16", "32" });
            ConfigureComboRow(recentTarget, recentPresets);
            recentPresets.onValueChanged.AddListener(i => { if (i >= 0 && i < 3) recentTarget.text = new[] { "8", "16", "32" }[i]; });
            recentMode.onValueChanged.AddListener(_ => RefreshContextPolicy());
            recentTarget.onValueChanged.AddListener(_ => RefreshContextPolicy());
            recentState = AddState(parent, "RecentDialogueState");
            recentHelp = AddState(parent, "RecentDialogueHelp");
            foreach (var text in new[] { recentState, recentHelp })
            {
                var size = text.GetComponent<LayoutElement>(); size.preferredHeight = -1; size.minHeight = 28;
                text.overflowMode = TextOverflowModes.Overflow;
            }
            if (contextPolicyView?.IsValid == true) ApplyContextPolicyView(contextPolicyView, null, true);
            else RefreshContextPolicy();
        }

        private string ContextDraftSignature() => recentMode == null ? "" : recentMode.value + ":" + recentTarget.text;
        private bool ContextPolicyDirty() => recentMode != null &&
            (!ContextPolicyConfig.TryDraft(recentMode.value, recentTarget.text, out var draft) ||
             (contextPolicyView == null ? draft.mode != "auto" : !draft.Matches(contextPolicyView.applied?.config) || !contextPolicyView.saved_matches_applied));

        private void RefreshContextPolicy()
        {
            if (recentMode == null) return;
            SetOptions(recentMode, SettingsUiLanguage.T("自动（根据有效容量）", "Auto (effective capacity)"), SettingsUiLanguage.T("手动", "Manual"));
            bool manual = recentMode.value == 1;
            recentTarget.transform.parent.gameObject.SetActive(manual);
            recentTarget.interactable = !busy;
            recentPresets.interactable = !busy;
            if (int.TryParse(recentTarget.text, out int n)) recentPresets.SetValueWithoutNotify(n == 8 ? 0 : n == 32 ? 2 : 1);
            recentPresets.RefreshShownValue();
            recentTitle.text = SettingsUiLanguage.T("近期对话保留", "Recent dialogue retention");
            recentHelp.text = SettingsUiLanguage.T(
                "自动按有效容量选择：8K→8条，16K及以上→16条。手动范围1–64。\n一条消息是玩家或NPC的一次真实发言；NPC回复与合法续话分别计数。当前输入属于CurrentTask，不重复计入历史。实际入模可能少于目标，必要时最多加两条衔接消息。更长历史增加输入Token消耗；此设置不改变模型上下文容量。",
                "Auto uses effective capacity: 8K → 8; 16K+ → 16. Manual range: 1–64.\nOne message is one real Player or NPC utterance; NPC replies and valid continuations count separately. Current input belongs to CurrentTask, not history. The model may receive fewer than the target, with up to two extra linking messages. Longer history costs more input tokens; this does not change the model's context capacity.");
            string applied = contextPolicyView?.applied?.config == null ? "—" : PolicyCaption(contextPolicyView.applied.config);
            string state = SettingsUiLanguage.T("当前已应用：", "Currently applied: ") + applied;
            if (!contextPolicyConnected) state += SettingsUiLanguage.T(" · Backend未连接 / 状态未确认", " · Backend disconnected / status unconfirmed");
            if (contextPolicyError != null) state += SettingsUiLanguage.T("\n读取或保存/应用失败：", "\nRead or save/apply failed: ") + contextPolicyError;
            if (contextPolicyView != null && !contextPolicyView.saved_matches_applied && contextPolicyView.saved != null)
                state += SettingsUiLanguage.T("\n已保存：", "\nSaved: ") + PolicyCaption(contextPolicyView.saved.config);
            if (ContextPolicyDirty()) state += SettingsUiLanguage.T("\n存在未保存更改；点击保存并应用。", "\nUnsaved changes; click Save & Apply.");
            if (combinedModelChinese != null) state += SettingsUiLanguage.T("\n模型配置：", "\nModel settings: ") +
                SettingsUiLanguage.T(combinedModelChinese, combinedModelEnglish);
            recentState.text = state;
        }

        private static string PolicyCaption(ContextPolicyConfig config) => config.mode == "auto" ?
            SettingsUiLanguage.T("自动", "Auto") :
            SettingsUiLanguage.T("手动，目标", "Manual, target ") + config.target_messages?.ToString(CultureInfo.InvariantCulture);

        private void HookContextConnection(bool subscribe)
        {
            if (client?.BackendClient == null) return;
            if (subscribe) client.BackendClient.ConnectionStatusChanged += ContextConnectionChanged;
            else client.BackendClient.ConnectionStatusChanged -= ContextConnectionChanged;
        }

        private void ContextConnectionChanged(string state)
        {
            if (state == "CONNECTED") RequestContextReload();
            else { contextPolicyConnected = false; RefreshContextPolicy(); }
        }

        private void RequestContextReload(bool replaceDraft = false)
        {
            if (client == null || !isActiveAndEnabled || !Application.isPlaying || contextReload != null) return;
            contextReload = StartCoroutine(ReloadContextAfterBusy(replaceDraft));
        }

        private IEnumerator ReloadContextAfterBusy(bool replaceDraft)
        {
            // Wait for the existing model operation; reconnect must not overwrite its draft.
            yield return null;
            while (busy) yield return null;
            yield return LoadContextPolicy(replaceDraft || !ContextPolicyDirty());
            contextReload = null;
        }

        private IEnumerator LoadContextPolicy(bool replaceDraft = true)
        {
            if (client == null) yield break;
            string before = ContextDraftSignature();
            ContextPolicyView result = null; string error = null;
            yield return client.GetContextPolicy((value, code) => { result = value; error = code; });
            ApplyContextPolicyView(result, error, replaceDraft && before == ContextDraftSignature());
        }

        private void ApplyContextPolicyView(ContextPolicyView result, string error, bool replaceDraft)
        {
            contextPolicyConnected = error == null && result?.IsValid == true;
            contextPolicyError = error ?? (result?.IsValid == true ? result.error : "CONTEXT_POLICY_RESPONSE_INVALID");
            if (contextPolicyConnected)
            {
                contextPolicyView = result;
                if (replaceDraft) { combinedModelChinese = null; combinedModelEnglish = null; }
                if (replaceDraft && recentMode != null)
                {
                    recentMode.SetValueWithoutNotify(result.applied.config.mode == "manual" ? 1 : 0);
                    recentTarget.SetTextWithoutNotify((result.applied.config.target_messages ?? 16).ToString(CultureInfo.InvariantCulture));
                }
            }
            RefreshContextPolicy();
        }

        private IEnumerator SaveSettingsWithContext()
        {
            bool saveContext = ContextPolicyDirty();
            ContextPolicySaveRequest request = null;
            if (saveContext)
            {
                if (!ContextPolicyConfig.TryDraft(recentMode.value, recentTarget.text, out var config))
                { contextPolicyError = "CONTEXT_POLICY_VALUE_INVALID"; RefreshContextPolicy(); SetStatus("近期消息目标应为1–64的整数；配置未保存。", "Recent message target must be an integer from 1–64; settings not saved."); yield break; }
                if (!contextPolicyConnected || contextPolicyView?.saved == null || contextPolicyView.error != null)
                { SetStatus("短期记忆状态未确认；请重新打开设置或重新连接，草稿保留。", "Context policy status unconfirmed; reopen settings or reconnect. Draft retained."); yield break; }
                request = new ContextPolicySaveRequest { expected_revision = contextPolicyView.saved.revision, config = config };
                contextSaveInProgress = true;
            }
            // Keep the model's existing atomic save-and-activate operation intact.
            // A context-only edit does not stage model activation or index work.
            bool saveModel = !saveContext || HasUnsavedSettings();
            if (saveModel) yield return SaveBothProfiles();
            else SetStatus("模型配置未更改。", "Model configuration unchanged.");
            if (!saveContext) yield break;
            string modelChinese = lastStatusChinese, modelEnglish = lastStatusEnglish;
            ContextPolicyView result = null; string error = null;
            yield return client.SaveContextPolicy(request, (value, code) => { result = value; error = code; });
            ApplyContextPolicySaveResult(request, result, error);
            bool confirmed = error == null && result?.Confirms(request) == true;
            combinedModelChinese = modelChinese; combinedModelEnglish = modelEnglish;
            RefreshContextPolicy();
            SetStatus((!saveModel ? "模型未更改" : modelSaveConfirmed ? "模型已保存" : "模型保存未确认") +
                (confirmed ? "；短期记忆已热应用。" : "；短期记忆未确认，草稿保留。"),
                (!saveModel ? "Model unchanged" : modelSaveConfirmed ? "Model saved" : "Model save unconfirmed") +
                (confirmed ? "; context hot-applied." : "; context unconfirmed; draft retained."));
            contextSaveInProgress = false;
            if (confirmed && completeAfterContextSave) { completeAfterContextSave = false; Complete(); }
        }

        private void ApplyContextPolicySaveResult(ContextPolicySaveRequest request, ContextPolicyView result, string error)
        {
            if (error == null && result?.Confirms(request) == true) ApplyContextPolicyView(result, null, true);
            else
            {
                contextPolicyError = error ?? "CONTEXT_POLICY_APPLY_UNCONFIRMED";
                if (error is "BACKEND_UNAVAILABLE" or "TIMEOUT") contextPolicyConnected = false;
                RefreshContextPolicy();
            }
        }
    }
}
