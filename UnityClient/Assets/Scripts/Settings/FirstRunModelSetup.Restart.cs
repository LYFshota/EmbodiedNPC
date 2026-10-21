using System;

namespace EmbodiedNPC.Settings
{
    public sealed partial class FirstRunModelSetup
    {
        // Only TitleSettingsHost subscribes. Cafe keeps its existing save and lifecycle.
        public event Action RestartToApplyRequested;
        public bool SavedAIRequiresRestart => profilesView?.restart_required == true;
        private bool restartOfferPending, restartExitRequested;

        private void OfferSavedRestart()
        {
            if (!restartOfferPending) return;
            restartOfferPending = false;
            if (!modelSaveConfirmed || profilesView?.restart_required != true || BlocksSettingsNavigation ||
                HasUnsavedSettings() || ContextPolicyDirty() || contextPolicyError != null) return;
            if (RestartToApplyRequested == null)
            {
                SetStatus("AI 配置已保存，将在下次启动游戏时生效。", "AI configuration saved; applies the next time the game starts.");
                return;
            }
            var reason = PortableAIConfigRestart.UnavailableReason;
            if (reason != null) { SetStatus("AI 配置已保存，需要重新启动以生效。\n" + reason,
                "AI configuration saved; restart required.\n" + reason); return; }
            ShowProfileModal("配置已保存\n\n需要重新启动游戏，新的 AI 配置才能生效。",
                "Configuration Saved\n\nRestart the game to apply your new AI settings.",
                new[] { ("立即重启", "Restart Now", (Action)ConfirmSavedRestart),
                    ("稍后", "Later", (Action)(() => SetStatus("AI 配置已保存，将在下次启动游戏时生效。",
                        "AI configuration saved; applies the next time the game starts."))) });
        }

        private void ConfirmSavedRestart()
        {
            // RemoveProfileModal destroys at end of frame; wait for that exact modal to be gone.
            if (restartExitRequested || IsSavingSettings || !modelSaveConfirmed || profilesView?.restart_required != true ||
                HasUnsavedSettings() || ContextPolicyDirty() || contextPolicyError != null) return;
            var reason = PortableAIConfigRestart.UnavailableReason;
            if (RestartToApplyRequested == null || reason != null)
            { SetStatus(reason ?? "统一启动入口不可用。", reason ?? "Unified launcher unavailable."); return; }
            restartExitRequested = true;
            StartCoroutine(RestartAfterConfirmation());
        }

        private System.Collections.IEnumerator RestartAfterConfirmation()
        {
            yield return null;
            if (IsSavingSettings || profileModal != null) { restartExitRequested = false; yield break; }
            SetStatus("正在正常退出，统一入口将重新启动游戏并检查配置。", "Exiting normally; the unified launcher will restart and recheck configuration.");
            RestartToApplyRequested?.Invoke();
        }
    }
}
