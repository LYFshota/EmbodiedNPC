using System;
using System.Collections;
using System.Collections.Generic;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CafeDemo
{
    public enum TitleStartupState { Checking, Unconfigured, PendingApplication, Unavailable, Connecting, Ready, Loading }

    /// <summary>Owns only title navigation and startup admission; no Cafe world exists in this scene.</summary>
    public sealed class TitleScreenController : MonoBehaviour
    {
        [SerializeField] private ModelConfigClient client;
        [SerializeField] private BackendWebSocketClient backend;
        [SerializeField] private TitleSettingsHost settings;
        [SerializeField] private GameObject menu, instructions;
        [SerializeField] private Button enterButton, settingsButton, instructionsButton, quitButton, retryButton, instructionsClose;
        [SerializeField] private TMP_Text stateText, detailText, enterCaption;
        private Coroutine check;
        private readonly Stack<IEnumerator> requests = new();
        private ModelConfigView config;
        private string configError;
        private bool loading, returnAfterActivation;
        private TMP_FontAsset runtimeFont;
        private StartupProgressDisplay startupProgress;
        private GameObject languageRow;
        private Button chineseButton, englishButton;
        private string statusMessage, statusDetail;
        private bool diagnosticsVisible;
        private Button diagnosticsButton, exportButton, exportFolderButton;
        private TMP_Text exportStatus;
        public TitleStartupState State { get; private set; }
        public bool CanEnter => State == TitleStartupState.Ready && !settings.AIRequiresRestart && backend != null && backend.IsConnected;
        public string StatusText => stateText.text;

        private void Awake()
        {
            // Title and the reused form must not bake runtime glyphs into the shared font WIP.
            if (stateText.font != null && stateText.font.sourceFontFile != null)
            {
                runtimeFont = TMP_FontAsset.CreateFontAsset(stateText.font.sourceFontFile);
                runtimeFont.isMultiAtlasTexturesEnabled = true;
                foreach (var text in GetComponentsInChildren<TMP_Text>(true)) text.font = runtimeFont;
            }
            startupProgress=new StartupProgressDisplay(menu.transform,stateText.font,new Vector2(-1.46f,.12f),new Vector2(-.12f,.88f),null,OpenAiConfiguration);
            startupProgress.Root.SetActive(false);
            CreateLanguageShortcut();
            CreateDiagnosticsShortcut();
        }
        private void OnDestroy()
        {
            startupProgress?.Dispose();
            if (runtimeFont == null) return;
            foreach (var texture in runtimeFont.atlasTextures) if (texture != null) Destroy(texture);
            Destroy(runtimeFont);
        }

        private void OnEnable()
        {
            enterButton.onClick.AddListener(EnterCafe);
            settingsButton.onClick.AddListener(OpenSettings);
            instructionsButton.onClick.AddListener(OpenInstructions);
            instructionsClose.onClick.AddListener(CloseInstructions);
            quitButton.onClick.AddListener(Quit);
            retryButton.onClick.AddListener(Retry);
            settings.Activated += ConfigurationActivated;
            settings.Closed += SettingsClosed;
            backend.ConnectionStatusChanged += ConnectionChanged;
            client.StartupCheckProgress += StartupHttpChanged;
            SettingsUiLanguage.Changed += RefreshLanguage;
            RefreshLanguage();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        private void Start() => Retry();
        private void OnDisable()
        {
            CancelCheck();
            enterButton.onClick.RemoveListener(EnterCafe);
            settingsButton.onClick.RemoveListener(OpenSettings);
            instructionsButton.onClick.RemoveListener(OpenInstructions);
            instructionsClose.onClick.RemoveListener(CloseInstructions);
            quitButton.onClick.RemoveListener(Quit);
            retryButton.onClick.RemoveListener(Retry);
            settings.Activated -= ConfigurationActivated;
            settings.Closed -= SettingsClosed;
            backend.ConnectionStatusChanged -= ConnectionChanged;
            client.StartupCheckProgress -= StartupHttpChanged;
            SettingsUiLanguage.Changed -= RefreshLanguage;
        }
        private void Update()
        {
            if (loading || Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            if (settings.IsOpen) settings.TryClose();
            else if (instructions.activeSelf) CloseInstructions();
        }
        private void ConnectionChanged(string status)
        {
            if (!loading && State == TitleStartupState.Ready && status != "CONNECTED")
            {
                SetState(TitleStartupState.Unavailable, "AI 连接已断开", "请重试连接；当前不能进入咖啡馆。");
                startupProgress.Fail(2,StartupUiFailure.WebSocket,status);startupProgress.Step(3,StartupStepState.Failed);
            }
        }
        private void StartupHttpChanged(StartupHttpStage stage)
        {
            if(State!=TitleStartupState.Checking)return;
            if(stage==StartupHttpStage.ProfilesReceived){startupProgress.Step(0,StartupStepState.Completed);startupProgress.Step(1,StartupStepState.Running);}
        }
        public void Retry()
        {
            if (loading) return;
            CancelCheck();
            check = StartCoroutine(Check());
        }
        private void CancelCheck()
        {
            if (check != null) StopCoroutine(check);
            check = null;
            while (requests.Count > 0) (requests.Pop() as IDisposable)?.Dispose();
        }
        private IEnumerator ReadConfig()
        {
            requests.Push(client.GetStartupConfig((value, error) => { config = value; configError = error; }, 6));
            while (requests.Count > 0)
            {
                var next = requests.Peek();
                if (!next.MoveNext()) { (requests.Pop() as IDisposable)?.Dispose(); continue; }
                if (next.Current is IEnumerator nested) requests.Push(nested);
                else yield return next.Current;
            }
        }
        private IEnumerator Check()
        {
            config = null; configError = null;
            startupProgress.Begin(new[]{"读取模型配置 / Backend HTTP","确认配置、active 与 Provider","建立 WebSocket","检查完成"},new[]{"Model configuration / Backend HTTP","Configuration, active model & Provider","WebSocket connection","Checks complete"});
            if (PortableDataPaths.StartupError != null)
            {
                configError = PortableDataPaths.StartupError;
                SetState(TitleStartupState.Unavailable, "便携用户数据不可用", PortableDataPaths.ErrorDescription(configError));
                startupProgress.Fail(0, StartupUiFailure.Portable, configError);
                check = null;
                yield break;
            }
            SetState(TitleStartupState.Checking, "正在检查 AI 连接…", "读取模型配置与当前运行状态。");
            yield return ReadConfig();
            if (configError != null || config == null)
                SetState(TitleStartupState.Unavailable, "暂时无法连接 AI 服务", "请先在外部启动 Backend，再重试；也可打开 AI 配置。\n错误：" + (configError ?? "INVALID_RESPONSE"));
            else if (!config.saved_configured)
                SetState(TitleStartupState.Unconfigured, "需要完成 AI 配置", "点击进入或设置，连接你的主模型。");
            else if (config.profiles.restart_required || config.profiles.active_runtime == null)
                SetState(TitleStartupState.PendingApplication, "配置已保存，等待应用", "请打开 AI 配置，确认重启并应用；保存成功不代表运行模型已生效。");
            else if (!config.configured)
                SetState(TitleStartupState.Unavailable, "当前运行模型尚未就绪", "模型配置检查未通过，请打开 AI 配置检查连接。");
            else
            {
                SetState(TitleStartupState.Connecting, "模型检查通过，正在连接…", ActiveModel() + "\n正在确认 WebSocket 连接。");
                startupProgress.Step(1,StartupStepState.Completed);startupProgress.Step(2,StartupStepState.Running);
                backend.EnsureConnected();
                float deadline = Time.realtimeSinceStartup + 19f;
                while (!backend.IsConnected && Time.realtimeSinceStartup < deadline) yield return null;
                if (backend.IsConnected)
                {
                    SetState(TitleStartupState.Ready, "AI 已就绪", ActiveModel() + "\nHTTP 配置与运行模型检查通过 · WebSocket 已连接\n检查于 " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
                    startupProgress.Step(2,StartupStepState.Completed);startupProgress.Step(3,StartupStepState.Completed);
                    if (returnAfterActivation && (!settings.IsOpen || settings.TryClose())) returnAfterActivation = false;
                }
                else {SetState(TitleStartupState.Unavailable, "模型检查通过，但游戏连接未成功", "WebSocket 未连接；请检查 Backend 后重试。");startupProgress.Fail(2,StartupUiFailure.WebSocket,backend.ConnectionStatus);}
            }
            startupProgress.ConfigAuxiliary(config);
            if(config?.configured!=true)
            {
                var reason=StartupProgressDisplay.ConfigFailure(config,configError);
                startupProgress.Fail(startupProgress.States[0]==StartupStepState.Completed?1:0,reason,configError??reason.ToString());
            }
            check = null;
        }
        private string ActiveModel() => "当前模型：" + (config?.profiles?.active_runtime?.main_model?.model ?? "未知");
        private void SetState(TitleStartupState value, string message, string detail)
        {
            State = value; statusMessage = message; statusDetail = detail;
            enterButton.interactable = value == TitleStartupState.Ready || value == TitleStartupState.Unconfigured;
            retryButton.interactable = value != TitleStartupState.Checking && value != TitleStartupState.Connecting && value != TitleStartupState.Loading;
            RefreshLanguage();
        }
        private void CreateLanguageShortcut()
        {
            languageRow = new GameObject("QuickLanguage", typeof(RectTransform));
            languageRow.transform.SetParent(transform, false);
            var rect = (RectTransform)languageRow.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.one;
            rect.sizeDelta = new Vector2(260, 78); rect.anchoredPosition = new Vector2(-24, -18);
            TMP_Text Label(string name, string caption, Vector2 min, Vector2 max, Transform parent)
            {
                var text = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
                text.transform.SetParent(parent, false); text.font = stateText.font; text.fontSize = 22;
                text.color = new Color(.97f,.92f,.83f); text.alignment = TextAlignmentOptions.Center;
                text.raycastTarget = false; text.text = caption;
                text.rectTransform.anchorMin = min; text.rectTransform.anchorMax = max;
                text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
                return text;
            }
            Label("LanguageLabel", "语言 / Language", new Vector2(0,.58f), Vector2.one, languageRow.transform);
            Button Choice(string name, string caption, float x, bool english)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
                go.transform.SetParent(languageRow.transform, false);
                var r = (RectTransform)go.transform; r.anchorMin = new Vector2(x,0); r.anchorMax = new Vector2(x+.48f,.55f);
                r.offsetMin = r.offsetMax = Vector2.zero;
                var button = go.GetComponent<Button>(); button.targetGraphic = go.GetComponent<Image>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => SettingsUiLanguage.SetEnglish(english));
                Label("Caption", caption, Vector2.zero, Vector2.one, go.transform);
                return button;
            }
            chineseButton = Choice("Chinese", "中文", 0, false);
            englishButton = Choice("English", "English", .52f, true);
        }
        private void CreateDiagnosticsShortcut()
        {
            var row = new GameObject("TitleDiagnostics", typeof(RectTransform)); row.transform.SetParent(transform, false);
            var r = (RectTransform)row.transform; r.anchorMin = r.anchorMax = r.pivot = Vector2.zero;
            r.sizeDelta = new Vector2(600, 46); r.anchoredPosition = new Vector2(28, 24);
            Button Action(string name, int index, Action clicked)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button)); go.transform.SetParent(row.transform, false);
                var rect = (RectTransform)go.transform; rect.anchorMin = new Vector2(index / 3f, 0); rect.anchorMax = new Vector2((index + 1) / 3f, 1);
                rect.offsetMin = new Vector2(0,0); rect.offsetMax = new Vector2(-12,0);
                var b = go.GetComponent<Button>(); b.targetGraphic = go.GetComponent<Image>(); b.image.color = new Color(.25f,.16f,.12f,.95f);
                b.onClick.AddListener(() => clicked());
                var t = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TMP_Text>(); t.transform.SetParent(go.transform, false);
                t.font = stateText.font; t.fontSize = 21; t.color = new Color(.97f,.92f,.83f); t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
                t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one; t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
                return b;
            }
            diagnosticsButton = Action("DiagnosticsDetails",0,()=> { diagnosticsVisible=!diagnosticsVisible; startupProgress.Root.SetActive(diagnosticsVisible); RefreshLanguage(); });
            exportButton = Action("TitleExportDiagnostics",1,()=> { exportStatus.text=DiagnosticsExport.TryExport(); });
            exportFolderButton = Action("OpenDiagnosticsFolder",2,()=>exportStatus.text=DiagnosticsExport.TryOpenFolder());
            exportStatus = new GameObject("ExportStatus",typeof(RectTransform),typeof(TextMeshProUGUI)).GetComponent<TMP_Text>(); exportStatus.transform.SetParent(row.transform,false);
            exportStatus.font=stateText.font; exportStatus.fontSize=18; exportStatus.color=new Color(.97f,.92f,.83f); exportStatus.raycastTarget=false;
            exportStatus.rectTransform.anchorMin=new Vector2(0,1); exportStatus.rectTransform.anchorMax=new Vector2(1,1);
            exportStatus.rectTransform.offsetMin=new Vector2(0,8); exportStatus.rectTransform.offsetMax=new Vector2(0,76);
            exportStatus.textWrappingMode=TextWrappingModes.Normal;
        }
        private void RefreshLanguage()
        {
            string T(string zh, string en) => SettingsUiLanguage.T(zh,en);
            void Caption(Button button, string zh, string en) => button.GetComponentInChildren<TMP_Text>().text = T(zh,en);
            Caption(settingsButton,"设置","Settings"); Caption(instructionsButton,"操作说明","Controls");
            Caption(quitButton,"退出游戏","Quit"); Caption(retryButton,"重新检查 AI","Recheck AI");
            Caption(diagnosticsButton, diagnosticsVisible ? "收起诊断" : "诊断详情", diagnosticsVisible ? "Hide diagnostics" : "Diagnostics");
            Caption(exportButton,"导出日志","Export logs"); Caption(exportFolderButton,"打开目录","Open folder");
            Caption(instructionsClose,"返回主菜单","Back to main menu");
            menu.transform.Find("Subtitle").GetComponent<TMP_Text>().text = T("一个拥有记忆与自主行为的 AI 咖啡馆","An AI cafe with memory and autonomous characters");
            enterCaption.text = State == TitleStartupState.Loading ? T("正在进入…","Entering…") : State == TitleStartupState.Unconfigured ? T("配置 AI，进入咖啡馆","Configure AI to enter") : T("进入咖啡馆","Enter cafe");
            if (statusMessage != null) stateText.text = TranslateStatus(statusMessage);
            if (statusDetail != null) detailText.text = TranslateStatus(statusDetail);
            detailText.gameObject.SetActive(diagnosticsVisible || State is TitleStartupState.Unconfigured or TitleStartupState.Unavailable or TitleStartupState.PendingApplication);
            retryButton.gameObject.SetActive(State != TitleStartupState.Ready && !loading);
            diagnosticsButton.transform.parent.gameObject.SetActive(menu.activeSelf && !loading);
            if (instructions.activeSelf) instructions.GetComponentInChildren<TMP_Text>(true).text = SettingsOperationHelp.Text;
            languageRow.SetActive(menu.activeSelf && !loading);
            foreach (var button in new[]{chineseButton,englishButton})
            {
                bool selected = (button == englishButton) == SettingsUiLanguage.IsEnglish;
                button.image.color = selected ? new Color(.94f,.79f,.45f) : new Color(.25f,.16f,.12f,.95f);
                button.GetComponentInChildren<TMP_Text>().color = selected ? new Color(.20f,.13f,.10f) : new Color(.97f,.92f,.83f);
            }
        }
        private static string TranslateStatus(string text)
        {
            if (!SettingsUiLanguage.IsEnglish) return text;
            if (text.Contains('\n')) return string.Join("\n", Array.ConvertAll(text.Split('\n'), TranslateStatus));
            // Translate presentation only; preserve dynamic model names and error codes.
            var pairs = new (string zh,string en)[]{
                ("AI 连接已断开","AI connection lost"),("请重试连接；当前不能进入咖啡馆。","Retry the connection before entering the cafe."),
                ("便携用户数据不可用","Portable user data unavailable"),("正在检查 AI 连接…","Checking AI connection…"),
                ("读取模型配置与当前运行状态。","Reading model configuration and runtime status."),
                ("暂时无法连接 AI 服务","AI service unavailable"),("请先在外部启动 Backend，再重试；也可打开 AI 配置。","Start Backend and retry, or open AI settings."),("错误：","Error: "),
                ("需要完成 AI 配置","AI setup required"),("点击进入或设置，连接你的主模型。","Choose Enter or Settings to configure your main model."),
                ("配置已保存，等待应用","Configuration saved, pending application"),("请打开 AI 配置，确认重启并应用；保存成功不代表运行模型已生效。","Open AI settings and confirm restart to apply. Saving alone does not activate the running model."),
                ("当前运行模型尚未就绪","Active model is not ready"),("模型配置检查未通过，请打开 AI 配置检查连接。","Model checks failed. Open AI settings to check the connection."),
                ("模型检查通过，正在连接…","Model checks passed, connecting…"),("正在确认 WebSocket 连接。","Checking WebSocket connection."),
                ("AI 已就绪","AI ready"),("HTTP 配置与运行模型检查通过 · WebSocket 已连接","HTTP and active model checks passed · WebSocket connected"),
                ("检查于 ","Checked at "),("当前模型：","Active model: "),("未知","Unknown"),
                ("模型检查通过，但游戏连接未成功","Model checks passed, game connection failed"),("WebSocket 未连接；请检查 Backend 后重试。","WebSocket disconnected. Check Backend and retry."),
                ("正在进入咖啡馆…","Entering cafe…"),("正在关闭标题连接并加载场景。","Closing the Title connection and loading the scene."),
                ("连接关闭未完成","Connection shutdown incomplete"),("请重试；尚未加载咖啡馆。","Retry; the cafe has not loaded."),
                ("场景加载异常","Scene loading failed"),("尚未进入咖啡馆，请查看错误详情。仍可打开设置或重新检查。","The cafe has not loaded. View error details, open settings or retry.")
            };
            // Full strings first, so short status phrases never alter a longer failure message.
            foreach (var pair in pairs) if (text == pair.zh) return pair.en;
            foreach (var pair in pairs) if (text.StartsWith(pair.zh, StringComparison.Ordinal)) return pair.en + text.Substring(pair.zh.Length);
            return text;
        }
        public void OpenSettings()
        {
            if (loading) return;
            CloseInstructions();
            menu.SetActive(false);
            settings.Open(config, configError);
            RefreshLanguage();
        }
        private void OpenAiConfiguration(){OpenSettings();if(settings.IsOpen)settings.Navigation?.ShowAi(false);}
        private void SettingsClosed()
        {
            menu.SetActive(true);
            RefreshLanguage();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(settingsButton.gameObject);
            // A saved pending configuration invalidates an earlier Ready cached by the menu.
            // Reuse the original check; saving is never ConfigurationActivated.
            if (settings.AIRequiresRestart) Retry();
        }
        private void ConfigurationActivated() { returnAfterActivation = true; Retry(); }
        private void OpenInstructions()
        {
            instructions.GetComponentInChildren<TMP_Text>(true).text = SettingsOperationHelp.Text;
            menu.SetActive(false); instructions.SetActive(true);
            RefreshLanguage();
        }
        private void CloseInstructions() { instructions.SetActive(false); if (!settings.IsOpen) menu.SetActive(true); RefreshLanguage(); }
        public void EnterCafe()
        {
            if (State == TitleStartupState.Unconfigured) { OpenSettings(); return; }
            if (!CanEnter || loading || settings.IsOpen || instructions.activeSelf) return;
            loading = true;
            CancelCheck();
            ShowCafeTransition();
            SetState(TitleStartupState.Loading, "正在进入咖啡馆…", "正在关闭标题连接并加载场景。");
            settingsButton.interactable = instructionsButton.interactable = quitButton.interactable = false;
            StartCoroutine(LoadCafe());
        }
        private void ShowCafeTransition()
        {
            startupProgress.SetCompact(true);
            // The menu's existing status already displays the transition; keep its diagnostic panel out of normal loading.
            startupProgress.Root.SetActive(false);
            startupProgress.Begin(new[]{"准备切换 / 关闭 Title 连接","加载 CafeDemoV2","建立 Cafe WebSocket","确认 Cafe 启动准入","恢复玩家控制"},new[]{"Prepare / close Title connection","Load CafeDemoV2","Cafe WebSocket connection","Cafe startup admission","Restore player control"});
        }
        private IEnumerator LoadCafe()
        {
            yield return null; // Let the real transition stage render before beginning scene I/O.
            // Await the scene-owned connection's shutdown before Cafe creates its own client.
            var disconnect = backend.DisconnectAsync();
            while (!disconnect.IsCompleted) yield return null;
            if (disconnect.IsFaulted)
            {
                loading = false;
                settingsButton.interactable = instructionsButton.interactable = quitButton.interactable = true;
                SetState(TitleStartupState.Unavailable, "连接关闭未完成", "请重试；尚未加载咖啡馆。");
                startupProgress.Fail(0,StartupUiFailure.WebSocket,"TITLE_DISCONNECT_FAILED");
                yield break;
            }
            startupProgress.Step(0,StartupStepState.Completed);startupProgress.Step(1,StartupStepState.Running);
            yield return null;
            AsyncOperation operation=null;
            try {operation=SceneManager.LoadSceneAsync("CafeDemoV2", LoadSceneMode.Single);}
            catch(Exception exception){SceneLoadFailed(exception.GetType().Name);}
            if(operation==null){if(loading)SceneLoadFailed("NO_SCENE_OPERATION");yield break;}
            yield return operation;
        }
        private void SceneLoadFailed(string code)
        {
            loading=false;settingsButton.interactable=instructionsButton.interactable=quitButton.interactable=true;
            SetState(TitleStartupState.Unavailable,"场景加载异常","尚未进入咖啡馆，请查看错误详情。仍可打开设置或重新检查。");
            startupProgress.Fail(1,StartupUiFailure.Scene,code);
        }
        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
