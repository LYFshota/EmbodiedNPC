using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EmbodiedNPC.Dialogue;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace EmbodiedNPC.Settings
{
    /// <summary>Shared model configuration form; the Backend remains config and runtime authority.</summary>
    public sealed partial class FirstRunModelSetup : MonoBehaviour
    {
        [SerializeField] private ModelConfigClient client;
        [SerializeField] private DialogueController dialogueController;
        [SerializeField] private GameObject panel;
        [SerializeField] private TMP_Dropdown mainProvider;
        [SerializeField] private TMP_InputField mainBaseUrl;
        [SerializeField] private TMP_InputField mainApiKey;
        [SerializeField] private TMP_InputField mainModel;
        [SerializeField] private TMP_Dropdown mainCandidates;
        [SerializeField] private TMP_InputField temperature;
        [SerializeField] private Toggle thinking;
        [SerializeField] private TMP_InputField maxOutputTokens;
        [SerializeField] private TMP_InputField contextLength;
        [SerializeField] private TMP_InputField mainTimeout;
        [SerializeField] private Toggle embeddingEnabled;
        [SerializeField] private Toggle reuseMainApi;
        [SerializeField] private TMP_Dropdown embeddingProvider;
        [SerializeField] private GameObject embeddingFields;
        [SerializeField] private GameObject embeddingConnectionFields;
        [SerializeField] private TMP_InputField embeddingBaseUrl;
        [SerializeField] private TMP_InputField embeddingApiKey;
        [SerializeField] private TMP_InputField embeddingModel;
        [SerializeField] private TMP_Dropdown embeddingCandidates;
        [SerializeField] private TMP_InputField embeddingTimeout;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button saveButton;
        [FormerlySerializedAs("retryButton")]
        [SerializeField] private Button restartBackendButton;
        [SerializeField] private Button recheckButton;
        [SerializeField] private Button discoverMainButton;
        [SerializeField] private Button testMainButton;
        [SerializeField] private Button discoverEmbeddingButton;
        [SerializeField] private Button testEmbeddingButton;
        [SerializeField] private bool developerMode;

        private int revision;
        private bool busy;
        private bool savedThisSession;
        private TMP_Dropdown contextPresets;
        private static readonly int[] ContextPresetLengths = { 16384, 32768, 65536 };
        private readonly List<int> availableContextLengths = new(ContextPresetLengths);
        private int loadedLegacyContextLength;
        private bool controlsPrepared;
        private string lastStatusChinese;
        private string lastStatusEnglish;
        public event Action ConfigurationActivated;
        // Read-only completion signal for the existing manual runtime check; not another readiness gate.
        public event Action<RuntimeStatusView,string> RuntimeStatusChecked;
        public bool IsSetupVisible => panel != null && panel.activeSelf;

        private void Awake()
        {
            if (developerMode) return;
            dialogueController?.SetSetupReady(false);
            if (panel != null) panel.SetActive(true);
        }

        private void OnEnable()
        {
            EnsureControls();
            HookContextConnection(true);
            if (developerMode) RequestContextReload(true);
            SettingsUiLanguage.Changed += RefreshLanguage;
            saveButton.onClick.AddListener(Save);
            restartBackendButton.onClick.AddListener(RestartBackend);
            recheckButton.onClick.AddListener(Recheck);
            discoverMainButton.onClick.AddListener(DiscoverMain);
            testMainButton.onClick.AddListener(TestMain);
            discoverEmbeddingButton.onClick.AddListener(DiscoverEmbedding);
            testEmbeddingButton.onClick.AddListener(TestEmbedding);
            mainProvider.onValueChanged.AddListener(OnMainProviderChanged);
            mainBaseUrl.onValueChanged.AddListener(OnMainUrlChanged);
            if (contextPresets != null) contextPresets.onValueChanged.AddListener(OnContextPresetChosen);
            if (contextLength != null) contextLength.onValueChanged.AddListener(OnContextLengthChanged);
            embeddingProvider.onValueChanged.AddListener(OnEmbeddingProviderChanged);
            embeddingEnabled.onValueChanged.AddListener(OnEmbeddingEnabledChanged);
            reuseMainApi.onValueChanged.AddListener(OnReuseChanged);
            mainCandidates.onValueChanged.AddListener(OnMainCandidateChosen);
            embeddingCandidates.onValueChanged.AddListener(OnEmbeddingCandidateChosen);
            mainModel.onValueChanged.AddListener(OnMainModelEdited);
            embeddingModel.onValueChanged.AddListener(OnEmbeddingModelEdited);
            RefreshLanguage();
            RefreshEmbeddingFields();
            StartCoroutine(EnableDropdownsAfterStart());
            if (profilesView != null) StartProfilePolling();
        }

        private IEnumerator EnableDropdownsAfterStart()
        {
            dropdownsWarming = true;
            warmingDropdowns = panel.GetComponentsInChildren<TMP_Dropdown>(true);
            warmingInteractable = new bool[warmingDropdowns.Length];
            for (int i = 0; i < warmingDropdowns.Length; i++)
            {
                warmingInteractable[i] = warmingDropdowns[i].interactable;
                warmingDropdowns[i].interactable = false;
            }
            // TMP_Dropdown creates its fade runner in Start, one frame after this
            // model page becomes active. Show() before then leaves its template visible.
            yield return null;
            FinishDropdownWarmup();
        }

        private bool dropdownsWarming;
        private TMP_Dropdown[] warmingDropdowns;
        private bool[] warmingInteractable;

        private void FinishDropdownWarmup()
        {
            if (warmingDropdowns != null)
                for (int i = 0; i < warmingDropdowns.Length; i++)
                    if (warmingDropdowns[i] != null) warmingDropdowns[i].interactable = warmingInteractable[i];
            warmingDropdowns = null; warmingInteractable = null;
            dropdownsWarming = false;
            RefreshCandidateControls();
            if (profilesView != null && !busy) RefreshEmbeddingFields();
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            restartOfferPending = false; restartExitRequested = false;
            contextReload = null;
            contextSaveInProgress = false;
            completeAfterContextSave = false;
            HookContextConnection(false);
            FinishDropdownWarmup();
            busy = false;
            SetProfileBusy(false);
            StopProfilePolling();
            SettingsUiLanguage.Changed -= RefreshLanguage;
            saveButton.onClick.RemoveListener(Save);
            restartBackendButton.onClick.RemoveListener(RestartBackend);
            recheckButton.onClick.RemoveListener(Recheck);
            discoverMainButton.onClick.RemoveListener(DiscoverMain);
            testMainButton.onClick.RemoveListener(TestMain);
            discoverEmbeddingButton.onClick.RemoveListener(DiscoverEmbedding);
            testEmbeddingButton.onClick.RemoveListener(TestEmbedding);
            mainProvider.onValueChanged.RemoveListener(OnMainProviderChanged);
            mainBaseUrl.onValueChanged.RemoveListener(OnMainUrlChanged);
            if (contextPresets != null) contextPresets.onValueChanged.RemoveListener(OnContextPresetChosen);
            if (contextLength != null) contextLength.onValueChanged.RemoveListener(OnContextLengthChanged);
            embeddingProvider.onValueChanged.RemoveListener(OnEmbeddingProviderChanged);
            embeddingEnabled.onValueChanged.RemoveListener(OnEmbeddingEnabledChanged);
            reuseMainApi.onValueChanged.RemoveListener(OnReuseChanged);
            mainCandidates.onValueChanged.RemoveListener(OnMainCandidateChosen);
            embeddingCandidates.onValueChanged.RemoveListener(OnEmbeddingCandidateChosen);
            mainModel.onValueChanged.RemoveListener(OnMainModelEdited);
            embeddingModel.onValueChanged.RemoveListener(OnEmbeddingModelEdited);
        }

        private void Start()
        {
            if (!developerMode) Begin(LoadConfig(true));
        }

        public void ReloadForDeveloper()
        {
            if (developerMode) Begin(LoadConfig(false));
        }

        // Presentation only: the Cafe panel owns the startup HTTP request and retry lifecycle.
        public void ShowStartupConnecting() => SetStatus("正在连接 AI Backend…");
        public void ShowStartupFeedback(string chinese,string english)=>SetStatus(chinese,english);
        public void RecheckStartupStatus()
        {SetStatus("正在重新检查配置与当前运行状态…","Rechecking configuration and active runtime…");Recheck();}

        public void ApplyStartupResult(ModelConfigView value, string error)
        {
            if (value?.profiles != null) ApplyProfiles(value.profiles);
            if (error != null || value == null)
            {
                var message = P0FeedbackText.BackendError(error);
                SetStatus(message.Chinese, message.English);
                return;
            }
            ApplyInitialConfig(value);
            if (value.saved_configured && !value.configured)
            {
                SetStatus("配置已保存，但运行模型未就绪；请应用、重启并检查连接。", "Configuration is saved, but runtime is not ready. Apply, restart and check the connection.");
                return;
            }
            SetStatus(value.configured ? "配置已重新加载。" : "当前没有已保存的模型配置。");
        }

        public void PrepareUnconfiguredDefaults()
        {
            if (!developerMode || mainProvider == null || contextLength == null || mainProvider.value != 0)
                return;
            if (string.IsNullOrWhiteSpace(contextLength.text) || contextLength.text == "0")
                contextLength.text = "32768";
        }

        private void Begin(IEnumerator operation)
        {
            if (busy || restartExitRequested || client == null) return;
            StartCoroutine(Run(operation));
        }

        private IEnumerator Run(IEnumerator operation)
        {
            busy = true;
            try
            {
                // Do not snapshot the temporary warmup lock as the control's normal state.
                while (dropdownsWarming) yield return null;
                SetProfileBusy(true);
                yield return operation;
            }
            finally { busy = false; contextSaveInProgress = false; SetProfileBusy(false); RefreshContextPolicy(); RefreshBriefAiState(); }
            OfferSavedRestart();
        }

        private string MainType() => profilesView != null ? CurrentPreset(false).protocol : MainProviderType(mainProvider.value, mainBaseUrl?.text);
        public static string MainProviderType(int source, string baseUrl)
        {
            if (source == 1) return "ollama";
            if (Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri uri) &&
                uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
                (uri.AbsolutePath == "/" || uri.AbsolutePath == string.Empty))
                return "deepseek_official_responses";
            return "openai_compatible";
        }
        private static string EmbeddingType(int index) => index == 1 ? "ollama" : "openai_compatible";

        private void EnsureControls()
        {
            if (controlsPrepared || mainProvider == null) return;
            controlsPrepared = true;
            int old = mainProvider.value;
            mainProvider.ClearOptions();
            mainProvider.AddOptions(new List<string> { "云端", "本地" });
            mainProvider.SetValueWithoutNotify(old == 1 ? 1 : 0);
            embeddingProvider.ClearOptions();
            embeddingProvider.AddOptions(new List<string> { "云端", "本地" });
            // Keep editable values and their choices in the same horizontal row.
            PlaceChoicesBesideInput(mainCandidates, mainModel);
            PlaceChoicesBesideInput(embeddingCandidates, embeddingModel);
            if (contextLength != null && mainCandidates != null)
            {
                contextLength.readOnly = false;
                if (string.IsNullOrWhiteSpace(contextLength.text) || contextLength.text == "0")
                    contextLength.SetTextWithoutNotify("32768");
                contextPresets = Instantiate(mainCandidates, contextLength.transform);
                contextPresets.name = "ContextPresets";
                contextPresets.ClearOptions();
                RefreshContextPreset();
                ConfigureComboRow(contextLength, contextPresets);
                // Model candidates start disabled; fixed context presets are immediately available.
                contextPresets.interactable = true;
                contextPresets.gameObject.SetActive(true);
            }
            // Present embedding as Enabled → Cloud/Local → optional reuse → connection fields.
            if (embeddingFields != null && reuseMainApi != null)
            {
                Transform reuseRow = reuseMainApi.transform.parent;
                reuseRow.SetParent(embeddingFields.transform, false);
                reuseRow.SetSiblingIndex(1);
            }
        }

        private static void PlaceChoicesBesideInput(TMP_Dropdown choices, TMP_InputField input)
        {
            if (choices == null || input == null) return;
            Transform oldRow = choices.transform.parent;
            choices.transform.SetParent(input.transform, false);
            oldRow.gameObject.SetActive(false);
            ConfigureComboRow(input, choices);
            choices.gameObject.SetActive(true);
            choices.interactable = false;
        }

        private static void ConfigureComboRow(TMP_InputField input, TMP_Dropdown choices)
        {
            RectTransform button = choices.GetComponent<RectTransform>();
            button.anchorMin = new Vector2(1, 0);
            button.anchorMax = Vector2.one;
            button.pivot = new Vector2(1, .5f);
            button.offsetMin = new Vector2(-42, 0);
            button.offsetMax = Vector2.zero;
            bool capacity = choices.name is "ContextPresets" or "RecentDialoguePresets";
            float width = choices.name == "ContextPresets" ? 204 : capacity ? 116 : 42;
            button.offsetMin = new Vector2(-width, 0);
            if (input.textViewport != null)
            {
                Vector2 offset = input.textViewport.offsetMax;
                input.textViewport.offsetMax = new Vector2(-width - 8, offset.y);
            }
            if (choices.template != null)
            {
                RectTransform popup = choices.template;
                popup.anchorMin = new Vector2(1, 0);
                popup.anchorMax = new Vector2(1, 0);
                popup.pivot = new Vector2(1, 1);
                popup.anchoredPosition = Vector2.zero;
                SettingsPanelTheme.SizeDropdown(choices);
            }
            if (choices.itemText != null)
            {
                choices.itemText.textWrappingMode = TextWrappingModes.NoWrap;
                choices.itemText.overflowMode = TextOverflowModes.Ellipsis;
                choices.itemText.alignment = TextAlignmentOptions.MidlineLeft;
            }
            Transform arrow = choices.transform.Find("Arrow");
            if (arrow != null) arrow.gameObject.SetActive(false);
            Transform externalChevron = choices.transform.Find("SettingsChevron");
            if (externalChevron == null)
            {
                TMP_Text label;
                if (choices.captionText != null) label = Instantiate(choices.captionText, choices.transform);
                else { var go = new GameObject("SettingsChevron", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(choices.transform, false); label = go.GetComponent<TMP_Text>(); }
                label.name = "SettingsChevron";
                label.text = "v"; label.raycastTarget = false;
                var r = label.rectTransform; r.anchorMin = new Vector2(1, 0); r.anchorMax = Vector2.one;
                r.offsetMin = new Vector2(-28, 0); r.offsetMax = Vector2.zero;
                label.alignment = TextAlignmentOptions.Center; externalChevron = label.transform;
            }
            if (externalChevron != null) externalChevron.gameObject.SetActive(true);
            if (choices.captionText != null)
            {
                choices.captionText.enabled = capacity;
                choices.captionText.alignment = TextAlignmentOptions.MidlineLeft;
                RectTransform caption = choices.captionText.rectTransform;
                caption.anchorMin = Vector2.zero;
                caption.anchorMax = Vector2.one;
                caption.offsetMin = new Vector2(8, 0);
                caption.offsetMax = new Vector2(-30, 0);
            }
        }

        private void OnContextPresetChosen(int index)
        {
            if (index < 0 || index >= availableContextLengths.Count) return;
            contextLength.text = availableContextLengths[index].ToString(CultureInfo.InvariantCulture);
            RefreshContextPreset();
        }

        private void OnContextLengthChanged(string _)
        {
            RefreshContextPreset();
            RefreshContextCapacityState();
        }

        private void RefreshContextPreset()
        {
            if (contextPresets == null) return;
            int.TryParse(contextLength.text, out int value);
            availableContextLengths.Clear();
            availableContextLengths.AddRange(ContextPresetLengths);
            if (TryContextPreset(contextLength.text, out _) && !availableContextLengths.Contains(value))
                availableContextLengths.Add(value);
            contextPresets.options = availableContextLengths.ConvertAll(v => new TMP_Dropdown.OptionData(
                v == 32768 ? SettingsUiLanguage.T("32K（推荐）", "32K (Recommended)") :
                v % 1024 == 0 ? (v / 1024) + "K" : SettingsUiLanguage.T("当前自定义：", "Current custom: ") + v));
            int index = availableContextLengths.IndexOf(value);
            EnsureDropdownPlaceholder(contextPresets, SettingsUiLanguage.T("旧配置", "Legacy"));
            contextPresets.SetValueWithoutNotify(index);
            contextPresets.RefreshShownValue();
        }

        private static bool TryContextPreset(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value > 0 && value <= 1_000_000;

        // The default is a budget, not a capability claim. Allow each model's explicit capacity.
        private bool TryMainContext(string text, out int value) => TryContextPreset(text, out value);

        private void RefreshLanguage()
        {
            if (mainProvider == null) return;
            bool en = SettingsUiLanguage.IsEnglish;
            SetOptions(mainProvider, en ? "Cloud" : "云端", en ? "Local" : "本地");
            SetOptions(embeddingProvider, en ? "Cloud" : "云端", en ? "Local" : "本地");
            SetRow(mainProvider, "主模型来源", "Main model source");
            SetRow(mainBaseUrl, mainProvider.value == 1 ? "Ollama 地址" : "API 地址",
                mainProvider.value == 1 ? "Ollama URL" : "API URL");
            SetRow(mainApiKey, "API 密钥", "API key");
            SetRow(mainModel, "主模型", "Main model");
            SetCandidatePlaceholder(mainCandidates);
            SetRow(temperature, "温度", "Temperature");
            SetRow(thinking, "思考模式", "Thinking");
            SetRow(maxOutputTokens, "最大输出 Token", "Max output tokens");
            SetRow(contextLength, "主模型上下文长度", "Main Model Context Length");
            RefreshContextPreset();
            SetRow(mainTimeout, "主模型超时（秒）", "Main timeout (seconds)");
            SetRow(embeddingEnabled, "启用 Embedding", "Enable embedding");
            SetRow(reuseMainApi, "复用主模型 API", "Reuse main model API");
            SetRow(embeddingProvider, "Embedding 来源", "Embedding source");
            SetRow(embeddingBaseUrl, embeddingProvider.value == 1 ? "Ollama 地址" : "Embedding API 地址",
                embeddingProvider.value == 1 ? "Ollama URL" : "Embedding API URL");
            SetRow(embeddingApiKey, "Embedding API 密钥", "Embedding API key");
            SetRow(embeddingModel, "Embedding 模型", "Embedding model");
            SetCandidatePlaceholder(embeddingCandidates);
            SetRow(embeddingTimeout, "Embedding 超时（秒）", "Embedding timeout (seconds)");
            if (panel == null || panel.GetComponentInParent<SettingsPanelTheme>(true) == null)
            {
                SetRow(discoverMainButton, "获取主模型", "Discover main models");
                SetRow(testMainButton, "测试主模型", "Test main model");
                SetRow(discoverEmbeddingButton, "获取 Embedding 模型", "Discover embedding models");
                SetRow(testEmbeddingButton, "测试 Embedding", "Test embedding");
            }
            SetButton(discoverMainButton, "获取模型", "Get models");
            SetButton(testMainButton, "测试连接", "Test connection");
            SetButton(discoverEmbeddingButton, "获取模型", "Get models");
            SetButton(testEmbeddingButton, "测试连接", "Test connection");
            SetButton(saveButton, "保存配置", "Save configuration");
            SetButton(recheckButton, "检查配置", "Check configuration");
            SetButton(restartBackendButton, "重启后端", "Restart backend");
            SetPlaceholder(mainBaseUrl, "输入 API 地址", "Enter API URL");
            SetPlaceholder(mainApiKey, "输入 API 密钥", "Enter API key");
            SetPlaceholder(mainModel, "输入模型 ID，或从候选模型选择", "Enter model ID or choose a candidate");
            SetPlaceholder(embeddingBaseUrl, "输入 Embedding API 地址", "Enter embedding API URL");
            SetPlaceholder(embeddingApiKey, "输入 Embedding API 密钥", "Enter embedding API key");
            SetPlaceholder(embeddingModel, "输入 Embedding 模型 ID", "Enter embedding model ID");
            if (panel != null)
            {
                SettingsUiLanguage.StyleCoffeeForm(panel);
                foreach (Toggle toggle in panel.GetComponentsInChildren<Toggle>(true))
                    SettingsUiLanguage.StyleToggle(toggle);
                TMP_Text title = panel.transform.Find("FirstRunSetupPanel/Title")?.GetComponent<TMP_Text>();
                if (title == null) title = panel.transform.Find("Title")?.GetComponent<TMP_Text>();
                if (title == null) title = panel.transform.parent?.Find("Title")?.GetComponent<TMP_Text>();
                if (title != null) title.text = SettingsUiLanguage.T(
                    developerMode ? "模型 / API 配置" : "EmbodiedNPC · 首次模型配置",
                    developerMode ? "Model / API Configuration" : "EmbodiedNPC · First-run Model Setup");
                foreach (TMP_Text label in panel.GetComponentsInChildren<TMP_Text>(true))
                    if (label.gameObject.name == "Main LLM") label.text = SettingsUiLanguage.T(
                        "主模型", "Main model");
            }
            if (lastStatusChinese != null && statusText != null)
                statusText.text = SettingsUiLanguage.T(lastStatusChinese, lastStatusEnglish ?? lastStatusChinese);
            RefreshProfileLanguage();
            if(configurationContent!=null)SetSettingsPage(SettingsPage);
        }

        private static void SetOptions(TMP_Dropdown dropdown, params string[] options)
        {
            if (dropdown == null) return;
            int index = dropdown.value;
            dropdown.ClearOptions();
            dropdown.AddOptions(new List<string>(options));
            dropdown.SetValueWithoutNotify(Mathf.Clamp(index, 0, options.Length - 1));
            dropdown.RefreshShownValue();
        }

        private static void SetRow(Component control, string chinese, string english)
        {
            TMP_Text label = control?.transform.parent.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null) label.text = SettingsUiLanguage.T(chinese, english);
        }

        private static void SetButton(Button button, string chinese, string english)
        {
            TMP_Text label = button?.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = SettingsUiLanguage.T(chinese, english);
        }

        private static void SetPlaceholder(TMP_InputField input, string chinese, string english)
        {
            if (input?.placeholder is TMP_Text label) label.text = SettingsUiLanguage.T(chinese, english);
        }

        private static void SetCandidatePlaceholder(TMP_Dropdown dropdown)
        {
            if (dropdown?.captionText != null) dropdown.captionText.enabled = false;
        }

        private void OnMainProviderChanged(int ignored)
        {
            if (profilesView != null) { RequestSourceSwitch(false, ignored); return; }
            ClearCandidates(mainCandidates);
            if (mainProvider.value == 0 && contextLength.text == "0")
                contextLength.text = "32768";
            if (reuseMainApi.isOn && MainType() != "deepseek_official_responses")
                embeddingProvider.SetValueWithoutNotify(mainProvider.value);
            if (MainType() == "deepseek_official_responses" && reuseMainApi.isOn)
                reuseMainApi.isOn = false;
            RefreshEmbeddingFields();
            RefreshLanguage();
        }

        private void OnMainUrlChanged(string ignored) => RefreshEmbeddingFields();

        private void OnEmbeddingProviderChanged(int ignored)
        {
            if (profilesView != null) { RequestSourceSwitch(true, ignored); return; }
            ClearCandidates(embeddingCandidates);
            RefreshEmbeddingFields();
            RefreshLanguage();
        }
        private void OnEmbeddingEnabledChanged(bool ignored) => RefreshEmbeddingFields();
        private void OnReuseChanged(bool ignored) => RefreshEmbeddingFields();

        private void RefreshEmbeddingFields()
        {
            if (profilesView != null)
            {
                if (embeddingFields != null) embeddingFields.SetActive(embeddingEnabled.isOn);
                if (embeddingConnectionFields != null) embeddingConnectionFields.SetActive(true);
                reuseMainApi.SetIsOnWithoutNotify(false);
                reuseMainApi.transform.parent.gameObject.SetActive(false);
                embeddingProvider.interactable = !busy;
                mainApiKey.transform.parent.gameObject.SetActive(mainProvider.value == 0);
                embeddingApiKey.transform.parent.gameObject.SetActive(embeddingProvider.value == 0);
                RefreshBuiltinFields();
                return;
            }
            bool enabled = embeddingEnabled.isOn;
            bool canReuse = MainType() != "deepseek_official_responses" &&
                            embeddingProvider.value == mainProvider.value;
            reuseMainApi.interactable = enabled && canReuse;
            if (!canReuse && reuseMainApi.isOn) reuseMainApi.SetIsOnWithoutNotify(false);
            if (embeddingFields != null) embeddingFields.SetActive(enabled);
            bool reuse = enabled && reuseMainApi.isOn;
            embeddingProvider.interactable = enabled && !reuse;
            if (embeddingConnectionFields != null) embeddingConnectionFields.SetActive(enabled && !reuse);
            if (mainApiKey != null) mainApiKey.transform.parent.gameObject.SetActive(mainProvider.value == 0);
            if (embeddingApiKey != null) embeddingApiKey.transform.parent.gameObject.SetActive(enabled && !reuse && embeddingProvider.value == 0);
        }

        public void ApplyInitialConfig(ModelConfigView value)
        {
            if (value == null) return;
            revision = value.revision;
            if (developerMode)
            {
                if (profilesView == null && value.configured) FillForm(value);
                return;
            }
            if (value.configured)
            {
                Complete();
                return;
            }
            savedThisSession = false;
            if (panel != null) panel.SetActive(true);
            dialogueController?.SetSetupReady(false);
            if (mainProvider != null && mainProvider.value == 0 && contextLength != null)
                contextLength.text = "32768";
            SetStatus("请先配置主模型与 Embedding，再保存配置。");
            RefreshEmbeddingFields();
        }

        private void Complete()
        {
            if (contextSaveInProgress) { completeAfterContextSave = true; return; }
            if (panel != null) panel.SetActive(false);
            dialogueController?.SetSetupReady(true);
        }

        private IEnumerator LoadConfig(bool initial)
        {
            yield return LoadProfiles(initial);
            yield return LoadContextPolicy();
        }

        private void FillForm(ModelConfigView value)
        {
            MainModelConfig main = value.main_model;
            EmbeddingConfig embed = value.embedding;
            if (main == null || embed == null) return;
            loadedLegacyContextLength = main.context_length > 0 ? main.context_length : 8192;
            mainProvider.SetValueWithoutNotify(main.provider_type == "ollama" ? 1 : 0);
            mainBaseUrl.text = main.base_url ?? string.Empty;
            mainApiKey.text = main.api_key ?? string.Empty;
            mainModel.text = main.model ?? string.Empty;
            temperature.text = main.temperature.ToString(CultureInfo.InvariantCulture);
            thinking.isOn = main.thinking;
            maxOutputTokens.text = main.max_output_tokens.ToString(CultureInfo.InvariantCulture);
            contextLength.text = loadedLegacyContextLength.ToString(CultureInfo.InvariantCulture);
            mainTimeout.text = main.timeout_seconds.ToString(CultureInfo.InvariantCulture);
            if (totalConcurrency != null)
            {
                bool local = main.provider_type == "ollama" || main.provider_type == "mock";
                int total = main.model_total_concurrency > 0 ? main.model_total_concurrency : local ? 1 : 6;
                totalConcurrency.text = total.ToString();
                foregroundConcurrency.text = (main.model_foreground_concurrency > 0 ? main.model_foreground_concurrency : total).ToString();
                backgroundConcurrency.text = (main.model_background_concurrency > 0 ? main.model_background_concurrency : Math.Min(total, local ? 1 : 2)).ToString();
            }
            embeddingEnabled.SetIsOnWithoutNotify(embed.enabled);
            reuseMainApi.SetIsOnWithoutNotify(embed.reuse_main_api);
            embeddingProvider.SetValueWithoutNotify(embed.provider_type == "ollama" ? 1 : 0);
            embeddingBaseUrl.text = embed.base_url ?? string.Empty;
            embeddingApiKey.text = embed.api_key ?? string.Empty;
            embeddingModel.text = embed.model ?? string.Empty;
            embeddingTimeout.text = embed.timeout_seconds.ToString(CultureInfo.InvariantCulture);
            RefreshEmbeddingFields();
        }

        private void Recheck() => Begin(defaultsRestored ? LoadProfiles(false) : CheckStatus(true));

        private void RestartBackend() => Begin(RestartLocalBackend());

        private IEnumerator RestartLocalBackend()
        {
            if (!CanTargetLocalBackend(client?.BackendClient?.HttpBaseUrl))
            {
                SetStatus("当前连接不属于本地 8000 管理目标，请手动重启对应 Backend。",
                    "This connection is not the local managed endpoint. Restart its Backend manually.");
                yield break;
            }
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                SetStatus("本地 Backend 重启仅支持 Windows Unity 编辑器。",
                    "Local Backend restart is available in the Windows Unity Editor only.");
                yield break;
            }
            string projectRoot = Directory.GetParent(Application.dataPath)?.Parent?.FullName;
            string backend = projectRoot == null ? null : Path.Combine(projectRoot, "AIBackend");
            string script = backend == null ? null : Path.Combine(backend, "restart_local_backend.ps1");
            if (script == null || !File.Exists(script))
            {
                SetStatus("找不到本地 Backend 重启脚本。", "Local Backend restart script was not found.");
                yield break;
            }

            SetStatus("正在重启 AI Backend…", "Restarting AI Backend…");
            System.Diagnostics.Process process;
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script +
                        "\" -BackendDir \"" + backend + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                process = System.Diagnostics.Process.Start(info);
            }
            catch (Exception)
            {
                SetStatus("无法启动 Backend 重启工具。", "Could not launch the Backend restart tool.");
                yield break;
            }
            if (process == null)
            {
                SetStatus("无法启动 Backend 重启工具。", "Could not launch the Backend restart tool.");
                yield break;
            }
            using (process)
            {
                while (!process.HasExited) yield return null;
                if (process.ExitCode != 0)
                {
                    SetStatus("重启失败，请检查 Backend 日志或 8000 端口。",
                        "Restart failed. Check the Backend logs or port 8000.");
                    yield break;
                }
            }
            yield return CheckStatus(false);
        }

        private IEnumerator CheckStatus(bool closeWhenReady)
        {
            if (developerMode && profilesView == null)
            {
                yield return LoadProfiles(false);
                if (profilesView == null) {RuntimeStatusChecked?.Invoke(null,"MODEL_CONFIG_UNAVAILABLE");yield break;}
            }
            RuntimeStatusView status = null;
            string error = null;
            long httpCode = 0;
            yield return client.GetRuntimeStatus((value, code) => { status = value; error = code; }, code => httpCode = code);
            if (error != null || status == null)
            {
                var message = P0FeedbackText.BackendError(error, httpCode);
                SetStatus(message.Chinese, message.English);
                RuntimeStatusChecked?.Invoke(null,error??"INVALID_RUNTIME_RESPONSE");
                yield break;
            }
            ApplyRuntimeStatus(status, closeWhenReady);
            RuntimeStatusChecked?.Invoke(status,null);
            if (profilesView != null)
                yield return client.GetProfiles((value, code) => { if (code == null && value != null) { profilesView = value; revision = value.revision; RefreshProfileStates(); } });
        }

        public void ApplyRuntimeStatus(RuntimeStatusView status, bool closeWhenReady)
        {
            if (status == null) return;
            if (status.restart_required)
            {
                SetStatus("已选定的配置待重启生效。", "Selected settings require a Backend restart.");
                return;
            }
            if (developerMode)
            {
                if (status.model_config != null && status.model_config.configured &&
                    status.active_provider != null && status.active_provider.available)
                {
                    SetStatus("配置已生效。");
                    ConfigurationActivated?.Invoke();
                }
                else SetStatus("配置尚未可用，请检查模型连接并重新检查。");
                return;
            }
            if (status.active_provider == null || !status.active_provider.available)
            {
                SetStatus("配置尚未可用，请检查模型连接并重新检查。");
                return;
            }
            SetStatus("配置已保存并已生效。");
            if (closeWhenReady && savedThisSession) Complete();
        }

        private bool RequireDeveloperProfiles()
        {
            if (!developerMode || profilesView != null) return true;
            SetStatus("请先重新加载 v2 模型配置，当前不能保存或测试。", "Reload v2 model settings before saving or testing.");
            return false;
        }

        private void Save()
        {
            if (RequireDeveloperProfiles()) Begin(profilesView != null ? SaveSettingsWithContext() : SaveCurrent());
        }

        private IEnumerator SaveCurrent()
        {
            if (!TryBuildMain(out MainModelConfig main) || !TryBuildEmbedding(main, out EmbeddingConfig embed))
                yield break;
            ModelConfigView result = null;
            string error = null;
            yield return client.SaveConfig(new ModelConfigSaveRequest
            {
                expected_revision = revision,
                config = new ModelConfigDocument { main_model = main, embedding = embed },
            }, (value, code) => { result = value; error = code; });
            if (error == "MODEL_CONFIG_REVISION_CONFLICT")
            {
                SetStatus("配置已发生变化，请重新打开模型页后再保存。");
                yield break;
            }
            if (error != null || result == null)
            {
                SetStatus(error == "BACKEND_UNAVAILABLE" ? "无法连接 AI Backend，请重试。" :
                    "保存失败，请检查配置后重试。");
                yield break;
            }
            revision = result.revision;
            savedThisSession = true;
            yield return CheckStatus(false);
        }

        private bool TryBuildMain(out MainModelConfig main)
        {
            main = null;
            string model = SelectedModel(mainModel, mainCandidates);
            if (string.IsNullOrWhiteSpace(mainBaseUrl.text)) { SetStatus("请填写主模型 API 地址。", "Enter the main model URL."); return false; }
            if (MainType() == "deepseek_official_responses" && string.IsNullOrWhiteSpace(mainApiKey.text))
            { SetStatus("DeepSeek 需要 API Key。", "DeepSeek requires an API key."); return false; }
            if (string.IsNullOrWhiteSpace(model)) { SetStatus("请选择或输入主模型。", "Select or enter a main model."); return false; }
            if (!TryFloat(temperature.text, 0, 2, out float temp)) { SetStatus("Temperature 应在 0 到 2 之间。", "Temperature must be between 0 and 2."); return false; }
            if (!TryInt(maxOutputTokens.text, 1, 32768, out int tokens)) { SetStatus("最大输出 Token 应在 1 到 32768 之间。", "Max output tokens must be between 1 and 32768."); return false; }
            if (!TryMainContext(contextLength.text, out int context)) { SetStatus("请选择模型实际支持的容量，或输入 1–1000000 的自定义值。", "Choose the model's supported capacity, or enter a custom value from 1 to 1000000."); return false; }
            if (!TryFloat(mainTimeout.text, .01f, 600, out float timeout)) { SetStatus("主模型超时应在 0.01 到 600 秒之间。", "Main timeout must be between 0.01 and 600 seconds."); return false; }
            if (!TryConcurrency(out int total, out int foreground, out int background))
            { SetStatus("并发须为 1–32，前后台均不能超过总并发。", "Concurrency must be 1–32; class limits cannot exceed total."); return false; }
            main = new MainModelConfig
            {
                provider_type = MainType(), base_url = mainBaseUrl.text.Trim(),
                api_key = mainProvider.value == 1 ? null : mainApiKey.text.Trim(), model = model, temperature = temp,
                thinking = thinking.isOn, max_output_tokens = tokens, context_length = context,
                timeout_seconds = timeout,
                model_total_concurrency = total, model_foreground_concurrency = foreground, model_background_concurrency = background,
            };
            return true;
        }

        private bool TryBuildEmbedding(MainModelConfig main, out EmbeddingConfig embed)
        {
            embed = null;
            float timeout = 30;
            if (embeddingEnabled.isOn && !TryFloat(embeddingTimeout.text, .01f, 600, out timeout))
            {
                SetStatus("Embedding 超时参数无效。");
                return false;
            }
            bool enabled = embeddingEnabled.isOn;
            bool reuse = enabled && reuseMainApi.isOn && main.provider_type != "deepseek_official_responses" &&
                         EmbeddingType(embeddingProvider.value) == main.provider_type;
            string model = SelectedModel(embeddingModel, embeddingCandidates);
            if (enabled && (string.IsNullOrWhiteSpace(model) ||
                            (!reuse && string.IsNullOrWhiteSpace(embeddingBaseUrl.text))))
            {
                SetStatus("Embedding 已启用：请配置模型和地址，或关闭 Embedding。",
                    "Embedding is enabled: configure its model and URL, or disable it.");
                return false;
            }
            embed = new EmbeddingConfig
            {
                enabled = enabled, reuse_main_api = reuse,
                provider_type = reuse ? main.provider_type : EmbeddingType(embeddingProvider.value),
                base_url = reuse || !enabled ? null : embeddingBaseUrl.text.Trim(),
                api_key = reuse || !enabled || embeddingProvider.value == 1 ? null : embeddingApiKey.text,
                model = enabled ? model : null, timeout_seconds = timeout,
            };
            return true;
        }

        private static bool TryFloat(string text, float min, float max, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && value >= min && value <= max;
        private static bool TryInt(string text, int min, int max, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= min && value <= max;

        private void DiscoverMain() => Begin(Discover(false));
        private void DiscoverEmbedding() => Begin(Discover(true));

        private IEnumerator Discover(bool forEmbedding)
        {
            if (!RequireDeveloperProfiles()) yield break;
            if (profilesView != null) { yield return ProbeCurrentProfile(forEmbedding, "discover"); yield break; }
            bool reuse = forEmbedding && reuseMainApi.isOn;
            string provider = forEmbedding ? (reuse ? MainType() :
                EmbeddingType(embeddingProvider.value)) : MainType();
            string url = forEmbedding && !reuse ? embeddingBaseUrl.text : mainBaseUrl.text;
            string key = forEmbedding && !reuse ? embeddingApiKey.text : mainApiKey.text;
            string timeoutText = forEmbedding ? embeddingTimeout.text : mainTimeout.text;
            if (string.IsNullOrWhiteSpace(url) || !TryFloat(timeoutText, .01f, 120, out float timeout))
            {
                SetStatus("请先填写有效的 API 地址和超时。");
                yield break;
            }
            DiscoverModelsResponse result = null;
            string error = null;
            yield return client.DiscoverModels(new DiscoverModelsRequest
            {
                provider_type = provider, base_url = url.Trim(), api_key = key?.Trim(),
                timeout_seconds = timeout,
            }, (value, code) => { result = value; error = code; });
            ApplyDiscoveryResult(forEmbedding, result, error);
        }

        public void ApplyDiscoveryResult(bool forEmbedding, DiscoverModelsResponse result, string error)
        {
            if (error != null || result == null || !result.success || result.models == null ||
                result.models.Length == 0)
            {
                SetStatus("无法获取模型列表，请手动输入模型名称。");
                return;
            }
            ApplyDiscoveredModels(forEmbedding ? embeddingCandidates : mainCandidates, result.models);
            RefreshCandidateControls();
            SetStatus("模型列表已更新；可选择或输入模型。", "Model list updated. Choose or enter a model.");
        }

        public static void ApplyDiscoveredModels(TMP_Dropdown dropdown, string[] models)
        {
            if (dropdown == null || models == null) return;
            EnsureDropdownPlaceholder(dropdown, "");
            var options = new List<TMP_Dropdown.OptionData>();
            foreach (string model in models)
                if (!string.IsNullOrWhiteSpace(model)) options.Add(new TMP_Dropdown.OptionData(model));
            dropdown.ClearOptions();
            dropdown.AddOptions(options);
            dropdown.SetValueWithoutNotify(-1);
            dropdown.RefreshShownValue();
            dropdown.gameObject.SetActive(true);
            dropdown.interactable = options.Count > 0;
        }

        private void OnMainCandidateChosen(int index)
        {
            if (index >= 0 && index < mainCandidates.options.Count)
            {
                mainModel.text = mainCandidates.options[index].text;
                mainCandidates.SetValueWithoutNotify(-1);
                mainCandidates.RefreshShownValue();
            }
        }
        private void OnEmbeddingCandidateChosen(int index)
        {
            if (index >= 0 && index < embeddingCandidates.options.Count)
            {
                embeddingModel.text = embeddingCandidates.options[index].text;
                embeddingCandidates.SetValueWithoutNotify(-1);
                embeddingCandidates.RefreshShownValue();
            }
        }

        private void OnMainModelEdited(string value) => ClearCandidateOnManualEdit(mainCandidates, value);
        private void OnEmbeddingModelEdited(string value) => ClearCandidateOnManualEdit(embeddingCandidates, value);

        private static void ClearCandidateOnManualEdit(TMP_Dropdown candidates, string value)
        {
            if (candidates == null || candidates.value < 0 || candidates.value >= candidates.options.Count) return;
            if (candidates.options[candidates.value].text != value)
                candidates.SetValueWithoutNotify(-1);
        }

        private static void ClearCandidates(TMP_Dropdown candidates)
        {
            if (candidates == null) return;
            EnsureDropdownPlaceholder(candidates, "");
            candidates.ClearOptions();
            candidates.SetValueWithoutNotify(-1);
            candidates.gameObject.SetActive(true);
            candidates.interactable = false;
        }

        private void RefreshCandidateControls()
        {
            // Discovery and profile reload can change availability while a UI lock is held.
            foreach (var candidates in new[] { mainCandidates, embeddingCandidates })
                if (candidates != null)
                    candidates.interactable = !busy && !dropdownsWarming && candidates.options.Count > 0;
            if (profilesView != null)
            {
                // Missing connection fields are explained on click; discovery never needs a saved model ID.
                discoverMainButton.interactable = !busy && !dropdownsWarming && !defaultsRestored;
                testMainButton.interactable = !busy && !dropdownsWarming && !defaultsRestored && mainSupplier.value >= 0;
            }
        }

        private static string SelectedModel(TMP_InputField input, TMP_Dropdown candidates)
        {
            if (candidates != null && candidates.gameObject.activeSelf && candidates.value >= 0 &&
                candidates.value < candidates.options.Count)
                return candidates.options[candidates.value].text.Trim();
            return input.text.Trim();
        }

        private void TestMain() => Begin(Test(false));
        private void TestEmbedding() => Begin(Test(true));

        private IEnumerator Test(bool forEmbedding)
        {
            if (!RequireDeveloperProfiles()) yield break;
            if (profilesView != null) { PromptProbe(forEmbedding, "connection"); yield break; }
            if (!TryBuildMain(out MainModelConfig main)) yield break;
            EmbeddingConfig embed = null;
            if (forEmbedding && (!embeddingEnabled.isOn || !TryBuildEmbedding(main, out embed)))
            {
                if (!embeddingEnabled.isOn) SetStatus("请先启用 Embedding。");
                yield break;
            }
            TestConnectionResponse result = null;
            string error = null;
            if (forEmbedding)
                yield return client.TestEmbedding(main, embed, (value, code) => { result = value; error = code; });
            else
                yield return client.TestMain(main, (value, code) => { result = value; error = code; });
            string code = error ?? result?.error_code;
            if (code != null && code.IndexOf("TIMEOUT", StringComparison.OrdinalIgnoreCase) >= 0)
                SetStatus("连接超时，请检查 API 地址。");
            else if (code != null && code.IndexOf("AUTH", StringComparison.OrdinalIgnoreCase) >= 0)
                SetStatus("API 密钥无效或未获授权。", "The API key is invalid or unauthorized.");
            else if (code != null && (code.IndexOf("INVALID", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      code.IndexOf("CONFIG", StringComparison.OrdinalIgnoreCase) >= 0))
                SetStatus("配置无效，请检查 API 地址、模型和参数。");
            else if (error != null) SetStatus("连接失败，请检查配置。");
            else if (result != null && result.success)
                SetStatus(forEmbedding ? "✓ Embedding 可用" : "✓ 主模型连接成功");
            else SetStatus(forEmbedding ? "Embedding 测试失败，请检查配置。" : "主模型连接失败，请检查配置。");
        }

        private void SetStatus(string message)
        {
            SetStatus(message, SettingsUiLanguage.StatusEnglish(message));
        }

        private void SetStatus(string chinese, string english)
        {
            lastStatusChinese = chinese;
            lastStatusEnglish = english;
            if (statusText != null)
            {
                statusText.text = SettingsUiLanguage.T(chinese, english);
                statusText.color = SettingsPanelTheme.Ink;
                statusText.textWrappingMode = TextWrappingModes.Normal;
                statusText.enableAutoSizing = true; statusText.fontSizeMin = 20; statusText.fontSizeMax = 24;
            }
        }
    }
}
