using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EmbodiedNPC.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CafeDemo
{
    /// <summary>Owns a developer draft; Backend alone owns stored Knowledge and versions.</summary>
    public sealed class CafeWorldKnowledgePanel : MonoBehaviour
    {
        private CafeKnowledgeView view;
        private KnowledgeManagementClient client;
        private KnowledgeCatalogView catalog;
        private string selectedId;
        private int editRevision;
        private bool dirty, busy, loadingControls, polling, unsupportedScope;
        private float nextPoll;
        private string statusZh = "正在加载世界知识…", statusEn = "Loading world knowledge…";
        private Transform list, form, attributes, relations, scopes;
        private GameObject advanced, restricted, dialog;
        private TMP_InputField search, nameInput, contentInput, typeInput, relationSearch;
        private TMP_Dropdown typePreset, accessMode, relationPicker;
        private TMP_Text listHint, metadata, indexText, status, dialogText;
        private CanvasGroup formGroup;
        private Button save, delete, restore, discard, create, reload;
        private readonly List<(KnowledgeItemView item, Button button)> listRows = new();
        private readonly List<AttributeRow> attributeRows = new();
        private readonly List<string> related = new(), relationOptions = new();
        private readonly List<(string id, Toggle toggle)> roles = new(), scenes = new();
        private string[] presetValues = { "reference", "setting", "character", "place", "item", "drink", "rule", "history", "service_reference", "" };
        private string dialogZh, dialogEn;
        private Action dialogCancel;
        public bool IsDirty => dirty;
        public bool IsBusy => busy;
        public bool ConfirmationVisible => dialog != null && dialog.activeSelf;
        public string SelectedId => selectedId;
        public string DraftName => nameInput?.text;
        public string DraftContent => contentInput?.text;

        private sealed class AttributeRow
        { public GameObject root; public TMP_InputField key, value; public TMP_Dropdown type; }

        public void Initialize(TMP_FontAsset font, KnowledgeManagementClient managementClient)
        {
            if (view != null) return;
            client = managementClient; view = new CafeKnowledgeView(font);
            var title = view.Text(transform, "KnowledgeTitle", "世界知识", "World Knowledge", 30);
            CafeKnowledgeView.Rect(title.transform, new Vector2(.025f, 1), new Vector2(.75f, 1), new Vector2(0, -66), new Vector2(0, -12));
            create = view.Button(transform, "NewKnowledge", "新建知识", "New knowledge", NewKnowledge);
            CafeKnowledgeView.Rect(create.transform, new Vector2(1, 1), Vector2.one, new Vector2(-230, -62), new Vector2(-36, -14));
            search = view.Input(transform, "KnowledgeSearch");
            CafeKnowledgeView.Rect(search.transform, new Vector2(.025f, 1), new Vector2(.32f, 1), new Vector2(0, -124), new Vector2(0, -76));
            list = view.Scroll(transform, "KnowledgeList");
            CafeKnowledgeView.Rect(list.parent.parent, new Vector2(.025f, 0), new Vector2(.32f, 1), new Vector2(0, 106), new Vector2(0, -134));
            listHint = view.Text(list, "KnowledgeListHint", "正在加载…", "Loading…", 18); CafeKnowledgeView.Height(listHint.gameObject, 80);
            reload = view.Button(transform, "ReloadKnowledge", "重新加载", "Reload", () => RequestLeave(() => StartCoroutine(LoadCatalog())));
            CafeKnowledgeView.Rect(reload.transform, new Vector2(.025f, 0), new Vector2(.32f, 0), new Vector2(0, 28), new Vector2(0, 80));
            form = view.Scroll(transform, "KnowledgeForm");
            CafeKnowledgeView.Rect(form.parent.parent, new Vector2(.35f, 0), new Vector2(.975f, 1), new Vector2(0, 110), new Vector2(0, -76));
            formGroup = form.gameObject.AddComponent<CanvasGroup>();
            Label(form, "知识名称 *", "Knowledge name *"); nameInput = view.Input(form, "KnowledgeName"); CafeKnowledgeView.Height(nameInput.gameObject, 52);
            Label(form, "知识内容 *", "Content *"); contentInput = view.Input(form, "KnowledgeContent", true); CafeKnowledgeView.Height(contentInput.gameObject, 248);
            Label(form, "知识类型", "Knowledge Type");
            typePreset = view.Dropdown(form, "KnowledgeTypePreset",
                new[] { "参考", "设定", "人物", "地点", "物品", "饮品", "规则", "历史事件", "服务参考", "自定义" },
                new[] { "Reference", "Setting", "Character", "Place", "Item", "Drink", "Rule", "Historical event", "Service Reference", "Custom" });
            CafeKnowledgeView.Height(typePreset.gameObject, 44);
            typeInput = view.Input(form, "KnowledgeType"); CafeKnowledgeView.Height(typeInput.gameObject, 48);
            metadata = view.Text(form, "KnowledgeMetadata", "", "", 16); CafeKnowledgeView.Height(metadata.gameObject, 94);
            var expand = view.Button(form, "KnowledgeAdvanced", "高级信息与自定义属性（展开 / 折叠）", "Advanced information & attributes (expand / collapse)", () => advanced.SetActive(!advanced.activeSelf));
            CafeKnowledgeView.Height(expand.gameObject, 46);
            advanced = CafeKnowledgeView.Node("KnowledgeAdvancedContent", form, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            Stack(advanced); advanced.SetActive(false);
            metadata.transform.SetParent(advanced.transform, false);
            Label(advanced.transform, "自定义属性", "Custom attributes");
            attributes = Container(advanced.transform, "Attributes");
            var add = view.Button(advanced.transform, "AddKnowledgeAttribute", "添加属性", "Add attribute", () =>
            {
                if (attributeRows.Count >= catalog.limits.attribute_keys) { Error("属性数量达到上限。", "Attribute limit reached."); return; }
                AddAttribute("", JValue.CreateString("")); Changed();
            }); CafeKnowledgeView.Height(add.gameObject, 44);
            Label(form, "关联知识", "Related knowledge");
            relations = Container(form, "Relations");
            relationSearch = view.Input(form, "RelatedKnowledgeSearch"); CafeKnowledgeView.Height(relationSearch.gameObject, 44);
            relationPicker = view.Dropdown(form, "RelatedKnowledgePicker", new[] { "选择要关联的知识" }, new[] { "Choose related knowledge" });
            CafeKnowledgeView.Height(relationPicker.gameObject, 44);
            Label(form, "访问范围", "Access scope");
            accessMode = view.Dropdown(form, "KnowledgeAccessMode", new[] { "默认授权范围", "限定角色 / 场景" }, new[] { "Default authorized scope", "Restrict character / scene" });
            CafeKnowledgeView.Height(accessMode.gameObject, 46);
            restricted = CafeKnowledgeView.Node("RestrictedKnowledgeScope", form, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)); Stack(restricted);
            scopes = restricted.transform;
            expand.transform.SetAsLastSibling(); advanced.transform.SetAsLastSibling();
            indexText = view.Text(transform, "KnowledgeIndexStatus", "", "", 21);
            CafeKnowledgeView.Rect(indexText.transform, new Vector2(.35f, 0), new Vector2(.975f, 0), new Vector2(0, 82), new Vector2(0, 108));
            status = view.Text(transform, "KnowledgeSaveStatus", "", "", 21);
            CafeKnowledgeView.Rect(status.transform, new Vector2(.35f, 0), Vector2.right, new Vector2(0, 16), new Vector2(-690, 78));
            discard = Footer("DiscardKnowledgeDraft", "放弃修改", "Discard draft", -674, -510, Discard);
            restore = Footer("RestoreKnowledge", "恢复默认", "Restore defaults", -500, -324, Restore);
            delete = Footer("DeleteKnowledge", "删除", "Delete", -314, -184, Delete);
            save = Footer("SaveKnowledge", "保存", "Save", -174, -36, () => Save()); SettingsPanelTheme.Button(save, true);
            LayoutPageFooter();
            search.onValueChanged.AddListener(_ => FilterList());
            nameInput.onValueChanged.AddListener(_ => Changed()); contentInput.onValueChanged.AddListener(_ => Changed());
            typeInput.onValueChanged.AddListener(_ => Changed());
            typePreset.onValueChanged.AddListener(i => { if (presetValues[i].Length != 0) typeInput.text = presetValues[i]; RefreshTypeEditor(); });
            relationSearch.onValueChanged.AddListener(_ => RefreshRelationPicker());
            relationPicker.onValueChanged.AddListener(i =>
            {
                if (loadingControls || i == 0 || i > relationOptions.Count) return;
                if (related.Count >= catalog.limits.related_ids) { Error("关联数量达到上限。", "Related knowledge limit reached."); return; }
                related.Add(relationOptions[i - 1]); RenderRelations(); Changed();
            });
            accessMode.onValueChanged.AddListener(_ => { restricted.SetActive(accessMode.value == 1); Changed(); });
            RefreshLanguage(); UpdateButtons();
        }

        private void OnEnable()
        { SettingsUiLanguage.Changed += RefreshLanguage; RefreshLanguage(); if (Application.isPlaying && view != null && catalog == null) StartCoroutine(LoadCatalog()); }
        private void OnDisable()
        {
            SettingsUiLanguage.Changed -= RefreshLanguage; StopAllCoroutines(); busy = polling = false;
            var events = EventSystem.current;
            if (events?.currentSelectedGameObject != null && events.currentSelectedGameObject.transform.IsChildOf(transform))
                events.SetSelectedGameObject(null);
        }
        private void Update()
        { if (catalog != null && !busy && !polling && Time.unscaledTime >= nextPoll) StartCoroutine(PollIndex()); }
        private KnowledgeItemView Selected => catalog?.items.FirstOrDefault(i => i.entry.knowledge_id == selectedId);

        private IEnumerator LoadCatalog()
        {
            busy = true; SetStatus("正在连接世界知识管理…", "Connecting to knowledge management…"); UpdateButtons();
            KnowledgeCatalogView result = null; string error = null;
            yield return client.Catalog((data, code) => { result = data; error = code; });
            busy = false;
            if (error != null || result?.limits == null || result.scopes == null) { ShowError(error); UpdateButtons(); yield break; }
            ApplyCatalog(result, selectedId);
            SetStatus(result.items.Length == 0 ? "已加载，知识列表为空。" : "已加载。", result.items.Length == 0 ? "Loaded; knowledge list is empty." : "Loaded.");
        }
        private void ApplyCatalog(KnowledgeCatalogView data, string preferred)
        {
            catalog = data; nextPoll = Time.unscaledTime + 5;
            nameInput.characterLimit = data.limits.name; contentInput.characterLimit = data.limits.content; typeInput.characterLimit = data.limits.knowledge_type;
            foreach (var row in listRows) Remove(row.button.gameObject); listRows.Clear();
            foreach (var item in data.items)
            {
                var captured = item;
                var button = view.Button(list, "KnowledgeItem", "", "", () => RequestLeave(() => LoadItem(captured)));
                CafeKnowledgeView.Height(button.gameObject, 74); listRows.Add((item, button));
            }
            var chosen = data.items.FirstOrDefault(i => i.entry.knowledge_id == preferred);
            if (chosen == null && preferred == null) chosen = data.items.FirstOrDefault();
            LoadItem(chosen); FilterList(); RefreshIndex(data.index);
        }
        private void LoadItem(KnowledgeItemView item)
        {
            loadingControls = true; selectedId = item?.entry.knowledge_id; editRevision = catalog.revision;
            nameInput.SetTextWithoutNotify(item?.entry.name ?? ""); contentInput.SetTextWithoutNotify(item?.entry.content ?? "");
            typeInput.SetTextWithoutNotify(item?.entry.knowledge_type ?? "reference");
            int preset = Array.IndexOf(presetValues, typeInput.text); typePreset.SetValueWithoutNotify(preset < 0 ? presetValues.Length - 1 : preset); typePreset.RefreshShownValue(); RefreshTypeEditor();
            foreach (var row in attributeRows) Remove(row.root); attributeRows.Clear();
            if (item != null) foreach (var pair in item.entry.attributes) AddAttribute(pair.Key, pair.Value);
            related.Clear(); if (item != null) related.AddRange(item.entry.related_ids);
            bool isDefault = item == null || (new HashSet<string>(item.entry.access.character_ids).SetEquals(catalog.scopes.character_ids)
                && item.entry.access.scene_ids.Length == 0 && item.entry.access.deployment_ids.SequenceEqual(new[] { catalog.scopes.deployment_id }));
            accessMode.SetValueWithoutNotify(isDefault ? 0 : 1); accessMode.RefreshShownValue();
            BuildScopes(item); restricted.SetActive(!isDefault); RenderRelations(); advanced.SetActive(false);
            dirty = false; loadingControls = false; UpdateMetadata(); UpdateListLabels(); UpdateButtons();
            var scroll = form.GetComponentInParent<ScrollRect>(); scroll.verticalNormalizedPosition = 1;
        }
        public void NewKnowledge() => RequestLeave(() => { if (catalog != null) { LoadItem(null); SetStatus("新建知识：填写名称和内容后保存。", "New knowledge: enter a name and content, then save."); nameInput.Select(); nameInput.ActivateInputField(); } });
        private void Discard() { LoadItem(Selected); SetStatus("已放弃未保存修改。", "Unsaved changes discarded."); }
        private void Changed()
        { if (loadingControls) return; dirty = true; SetStatus("未保存。", "Unsaved."); UpdateMetadata(); UpdateButtons(); }

        private KnowledgeEditView Edit()
        {
            var result = new KnowledgeEditView { expected_revision = editRevision, name = nameInput.text.Trim(), content = contentInput.text.Trim(),
                knowledge_type = string.IsNullOrWhiteSpace(typeInput.text) ? "reference" : typeInput.text.Trim(),
                related_ids = related.ToArray(), access_mode = accessMode.value == 0 ? "default" : "restricted" };
            if (result.name.Length == 0 || result.content.Length == 0) throw new ArgumentException(SettingsUiLanguage.T("请填写知识名称和内容。", "Enter a knowledge name and content."));
            foreach (var row in attributeRows)
            {
                string key = row.key.text.Trim();
                if (key.Length == 0 || key.Length > catalog.limits.attribute_key_length || result.attributes.ContainsKey(key))
                    throw new ArgumentException(SettingsUiLanguage.T("属性名称不能为空、重复或超过上限。", "Attribute keys must be nonempty, unique and within the limit."));
                JToken value;
                switch (row.type.value)
                {
                    case 0: value = new JValue(row.value.text); break;
                    case 1:
                        value = JToken.Parse(row.value.text);
                        if ((value.Type != JTokenType.Integer && value.Type != JTokenType.Float) ||
                            (value.Type == JTokenType.Float && (double.IsInfinity((double)value) || double.IsNaN((double)value))))
                            throw new ArgumentException(SettingsUiLanguage.T("属性数字无效。", "Invalid numeric attribute."));
                        break;
                    case 2:
                        if (!bool.TryParse(row.value.text, out bool boolean)) throw new ArgumentException(SettingsUiLanguage.T("布尔属性请输入 true 或 false。", "Enter true or false for boolean values."));
                        value = new JValue(boolean); break;
                    case 3: value = JValue.CreateNull(); break;
                    default:
                        value = JToken.Parse(row.value.text);
                        if (value.Type != JTokenType.Object && value.Type != JTokenType.Array) throw new ArgumentException(SettingsUiLanguage.T("JSON 属性必须是对象或数组。", "JSON attributes must be objects or arrays."));
                        break;
                }
                result.attributes.Add(key, value);
            }
            if (JsonConvert.SerializeObject(result.attributes, Formatting.None).Length > catalog.limits.attributes_chars)
                throw new ArgumentException(SettingsUiLanguage.T("属性总长度超过 Backend 上限。", "Attributes exceed the Backend limit."));
            if (result.access_mode == "restricted")
            {
                if (unsupportedScope) throw new ArgumentException(SettingsUiLanguage.T("原范围包含当前无法可信绑定的身份；请明确选择有效范围。", "The existing scope contains unbound identities; explicitly choose a valid scope."));
                result.character_ids = roles.Where(r => r.toggle.isOn).Select(r => r.id).ToArray();
                result.scene_ids = scenes.Where(r => r.toggle.isOn).Select(r => r.id).ToArray();
                if (result.character_ids.Length == 0) throw new ArgumentException(SettingsUiLanguage.T("请至少选择一个已授权角色。", "Select at least one authorized character."));
            }
            return result;
        }
        public void Save(Action after = null)
        {
            if (busy || catalog == null || Selected?.hidden == true) return;
            KnowledgeEditView edit;
            try { edit = Edit(); }
            catch (Exception error) when (error is ArgumentException || error is JsonException)
            { SetStatus(error.Message, error.Message); return; }
            StartCoroutine(Mutate(done => client.Save(selectedId, edit, done), selectedId, after));
        }
        private IEnumerator Mutate(Func<Action<KnowledgeCatalogView, string>, IEnumerator> request, string keepId, Action after = null)
        {
            busy = true; SetStatus("保存 / 更新中…", "Saving / updating…"); UpdateButtons();
            KnowledgeCatalogView data = null; string error = null;
            yield return request((result, code) => { data = result; error = code; });
            busy = false;
            if (error != null || data?.limits == null) { ShowError(error); UpdateButtons(); yield break; }
            ApplyCatalog(data, data.selected_id ?? keepId); SetStatus("已保存；索引状态另行更新。", "Saved; indexing is a separate operation."); after?.Invoke();
        }
        private void Delete()
        {
            var item = Selected; if (busy || item == null || item.hidden) return;
            string references = string.Join("、", item.referenced_by.Select(r => r.name));
            if (references.Length != 0) { Error("此知识被「" + references + "」引用，请先解除关联。", "Referenced by " + references + ". Unlink those references first."); return; }
            Confirm("删除「" + item.entry.name + "」？" + (item.has_default ? "内置知识将隐藏，可恢复默认。" : "将删除此自定义知识。"),
                "Delete “" + item.entry.name + "”? " + (item.has_default ? "The builtin will be hidden and can be restored." : "This custom entry will be deleted."),
                "确认删除", "Delete", () => StartCoroutine(Mutate(done => client.Delete(selectedId, editRevision, done), selectedId)));
        }
        private void Restore()
        {
            if (busy || Selected?.has_default != true) return;
            Confirm("恢复「" + Selected.entry.name + "」的内置默认内容？未保存修改也将放弃。", "Restore builtin defaults for “" + Selected.entry.name + "”? Unsaved edits will also be discarded.",
                "恢复默认", "Restore", () => StartCoroutine(Mutate(done => client.Restore(selectedId, editRevision, done), selectedId)));
        }
        public void RequestLeave(Action proceed)
        {
            if (busy) { Error("请等待当前操作完成。", "Wait for the current operation to finish."); return; }
            if (ConfirmationVisible) return;
            if (!dirty) { proceed(); return; }
            ShowDialog("当前知识有未保存修改。", "This knowledge has unsaved changes.",
                new[] { ("保存并继续", "Save & continue", (Action)(() => Save(proceed))),
                        ("放弃修改", "Discard", (Action)(() => { Discard(); proceed(); })),
                        ("取消", "Cancel", (Action)(() => { })) });
        }
        public bool CancelConfirmation()
        {
            if (!ConfirmationVisible) return false;
            if (busy) return true;
            dialog.SetActive(false);
            dialogCancel?.Invoke();
            return true;
        }
        private void Confirm(string zh, string en, string yesZh, string yesEn, Action action) =>
            ShowDialog(zh, en, new[] { (yesZh, yesEn, action), ("取消", "Cancel", (Action)(() => { })) });
        private void ShowDialog(string zh, string en, (string zh, string en, Action action)[] actions)
        {
            dialogCancel = actions.Length == 0 ? null : actions[actions.Length - 1].action;
            if (dialog != null) Remove(dialog);
            dialog = CafeKnowledgeView.Node("KnowledgeConfirmation", transform, typeof(Image));
            CafeKnowledgeView.Rect(dialog.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            dialog.GetComponent<Image>().color = new Color(.12f, .10f, .08f, .86f);
            var box = CafeKnowledgeView.Node("Dialog", dialog.transform, typeof(Image));
            CafeKnowledgeView.Rect(box.transform, new Vector2(.22f, .34f), new Vector2(.78f, .68f), Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(box.GetComponent<Image>(), SettingsPanelTheme.Paper);
            dialogZh = zh; dialogEn = en; dialogText = view.Text(box.transform, "Message", zh, en, 23);
            CafeKnowledgeView.Rect(dialogText.transform, Vector2.zero, Vector2.one, new Vector2(24, 80), new Vector2(-24, -20));
            for (int i = 0; i < actions.Length; i++)
            {
                var choice = actions[i]; var button = view.Button(box.transform, "ConfirmChoice" + i, choice.zh, choice.en,
                    () => { dialog.SetActive(false); choice.action(); });
                CafeKnowledgeView.Rect(button.transform, new Vector2((float)i / actions.Length, 0), new Vector2((float)(i + 1) / actions.Length, 0), new Vector2(12, 18), new Vector2(-12, 66));
            }
        }

        private IEnumerator PollIndex()
        {
            polling = true; nextPoll = Time.unscaledTime + 5;
            yield return client.Status((data, code) => RefreshIndex(code == null ? data?.index : null));
            polling = false;
        }
        private void RefreshIndex(KnowledgeIndexView value)
        {
            if (value != null && catalog != null) catalog.index = value;
            if (value == null || value.mode != "running")
                indexText.text = SettingsUiLanguage.T("整体索引暂不可用 / 暂停；保留结构化和词法检索。", "Global index unavailable / paused; structured and lexical retrieval remain available.");
            else indexText.text = SettingsUiLanguage.T(value.ready == value.total ? "整体索引完成：" : "等待 / 更新整体索引：", value.ready == value.total ? "Global index ready: " : "Global indexing pending: ") + value.ready + "/" + value.total;
        }
        private void SetStatus(string zh, string en) { statusZh = zh; statusEn = en; if (status != null) status.text = SettingsUiLanguage.T(zh, en); }
        private void Error(string zh, string en) => SetStatus(zh, en);
        private void ShowError(string code)
        {
            switch (code)
            {
                case "LOCAL_DEVELOPER_SESSION_PATH_INVALID":
                    Error("授权文件路径无效或超出 UserData；不会使用旧目录。请检查便携启动入口。草稿保留。", "Authorization path invalid or outside UserData; no legacy fallback. Check the portable entry. Draft retained."); break;
                case "LOCAL_DEVELOPER_SESSION_MISSING":
                    Error("本机授权文件不存在。请启动随包 Backend 后重新加载。草稿保留。", "Local authorization file not found. Start the packaged Backend and reload. Draft retained."); break;
                case "LOCAL_DEVELOPER_SESSION_ACCESS_DENIED":
                    Error("无权读取本机授权文件。请检查安装目录的读取权限后重新加载。草稿保留。", "Cannot access the local authorization file. Check installation-directory read permissions and reload. Draft retained."); break;
                case "LOCAL_DEVELOPER_SESSION_UNREADABLE":
                    Error("本机授权文件暂时无法读取。请稍后重新加载。草稿保留。", "Local authorization file cannot be read. Reload shortly. Draft retained."); break;
                case "LOCAL_DEVELOPER_SESSION_INVALID": case "LOCAL_DEVELOPER_SESSION_URL_INVALID":
                    Error("本机授权文件格式或服务地址无效。请由对应 Backend 重新生成后加载。草稿保留。", "Invalid local authorization file or service URL. Let the matching Backend regenerate it, then reload. Draft retained."); break;
                case "LOCAL_DEVELOPER_SESSION_MISMATCH": case "KNOWLEDGE_MANAGEMENT_UNAUTHORIZED":
                    Error("本机授权已失效或与当前服务不匹配。请确认对应 Backend 后重新加载。草稿保留。", "Local authorization expired or does not match this service. Check the matching Backend and reload. Draft retained."); break;
                case "LOCAL_DEVELOPER_SESSION_REQUIRED":
                    Error("未获得本机开发者授权；请启动对应 Backend 并检查会话文件。草稿保留。", "Local developer authorization unavailable. Start the matching Backend and check its session file. Draft retained."); break;
                case "KNOWLEDGE_REVISION_CONFLICT":
                    Error("版本冲突：草稿保留，请重新加载后核对；不会覆盖新数据。", "Revision conflict: draft retained. Reload and review; newer data was not overwritten."); break;
                case "BACKEND_UNAVAILABLE":
                    Error("Backend 已断开或超时。草稿保留，可重新加载或重试保存。", "Backend disconnected or timed out. Draft retained; reload or retry saving."); break;
                default:
                    Error("操作失败。草稿保留，请检查字段、范围或 Backend 状态。", "Operation failed. Draft retained; check fields, scope and Backend status."); break;
            }
        }
        private void UpdateButtons()
        {
            if (save == null) return;
            bool available = catalog != null && !busy, hidden = Selected?.hidden == true;
            formGroup.interactable = available && !hidden;
            save.interactable = available && !hidden; delete.interactable = available && Selected != null && !hidden;
            restore.gameObject.SetActive(Selected?.has_default == true); restore.interactable = available;
            discard.interactable = available && dirty; create.interactable = available; reload.interactable = !busy;
            foreach (var row in listRows) row.button.interactable = !busy;
        }
        private void UpdateMetadata()
        {
            var item = Selected;
            string origin = item == null ? SettingsUiLanguage.T("新建草稿", "New draft") : !item.has_default ? SettingsUiLanguage.T("自定义知识", "Custom") : item.origin == "override" ? SettingsUiLanguage.T("本地覆盖", "Local override") : SettingsUiLanguage.T("内置知识", "Builtin");
            metadata.text = origin + (dirty ? SettingsUiLanguage.T(" · 未保存", " · Unsaved") : "") + (item?.hidden == true ? SettingsUiLanguage.T(" · 已隐藏", " · Hidden") : "") +
                "\nID: " + (selectedId ?? SettingsUiLanguage.T("保存后由 Backend 生成", "Generated by Backend on save")) +
                "\nRevision: " + editRevision + (item == null ? "" : " · Version: " + item.entry.version + " · " + item.entry.updated_at + "\n" + item.entry.source);
        }
        private void FilterList()
        {
            string text = search.text.Trim(); int visible = 0;
            foreach (var row in listRows)
            {
                bool show = text.Length == 0 || row.item.entry.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || row.item.entry.content.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                row.button.gameObject.SetActive(show); if (show) visible++;
            }
            listHint.gameObject.SetActive(visible == 0); listHint.text = SettingsUiLanguage.T(catalog == null ? "尚未加载，请连接 Backend 后重试。" : "没有匹配的知识。", catalog == null ? "Not loaded. Connect to the Backend and retry." : "No matching knowledge.");
        }
        private void UpdateListLabels()
        {
            foreach (var row in listRows)
            {
                row.button.GetComponentInChildren<TMP_Text>().text = row.item.entry.name + "\n" + TypeCaption(row.item.entry.knowledge_type) + (row.item.hidden ? SettingsUiLanguage.T(" · 已隐藏", " · Hidden") : "");
                SettingsPanelTheme.Button(row.button, row.item.entry.knowledge_id == selectedId);
            }
        }
        public static string TypeCaption(string type) => type switch
        {
            "reference" => SettingsUiLanguage.T("参考", "Reference"),
            "setting" => SettingsUiLanguage.T("设定", "Setting"),
            "character" => SettingsUiLanguage.T("人物", "Character"),
            "place" => SettingsUiLanguage.T("地点", "Place"),
            "item" => SettingsUiLanguage.T("物品", "Item"),
            "drink" => SettingsUiLanguage.T("饮品", "Drink"),
            "rule" => SettingsUiLanguage.T("规则", "Rule"),
            "history" => SettingsUiLanguage.T("历史事件", "Historical event"),
            "service_reference" => SettingsUiLanguage.T("服务参考", "Service Reference"),
            _ => type
        };
        private void RefreshTypeEditor() => typeInput.gameObject.SetActive(typePreset.value == presetValues.Length - 1);

        private void BuildScopes(KnowledgeItemView item)
        {
            Clear(scopes); roles.Clear(); scenes.Clear();
            var access = item?.entry.access;
            unsupportedScope = access != null && (access.character_ids.Except(catalog.scopes.character_ids).Any() || access.scene_ids.Except(catalog.scopes.scene_ids).Any() || access.deployment_ids.Any(d => d != catalog.scopes.deployment_id));
            foreach (string role in catalog.scopes.character_ids)
            {
                var toggle = view.Toggle(scopes, "KnowledgeRole", RoleCaption(role), access == null || access.character_ids.Contains(role)); CafeKnowledgeView.Height(toggle.gameObject, 34);
                roles.Add((role, toggle)); toggle.onValueChanged.AddListener(_ => Changed());
            }
            foreach (string scene in catalog.scopes.scene_ids)
            {
                var toggle = view.Toggle(scopes, "KnowledgeScene", scene, access?.scene_ids.Contains(scene) == true); CafeKnowledgeView.Height(toggle.gameObject, 34);
                scenes.Add((scene, toggle)); toggle.onValueChanged.AddListener(_ => Changed());
            }
            if (catalog.scopes.scene_ids.Length == 0) Label(scopes, "当前没有可信 scene_id；不能限定场景。", "No trusted scene_id; scene restriction is unavailable.", 16, 52);
            if (unsupportedScope) Label(scopes, "原范围含未绑定身份，请明确核对，不会自动放宽。", "Existing scope has unbound identities; it will not be silently widened.", 16, 52);
        }
        private void AddAttribute(string key, JToken value)
        {
            var root = CafeKnowledgeView.Node("KnowledgeAttribute", attributes); CafeKnowledgeView.Height(root, 54);
            var row = new AttributeRow { root = root, key = view.Input(root.transform, "AttributeKey"), value = view.Input(root.transform, "AttributeValue"),
                type = view.Dropdown(root.transform, "AttributeType", new[] { "文本", "数字", "布尔", "空", "JSON" }, new[] { "Text", "Number", "Bool", "Null", "JSON" }) };
            CafeKnowledgeView.Rect(row.key.transform, Vector2.zero, new Vector2(.35f, 1), Vector2.zero, new Vector2(-6, 0));
            CafeKnowledgeView.Rect(row.type.transform, new Vector2(.35f, 0), new Vector2(.53f, 1), Vector2.zero, new Vector2(-6, 0));
            CafeKnowledgeView.Rect(row.value.transform, new Vector2(.53f, 0), new Vector2(.91f, 1), Vector2.zero, new Vector2(-6, 0));
            var button = view.Button(root.transform, "RemoveAttribute", "×", "×", () => { attributeRows.Remove(row); Remove(root); Changed(); });
            CafeKnowledgeView.Rect(button.transform, new Vector2(.91f, 0), Vector2.one, Vector2.zero, Vector2.zero);
            row.key.characterLimit = catalog.limits.attribute_key_length; row.value.characterLimit = catalog.limits.attributes_chars;
            row.key.SetTextWithoutNotify(key); row.value.SetTextWithoutNotify(value?.Type == JTokenType.String ? (string)value : value?.ToString(Formatting.None) ?? "null");
            int type = value == null || value.Type == JTokenType.Null ? 3 : value.Type == JTokenType.String ? 0 : value.Type == JTokenType.Boolean ? 2 : value.Type == JTokenType.Integer || value.Type == JTokenType.Float ? 1 : 4;
            row.type.SetValueWithoutNotify(type); row.type.RefreshShownValue();
            row.key.onValueChanged.AddListener(_ => Changed()); row.value.onValueChanged.AddListener(_ => Changed()); row.type.onValueChanged.AddListener(_ => Changed()); attributeRows.Add(row);
        }
        private void RenderRelations()
        {
            Clear(relations);
            foreach (string id in related.ToArray())
            {
                string caption = catalog.items.FirstOrDefault(i => i.entry.knowledge_id == id)?.entry.name ?? id + SettingsUiLanguage.T("（已失效）", " (unavailable)");
                var row = CafeKnowledgeView.Node("KnowledgeRelation", relations); CafeKnowledgeView.Height(row, 40);
                var text = view.Text(row.transform, "RelatedName", caption, caption, 18); CafeKnowledgeView.Rect(text.transform, Vector2.zero, new Vector2(.88f, 1), Vector2.zero, Vector2.zero);
                var remove = view.Button(row.transform, "UnlinkKnowledge", "×", "×", () => { related.Remove(id); RenderRelations(); Changed(); });
                CafeKnowledgeView.Rect(remove.transform, new Vector2(.88f, 0), Vector2.one, Vector2.zero, Vector2.zero);
            }
            RefreshRelationPicker();
        }
        private void RefreshRelationPicker()
        {
            if (catalog == null) return;
            bool previous = loadingControls; loadingControls = true; relationOptions.Clear();
            var names = new List<string> { SettingsUiLanguage.T("选择要关联的知识", "Choose related knowledge") };
            foreach (var item in catalog.items)
                if (!item.hidden && item.entry.knowledge_id != selectedId && !related.Contains(item.entry.knowledge_id) &&
                    item.entry.name.IndexOf(relationSearch.text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                { relationOptions.Add(item.entry.knowledge_id); names.Add(item.entry.name); }
            relationPicker.ClearOptions(); relationPicker.AddOptions(names); relationPicker.SetValueWithoutNotify(0); relationPicker.RefreshShownValue(); loadingControls = previous;
        }
        private void RefreshLanguage()
        {
            if (view == null) return;
            view.RefreshLanguage(); UpdateListLabels(); FilterList(); RefreshTypeEditor();
            foreach (var role in roles)
            {
                var label = role.toggle.transform.parent.Find("ScopeLabel")?.GetComponent<TMP_Text>();
                if (label != null) label.text = RoleCaption(role.id);
            }
            SettingsPanelTheme.Button(save, true);
            status.text = SettingsUiLanguage.T(statusZh, statusEn);
            if (catalog != null) { UpdateMetadata(); RefreshRelationPicker(); RefreshIndex(catalog.index); }
            if (dialogText != null) dialogText.text = SettingsUiLanguage.T(dialogZh, dialogEn);
        }
        private string RoleCaption(string id)
        {
            var definition = FindObjectsByType<EmbodiedNPC.Cognition.CognitiveContextComposer>(FindObjectsSortMode.None)
                .Select(x => x.CharacterDefinition).FirstOrDefault(x => x != null && x.CharacterId == id);
            return definition == null ? id : SettingsUiLanguage.NpcDisplayName(id,
                EmbodiedNPC.Cognition.CharacterProfileResolver.Resolve(definition)?.display_name) ?? id;
        }
        private Button Footer(string id, string zh, string en, float x1, float x2, Action action)
        { var button = view.Button(transform, id, zh, en, action); CafeKnowledgeView.Rect(button.transform, Vector2.right, Vector2.right, new Vector2(x1, 26), new Vector2(x2, 78)); return button; }
        private void LayoutPageFooter()
        {
            var footer=CafeKnowledgeView.Node("KnowledgeFooter",transform,typeof(Image));
            CafeKnowledgeView.Rect(footer.transform,Vector2.zero,Vector2.right,Vector2.zero,new Vector2(0,164));
            footer.GetComponent<Image>().color=SettingsPanelTheme.Paper;
            var actions=CafeKnowledgeView.Node("KnowledgeFooterActions",footer.transform,typeof(HorizontalLayoutGroup));
            CafeKnowledgeView.Rect(actions.transform,Vector2.zero,Vector2.right,new Vector2(20,12),new Vector2(-20,64));
            var stack=actions.GetComponent<HorizontalLayoutGroup>();stack.spacing=12;stack.childControlWidth=stack.childControlHeight=true;stack.childForceExpandWidth=true;
            foreach(var button in new[]{reload,discard,restore,delete,save})
            {button.transform.SetParent(actions.transform,false);var e=button.GetComponent<LayoutElement>()??button.gameObject.AddComponent<LayoutElement>();e.minWidth=0;e.flexibleWidth=1;}
            status.transform.SetParent(footer.transform,false);
            CafeKnowledgeView.Rect(status.transform,Vector2.zero,Vector2.right,new Vector2(20,76),new Vector2(-20,114));
            status.textWrappingMode=TextWrappingModes.Normal;status.overflowMode=TextOverflowModes.Ellipsis;
            indexText.transform.SetParent(footer.transform,false);
            CafeKnowledgeView.Rect(indexText.transform,Vector2.zero,Vector2.right,new Vector2(20,120),new Vector2(-20,154));
            foreach(var scroll in GetComponentsInChildren<ScrollRect>(true))
            {var r=(RectTransform)scroll.transform;r.offsetMin=new Vector2(r.offsetMin.x,178);}
        }
        private void Label(Transform parent, string zh, string en, float size = 20, float height = 32)
        { var label = view.Text(parent, "KnowledgeLabel", zh, en, size); CafeKnowledgeView.Height(label.gameObject, height); }
        private static Transform Container(Transform parent, string name)
        { var go = CafeKnowledgeView.Node(name, parent, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)); Stack(go); return go.transform; }
        private static void Stack(GameObject go)
        { var layout = go.GetComponent<VerticalLayoutGroup>(); layout.spacing = 8; layout.childControlHeight = layout.childControlWidth = true; layout.childForceExpandHeight = false; go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize; }
        private static void Remove(GameObject go) { if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }
        private static void Clear(Transform parent) { foreach (Transform child in parent) Remove(child.gameObject); }
    }
}
