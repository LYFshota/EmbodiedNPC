using EmbodiedNPC.Settings;
using UnityEngine;

namespace CafeDemo
{
    public sealed partial class CafeDemoDeveloperPanel
    {
        private StartupProgressDisplay startupProgress;

        private void CreateStartupProgress()
        {
            // No new requests or admission policy: this view mirrors the existing startup owner.
            startupProgress=new StartupProgressDisplay(startupOverlay.transform,backendText.font,
                new Vector2(.17f,.15f),new Vector2(.83f,.85f),RecheckStartup,OpenStartupConfiguration);
            startupProgress.SetCompact(true);
            startupProgress.Begin(new[]{"CafeDemoV2 已加载","读取配置 / Backend HTTP","确认配置、active 与 Provider",
                "建立 Cafe WebSocket","确认 Cafe 启动准入","恢复玩家控制"},
                new[]{"CafeDemoV2 loaded","Configuration / Backend HTTP","Configuration, active model & Provider",
                "Cafe WebSocket connection","Cafe startup admission","Restore player control"});
            startupProgress.Step(0,StartupStepState.Completed);
            startupProgress.Step(1,StartupStepState.Running);
        }
        private void StartupHttpChanged(StartupHttpStage stage)
        {
            if(startupConfigResolved||startupProgress==null)return;
            if(stage==StartupHttpStage.ProfilesReceived)
            {startupProgress.Step(1,StartupStepState.Completed);startupProgress.Step(2,StartupStepState.Running);}
        }
        private void ShowStartupConfig(ModelConfigView config,string error,bool accepted)
        {
            if(startupProgress==null)return;
            startupProgress.ConfigAuxiliary(config);
            if(accepted)
            {
                startupProgress.ClearFailure();
                startupProgress.Step(1,StartupStepState.Completed);startupProgress.Step(2,StartupStepState.Completed);
                startupProgress.Step(3,StartupStepState.Running);
                ShowStartupTransport(startupBackend==null?"OFFLINE":startupBackend.ConnectionStatus);
            }
            else
            {
                var failure=StartupProgressDisplay.ConfigFailure(config,error);
                startupProgress.Fail(startupProgress.States[1]==StartupStepState.Completed?2:1,failure,error??failure.ToString());
                startupProgress.Step(4,StartupStepState.Failed);
            }
        }
        private void ShowStartupTransport(string status)
        {
            if(startupProgress==null||!startupAdmissionHeld||!startupConfigAccepted)return;
            if(status=="CONNECTED")startupProgress.Step(3,StartupStepState.Completed);
            else if(status=="OFFLINE"||status=="CONNECTION_FAILED")
            {startupProgress.Fail(3,StartupUiFailure.WebSocket,status);startupProgress.Step(4,StartupStepState.Failed);}
            else if(status=="CONNECTING"||status=="RETRY_WAIT")
            {
                if(startupProgress.Failure==StartupUiFailure.WebSocket)startupProgress.ClearFailure();
                startupProgress.Step(3,StartupStepState.Running);startupProgress.Step(4,StartupStepState.Waiting);
            }
        }
        private void ShowStartupControlRestored()
        {
            if(startupConfigAccepted&&!startupAdmissionHeld&&!IsOpen&&player!=null&&orbit!=null&&
                !player.IsInputSuspended&&!orbit.IsInputSuspended)
                startupProgress?.Step(5,StartupStepState.Completed);
        }
        private void OpenStartupConfiguration(){OpenStartupSettings();if(IsOpen)PlayerNavigation?.ShowAi(false);}
        private void ShowStartupFailureInForm(bool completingCheck=false)
        {
            if(!IsOpen||!startupAdmissionHeld||startupProgress==null||startupProgress.Failure==StartupUiFailure.None||modelForm==null||modelForm.IsSavingSettings&&!completingCheck)return;
            var text=StartupProgressDisplay.FailureDescription(startupProgress.Failure);
            modelForm.ShowStartupFeedback(startupProgress.Failure==StartupUiFailure.Portable?PortableDataPaths.ErrorDescription(startupProgress.ErrorCode):text.Chinese,text.English);
        }
        private void RecheckStartup()
        {
            OpenStartupConfiguration();
            if(!IsOpen||modelForm==null||modelForm.IsSavingSettings)return;
            startupProgress.ClearFailure();
            startupProgress.Step(1,StartupStepState.Running);startupProgress.Step(2,StartupStepState.Waiting);
            startupProgress.Step(4,StartupStepState.Waiting);
            modelForm.RecheckStartupStatus(); // Same CheckStatus/activation path as the existing check button.
        }
        private void StartupRuntimeChecked(RuntimeStatusView status,string error)
        {
            if(!startupAdmissionHeld||startupProgress==null)return;
            if(error==null&&status!=null)startupProgress.Step(1,StartupStepState.Completed);
            // Failure captions only. Successful admission still comes solely from ConfigurationActivated + WS.
            StartupUiFailure failure;
            if(error!=null&&error.StartsWith("PORTABLE_",System.StringComparison.Ordinal))failure=StartupUiFailure.Portable;
            else if(error!=null||status==null)failure=StartupUiFailure.Http;
            else if(status.restart_required)failure=StartupUiFailure.Restart;
            else if(status.model_config?.configured!=true)failure=StartupUiFailure.Configuration;
            else if(status.active_provider?.available!=true)failure=StartupUiFailure.Provider;
            else return;
            startupProgress.Fail(error!=null?1:2,failure,error??failure.ToString());
            startupProgress.Step(4,StartupStepState.Failed);ShowStartupFailureInForm(true);
        }
    }
}
