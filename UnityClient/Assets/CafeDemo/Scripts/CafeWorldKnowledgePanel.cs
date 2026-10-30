using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private TMP_InputField search, nameInput, contentInput, typeInput, relationSearch, keywordsInput;
        private TMP_Dropdown typePreset, accessMode, relationPicker, usageMode;
        private TMP_Dropdown typeFilter, usageFilter, accessFilter, npcFilter;
        private Toggle preserveScope;
        private TMP_Text pageText;
        private Button previousPage, nextPage, importCatalog, exportCatalog, restoreAll;
        private readonly List<string> filterTypes = new(), filterNpcs = new();
        private string selectedType = "", selectedNpc = "";
        private int page, filteredCount;
        private const int PageSize = 40;
        public Func<bool, string> SelectJsonFile { private get; set; } = CafeKnowledgeFiles.Choose;
        private TMP_Text listHint, metadata, indexText, status, dialogText;
        private CanvasGroup formGroup;
        private Button save, delete, restore, discard, create, reload;
        private readonly List<(KnowledgeItemView item, Button button)> listRows = new();
        private readonly List<AttributeRow> attributeRows = new();
        private readonly List<string> related = new(), relationOptions = new();
        private readonly List<(string id, Toggle toggle)> roles = new(), scenes = new();
        private string[] presetValues = { "reference", "setting", "character", "place", "item", "drink", "rule", "history", "service_reference", "coffee_reference", "personal_background", "place_background", "shared_background", "shared_event", "custom", "" };
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
            CafeKnowledgeView.Rect(list.parent.parent, new Vector2(.025f, 0), new Vector2(.32f, 1), new Vector2(0, 106), new Vector2(0, -286));
            typeFilter = FilterControl("KnowledgeTypeFilter", new[] { "类型：全部" }, new[] { "Type: all" }, 0);
            usageFilter = FilterControl("KnowledgeUsageFilter", new[] { "使用：全部", "常驻知识", "按需检索" }, new[] { "Usage: all", "Constant background", "On-demand retrieval" }, 1);
            accessFilter = FilterControl("KnowledgeAccessFilter", new[] { "访问范围：全部", "公共知识", "指定 NPC 知识" }, new[] { "Access: all", "Public knowledge", "Selected NPC knowledge" }, 2);
            npcFilter = FilterControl("KnowledgeNpcFilter", new[] { "NPC 可知：全部 NPC" }, new[] { "Known by: all NPCs" }, 3);
            typeFilter.onValueChanged.AddListener(i => { selectedType = i == 0 ? "" : filterTypes[i-1]; FilterList(); });
            npcFilter.onValueChanged.AddListener(i => { selectedNpc = i == 0 ? "" : filterNpcs[i-1]; FilterList(); });
            usageFilter.onValueChanged.AddListener(_ => FilterList()); accessFilter.onValueChanged.AddListener(_ => FilterList());
            previousPage = view.Button(transform, "KnowledgePreviousPage", "上一页", "Previous", () => { page--; FilterList(false); });
            nextPage = view.Button(transform, "KnowledgeNextPage", "下一页", "Next", () => { page++; FilterList(false); });
            pageText = view.Text(transform, "KnowledgePage", "", "", 16);
            CafeKnowledgeView.Rect(previousPage.transform, new Vector2(.025f,0), new Vector2(.115f,0), new Vector2(0,180), new Vector2(0,218));
            CafeKnowledgeView.Rect(nextPage.transform, new Vector2(.23f,0), new Vector2(.32f,0), new Vector2(0,180), new Vector2(0,218));
            CafeKnowledgeView.Rect(pageText.transform, new Vector2(.115f,0), new Vector2(.23f,0), new Vector2(4,180), new Vector2(-4,218));
            pageText.alignment = TextAlignmentOptions.Center;
            listHint = view.Text(list, "KnowledgeListHint", "正在加载…", "Loading…", 18); CafeKnowledgeView.Height(listHint.gameObject, 80);
            reload = view.Button(transform, "ReloadKnowledge", "重新加载", "Reload", () => RequestLeave(() => StartCoroutine(LoadCatalog())));
            CafeKnowledgeView.Rect(reload.transform, new Vector2(.025f, 0), new Vector2(.32f, 0), new Vector2(0, 28), new Vector2(0, 80));
            form = view.Scroll(transform, "KnowledgeForm");
            CafeKnowledgeView.Rect(form.parent.parent, new Vector2(.35f, 0), new Vector2(.975f, 1), new Vector2(0, 110), new Vector2(0, -76));
            formGroup = form.gameObject.AddComponent<CanvasGroup>();
            Label(form, "知识名称 *", "Knowledge name *"); nameInput = view.Input(form, "KnowledgeName"); CafeKnowledgeView.Height(nameInput.gameObject, 52);
            Label(form, "知识内容 *", "Content *"); contentInput = view.Input(form, "KnowledgeContent", true); CafeKnowledgeView.Height(contentInput.gameObject, 248);
            Label(form, "知识使用模式", "Knowledge usage");
            usageMode = view.Dropdown(form, "KnowledgeUsageMode", new[] { "常驻知识", "按需检索" }, new[] { "Constant background", "On-demand retrieval" });
            CafeKnowledgeView.Height(usageMode.gameObject, 46);
            Label(form, "常驻会完整计入上下文预算，不代表更高权限；按需知识通过检索使用。", "Constant background consumes context budget and grants no extra authority. Retrieval is on demand.", 16, 58);
            Label(form, "知识类型", "Knowledge Type");
            typePreset = view.Dropdown(form, "KnowledgeTypePreset", presetValues.Select(v => v.Length == 0 ? "其他自定义类型" : TypeCaption(v)).ToArray(),
                presetValues.Select(v => v.Length == 0 ? "Other custom type" : TypeCaption(v)).ToArray());
            CafeKnowledgeView.Height(typePreset.gameObject, 44);
            typeInput = view.Input(form, "KnowledgeType"); CafeKnowledgeView.Height(typeInput.gameObject, 48);
            metadata = view.Text(form, "KnowledgeMetadata", "", "", 16); CafeKnowledgeView.Height(metadata.gameObject, 94);
            var expand = view.Button(form, "KnowledgeAdvanced", "高级信息与自定义属性（展开 / 折叠）", "Advanced information & attributes (expand / collapse)", () => advanced.SetActive(!advanced.activeSelf));
            CafeKnowledgeView.Height(expand.gameObject, 46);
            advanced = CafeKnowledgeView.Node("KnowledgeAdvancedContent", form, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            Stack(advanced); advanced.SetActive(false);
            metadata.transform.SetParent(advanced.transform, false);
            Label(advanced.transform, "关键词（每行一个）", "Keywords (one per line)");
            keywordsInput = view.Input(advanced.transform, "KnowledgeKeywords", true); CafeKnowledgeView.Height(keywordsInput.gameObject, 90);
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
            accessMode = view.Dropdown(form, "KnowledgeAccessMode", new[] { "公共知识", "指定 NPC 知识" }, new[] { "Public knowledge", "Selected NPC knowledge" });
            CafeKnowledgeView.Height(accessMode.gameObject, 46);
            Label(form, "两种权限均受可信部署与场景限制；常驻内容也需要通过相同权限校验。", "Both modes obey trusted deployment and scene scope; constant knowledge uses the same access checks.", 16, 58);
            preserveScope = view.Toggle(form, "PreserveKnowledgeScope", "", false);
            preserveScope.onValueChanged.AddListener(value =>
            {
                if (loadingControls) return;
                if (value) { RefreshScopeControls(); Changed(); return; }
                preserveScope.SetIsOnWithoutNotify(true);
                Confirm("切换为当前部署的可配置范围？保存后将移除未绑定角色、其他部署与未知场景条件；请重新选择知情范围。",
                    "Use the current deployment scope? Saving removes unbound characters, other deployments and unknown scenes. Review the new scope.",
                    "核对新范围", "Review scope", () => { preserveScope.SetIsOnWithoutNotify(false); RefreshScopeControls(); Changed(); });
            });
            restricted = CafeKnowledgeView.Node("RestrictedKnowledgeScope", form, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)); Stack(restricted);
            scopes = restricted.transform;
            expand.transform.SetAsLastSibling(); advanced.transform.SetAsLastSibling();
            indexText = view.Text(transform, "KnowledgeIndexStatus", "", "", 21);
            CafeKnowledgeView.Rect(indexText.transform, new Vector2(.35f, 0), new Vector2(.975f, 0), new Vector2(0, 82), new Vector2(0, 108));
            status = view.Text(transform, "KnowledgeSaveStatus", "", "", 21);
            CafeKnowledgeView.Rect(status.transform, new Vector2(.35f, 0), Vector2.right, new Vector2(0, 16), new Vector2(-690, 78));
            discard = Footer("DiscardKnowledgeDraft", "放弃修改", "Discard draft", -674, -510, Discard);
            restore = Footer("RestoreKnowledge", "恢复此条模板", "Restore this entry", -500, -324, Restore);
            delete = Footer("DeleteKnowledge", "删除", "Delete", -314, -184, Delete);
            save = Footer("SaveKnowledge", "保存", "Save", -174, -36, () => Save()); SettingsPanelTheme.Button(save, true);
            importCatalog = view.Button(transform, "ImportWorldKnowledge", "导入世界知识", "Import knowledge", ImportWorldKnowledge);
            exportCatalog = view.Button(transform, "ExportWorldKnowledge", "导出世界知识", "Export knowledge", ExportWorldKnowledge);
            restoreAll = view.Button(transform, "RestoreAllWorldKnowledge", "恢复系统默认世界知识", "Restore system knowledge", RestoreSystemKnowledge);
            LayoutPageFooter();
            search.onValueChanged.AddListener(_ => FilterList());
            nameInput.onValueChanged.AddListener(_ => Changed()); contentInput.onValueChanged.AddListener(_ => Changed());
            typeInput.onValueChanged.AddListener(_ => Changed());
            keywordsInput.onValueChanged.AddListener(_ => Changed());
            usageMode.onValueChanged.AddListener(_ => Changed());
            typePreset.onValueChanged.AddListener(i => { if (presetValues[i].Length != 0) typeInput.text = presetValues[i]; RefreshTypeEditor(); });
            relationSearch.onValueChanged.AddListener(_ => RefreshRelationPicker());
            relationPicker.onValueChanged.AddListener(i =>
            {
                if (loadingControls || i == 0 || i > relationOptions.Count) return;
                if (related.Count >= catalog.limits.related_ids) { Error("关联数量达到上限。", "Related knowledge limit reached."); return; }
                related.Add(relationOptions[i - 1]); RenderRelations(); Changed();
            });
            accessMode.onValueChanged.AddListener(_ => { RefreshScopeControls(); Changed(); });
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
            RefreshFilterChoices();
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
            keywordsInput.SetTextWithoutNotify(string.Join("\n", item?.entry.keywords ?? Array.Empty<string>()));
            usageMode.SetValueWithoutNotify(item?.entry.usage == "constant" ? 0 : 1); usageMode.RefreshShownValue();
            related.Clear(); if (item != null) related.AddRange(item.entry.related_ids);
            accessMode.SetValueWithoutNotify(item?.entry.access.mode == "public" ? 0 : 1); accessMode.RefreshShownValue();
            BuildScopes(item); preserveScope.SetIsOnWithoutNotify(unsupportedScope); RefreshScopeControls();
            RenderRelations(); advanced.SetActive(false);
            dirty = false; loadingControls = false; UpdateMetadata(); UpdateListLabels(); UpdateButtons();
            var scroll = form.GetComponentInParent<ScrollRect>(true); scroll.verticalNormalizedPosition = 1;
        }
        public void NewKnowledge() => RequestLeave(() => { if (catalog != null) { LoadItem(null); SetStatus("新建知识：填写名称和内容后保存。", "New knowledge: enter a name and content, then save."); nameInput.Select(); nameInput.ActivateInputField(); } });
        private void Discard() { LoadItem(Selected); SetStatus("已放弃未保存修改。", "Unsaved changes discarded."); }
        private void Changed()
        { if (loadingControls) return; dirty = true; SetStatus("未保存。", "Unsaved."); UpdateMetadata(); UpdateButtons(); }

        private KnowledgeEditView Edit()
        {
            var result = new KnowledgeEditView { expected_revision = editRevision, name = nameInput.text.Trim(), content = contentInput.text.Trim(),
                knowledge_type = string.IsNullOrWhiteSpace(typeInput.text) ? "reference" : typeInput.text.Trim(),
                related_ids = related.ToArray(), access_mode = accessMode.value == 0 ? "public" : "restricted",
                usage = usageMode.value == 0 ? "constant" : "retrieval", source = Selected?.entry.source ?? "custom",
                preserve_access = unsupportedScope && preserveScope.isOn,
                keywords = keywordsInput.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length != 0).ToArray() };
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
            if (result.keywords.Length > catalog.limits.keywords || result.keywords.Distinct().Count() != result.keywords.Length || result.keywords.Any(k => k.Length > catalog.limits.keyword_length) || result.source.Length == 0)
                throw new ArgumentException(SettingsUiLanguage.T("请检查来源与关键词：关键词不能重复或超过上限。", "Check source and keywords: keywords must be unique and within limits."));
            if (!result.preserve_access)
            {
                result.scene_ids = scenes.Where(r => r.toggle.isOn).Select(r => r.id).ToArray();
            }
            if (result.access_mode == "restricted" && !result.preserve_access)
            {
                result.character_ids = roles.Where(r => r.toggle.isOn).Select(r => r.id).ToArray();
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
            Confirm("从当前知识目录删除「" + item.entry.name + "」？系统模板不会自动补回此条目。",
                "Delete “" + item.entry.name + "” from the current catalog? The system template will not add it back automatically.",
                "确认删除", "Delete", () => StartCoroutine(Mutate(done => client.Delete(selectedId, editRevision, done), selectedId)));
        }
        private void Restore()
        {
            if (busy || Selected?.has_default != true) return;
            Confirm("从系统模板恢复「" + Selected.entry.name + "」？当前目录会先备份，未保存修改也将放弃。", "Copy “" + Selected.entry.name + "” from the system template? The current catalog will be backed up; unsaved edits will be discarded.",
                "恢复此条", "Restore entry", () => StartCoroutine(Mutate(done => client.Restore(selectedId, editRevision, done), selectedId)));
        }
        public void ImportWorldKnowledge() => RequestLeave(() =>
        {
            if (catalog == null) return;
            string path = ChooseJson(false); if (string.IsNullOrEmpty(path)) return;
            string json;
            try { json = CafeKnowledgeFiles.Read(path); }
            catch (Exception error) { FileError(error); return; }
            StartCoroutine(PreviewImport(json));
        });
        public void ExportWorldKnowledge() => RequestLeave(() =>
        {
            if (catalog == null) return;
            string path = ChooseJson(true); if (!string.IsNullOrEmpty(path)) StartCoroutine(ExportFile(path));
        });
        public void RestoreSystemKnowledge() => RequestLeave(() =>
        {
            if (catalog == null) return;
            int revision = catalog.revision;
            Confirm("恢复当前安装版本的系统默认世界知识？将完整覆盖当前目录，Backend 会先创建安全备份。此操作不修改 Persona、Unity 场景、Memory 或 Relationship。",
                "Restore this installation's system world knowledge? This fully replaces the current catalog after a safe Backend backup. Persona, Unity scenes, Memory and Relationship stay unchanged.",
                "备份并恢复整库", "Back up & restore all", () => StartCoroutine(Mutate(done => client.RestoreAll(revision, done), null)));
        });
        private string ChooseJson(bool saveFile)
        {
            try { return SelectJsonFile(saveFile); }
            catch (Exception error) { FileError(error); return null; }
        }
        private void FileError(Exception error) => Error("JSON 文件操作失败：" + error.Message, "JSON file operation failed: " + error.Message);
        private IEnumerator ExportFile(string path)
        {
            busy = true; SetStatus("正在导出已保存的完整当前目录…", "Exporting the complete saved current catalog…"); UpdateButtons();
            string json = null, error = null;
            yield return client.Export((data, code) => { json = data; error = code; });
            busy = false; UpdateButtons();
            if (error != null) { ShowError(error); yield break; }
            try { CafeKnowledgeFiles.Write(path, json); SetStatus("已导出完整当前世界知识目录。", "Complete current world knowledge exported."); }
            catch (Exception failure) { FileError(failure); }
        }
        private IEnumerator PreviewImport(string json)
        {
            busy = true; SetStatus("正在校验完整知识包并比较差异…", "Validating the complete package and comparing changes…"); UpdateButtons();
            KnowledgePreviewView preview = null; string error = null;
            yield return client.Preview(json, (data, code) => { preview = data; error = code; });
            busy = false; UpdateButtons();
            if (error != null || preview?.preview_token == null) { ShowError(error); yield break; }
            string counts = SettingsUiLanguage.T("新增 / 修改 / 不变 / 删除：", "Added / modified / unchanged / deleted: ") +
                preview.added.Length + " / " + preview.modified.Length + " / " + preview.unchanged.Length + " / " + preview.deleted.Length;
            string details = PreviewDetails(preview);
            string zh = "完整替换当前世界知识，完成后共 " + preview.replacement_count + " 条；本地 revision " + preview.revision + "。\n" + counts + details +
                "\n提交前会备份当前目录；期间若有并发修改，提交将被拒绝。导入只替换知识，不同步替换 NPC Persona、Unity 场景、Memory、Relationship 或 System Prompt。";
            string en = "Fully replace current world knowledge with " + preview.replacement_count + " entries; local revision " + preview.revision + ".\n" + counts + details +
                "\nBackend backs up before replacing; concurrent changes invalidate this preview. Import replaces knowledge only, not NPC Persona, Unity scenes, Memory, Relationship or System Prompt.";
            Confirm(zh, en, "备份并完整替换", "Back up & replace all",
                () => StartCoroutine(Mutate(done => client.Import(json, preview, done), null)));
        }
        private static string PreviewDetails(KnowledgePreviewView preview)
        {
            var text = new System.Text.StringBuilder();
            void Section(string zh, string en, IEnumerable<string> values)
            {
                string[] all = values.ToArray();
                if (all.Length == 0) return;
                text.Append("\n\n").Append(SettingsUiLanguage.T(zh, en)).Append(":\n").Append(string.Join("\n", all.Take(20)));
                if (all.Length > 20) text.Append(SettingsUiLanguage.T("\n…仅显示前 20 项，共 ", "\n…first 20 shown; total ")).Append(all.Length);
            }
            Section("新增 ID", "Added IDs", preview.added);
            Section("修改 ID 与字段", "Modified IDs and fields", preview.modified.Select(m => m.knowledge_id + ": " + string.Join(", ", m.fields)));
            Section("删除 ID", "Deleted IDs", preview.deleted);
            if (preview.warnings.Length > 0)
            {
                text.Append(SettingsUiLanguage.T("\n\n权限与场景提示（原设置保留，不自动扩大）：", "\n\nScope warnings (preserved without widening):"));
                foreach (var group in preview.warnings.GroupBy(w => w.code))
                {
                    string label = group.Key switch {
                        "UNKNOWN_CHARACTER_IDS" => SettingsUiLanguage.T("未注册的受限 CharacterId", "Unregistered restricted CharacterIds"),
                        "OTHER_DEPLOYMENT_IDS" => SettingsUiLanguage.T("其他部署 ID", "Other deployment IDs"),
                        "OTHER_SCENE_IDS" => SettingsUiLanguage.T("当前场景以外的 ID", "Other scene IDs"),
                        _ => SettingsUiLanguage.T("不适用于当前部署或场景的知识", "Entries not applicable here") };
                    Section(label, label, group.Select(w => w.knowledge_id + ": " + string.Join(", ", w.values)));
                }
            }
            return text.ToString();
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
            CafeKnowledgeView.Rect(box.transform, new Vector2(.12f, .14f), new Vector2(.88f, .86f), Vector2.zero, Vector2.zero);
            SettingsPanelTheme.Surface(box.GetComponent<Image>(), SettingsPanelTheme.Paper);
            dialogZh = zh; dialogEn = en;
            var message = view.Scroll(box.transform, "KnowledgeConfirmationScroll");
            CafeKnowledgeView.Rect(message.parent.parent, Vector2.zero, Vector2.one, new Vector2(20,80), new Vector2(-20,-20));
            dialogText = view.Text(message, "Message", zh, en, 20);
            dialogText.richText = false; dialogText.textWrappingMode = TextWrappingModes.Normal;
            dialogText.gameObject.AddComponent<LayoutElement>().minHeight = 70;
            dialogText.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
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
            if (value == null) { indexText.text = SettingsUiLanguage.T("索引状态暂不可用；按需知识可词法回退。常驻不依赖向量。", "Index status unavailable; retrieval can fall back to lexical. Constant background needs no vectors."); return; }
            indexText.text = SettingsUiLanguage.T("常驻（无需向量）：", "Constant (no vectors): ") + value.constant +
                SettingsUiLanguage.T("；按需向量缓存：", "; retrieval vector cache: ") + value.ready + "/" + value.total +
                (value.mode == "running" ? SettingsUiLanguage.T("；未就绪条目使用词法检索。", "; pending entries use lexical retrieval.") :
                    SettingsUiLanguage.T("；编码不可用 / 暂停，按需使用词法检索。", "; encoding unavailable / paused, using lexical retrieval."));
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
                case "KNOWLEDGE_EDIT_INVALID":
                    Error("JSON 或字段校验失败：请检查 schema_version=2、重复 ID、关联引用、访问范围和使用模式。当前目录未被覆盖。", "Invalid JSON or fields: check schema_version=2, duplicate IDs, references, access and usage. Current catalog was not replaced."); break;
                case "KNOWLEDGE_PREVIEW_INVALID": case "KNOWLEDGE_PREVIEW_REQUIRED":
                    Error("导入预览已失效，请重新预览；当前目录未被覆盖。", "Import preview invalid. Preview again; current catalog was not replaced."); break;
                case "KNOWLEDGE_BACKUP_FAILED": case "KNOWLEDGE_WRITE_FAILED":
                    Error("备份或写入失败，当前目录未被覆盖。请检查磁盘与权限。", "Backup or write failed; current catalog was not replaced. Check disk access."); break;
                case "KNOWLEDGE_INPUT_TOO_LARGE":
                    Error("知识 JSON 超过 16 MiB 文件或存储上限。", "Knowledge JSON exceeds the 16 MiB file or storage limit."); break;
                case "KNOWLEDGE_STORE_BUSY":
                    Error("知识目录正在由另一个进程修改。草稿保留，请稍后重试。", "Another process is updating the catalog. Draft retained; retry shortly."); break;
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
            search.interactable = !busy;
            previousPage.interactable = !busy && page > 0; nextPage.interactable = !busy && (page+1)*PageSize < filteredCount;
            importCatalog.interactable = exportCatalog.interactable = restoreAll.interactable = available;
            foreach (var control in new[] { typeFilter, usageFilter, accessFilter, npcFilter }) control.interactable = !busy;
            RefreshScopeControls();
        }
        private void UpdateMetadata()
        {
            var item = Selected;
            string origin = item == null ? SettingsUiLanguage.T("新建草稿", "New draft") : item.origin == "current" ? SettingsUiLanguage.T("当前世界知识", "Current world knowledge") : !item.has_default ? SettingsUiLanguage.T("自定义知识", "Custom") : item.origin == "override" ? SettingsUiLanguage.T("本地覆盖", "Local override") : SettingsUiLanguage.T("内置知识", "Builtin");
            metadata.text = origin + (dirty ? SettingsUiLanguage.T(" · 未保存", " · Unsaved") : "") + (item?.hidden == true ? SettingsUiLanguage.T(" · 已隐藏", " · Hidden") : "") +
                "\nID: " + (selectedId ?? SettingsUiLanguage.T("保存后由 Backend 生成", "Generated by Backend on save")) +
                SettingsUiLanguage.T("\n目录版本：", "\nRevision: ") + editRevision + (item == null ? "" : SettingsUiLanguage.T(" · 条目版本：", " · Entry version: ") + item.entry.version + " · " + item.entry.updated_at);
        }
        private void FilterList(bool resetPage = true)
        {
            if (list == null) return;
            if (resetPage) page = 0;
            string text = search.text.Trim();
            var matches = (catalog?.items ?? Array.Empty<KnowledgeItemView>()).Where(item =>
            {
                var e = item.entry;
                return (text.Length == 0 || e.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.knowledge_id.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || e.content.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (selectedType.Length == 0 || e.knowledge_type == selectedType) &&
                    (usageFilter.value == 0 || e.usage == (usageFilter.value == 1 ? "constant" : "retrieval")) &&
                    (accessFilter.value == 0 || e.access.mode == (accessFilter.value == 1 ? "public" : "restricted")) &&
                    (selectedNpc.Length == 0 || ((e.access.mode == "public" || e.access.character_ids.Contains(selectedNpc)) &&
                        e.access.deployment_ids.Contains(catalog.scopes.deployment_id) &&
                        (e.access.scene_ids.Length == 0 || e.access.scene_ids.Intersect(catalog.scopes.scene_ids).Any())));
            }).ToArray();
            filteredCount = matches.Length;
            int pages = Math.Max(1, (matches.Length + PageSize - 1) / PageSize); page = Mathf.Clamp(page, 0, pages - 1);
            foreach (var row in listRows) Remove(row.button.gameObject); listRows.Clear();
            foreach (var item in matches.Skip(page * PageSize).Take(PageSize))
            {
                var captured = item;
                var button = view.Button(list, "KnowledgeItem", "", "", () => RequestLeave(() => LoadItem(captured)));
                CafeKnowledgeView.Height(button.gameObject, 84); button.interactable = !busy; listRows.Add((item, button));
            }
            listHint.gameObject.SetActive(matches.Length == 0);
            listHint.text = SettingsUiLanguage.T(catalog == null ? "尚未加载，请连接 Backend 后重试。" : "没有匹配的知识。", catalog == null ? "Not loaded. Connect to the Backend and retry." : "No matching knowledge.");
            pageText.text = (page+1) + "/" + pages + " · " + matches.Length;
            previousPage.interactable = !busy && page > 0; nextPage.interactable = !busy && page+1 < pages;
            UpdateListLabels();
        }
        private TMP_Dropdown FilterControl(string id, string[] zh, string[] en, int row)
        {
            var dropdown = view.Dropdown(transform, id, zh, en);
            CafeKnowledgeView.Rect(dropdown.transform, new Vector2(.025f,1), new Vector2(.32f,1), new Vector2(0,-160-row*38), new Vector2(0,-126-row*38));
            return dropdown;
        }
        private void RefreshFilterChoices()
        {
            if (catalog == null) return;
            filterTypes.Clear(); filterTypes.AddRange(catalog.items.Select(i => i.entry.knowledge_type).Distinct().OrderBy(v => v));
            filterNpcs.Clear(); filterNpcs.AddRange(catalog.scopes.character_ids);
            SetFilterChoices(typeFilter, filterTypes, ref selectedType, SettingsUiLanguage.T("类型：全部", "Type: all"), TypeCaption);
            SetFilterChoices(npcFilter, filterNpcs, ref selectedNpc, SettingsUiLanguage.T("NPC 可知：全部 NPC", "Known by: all NPCs"), RoleCaption);
        }
        private static void SetFilterChoices(TMP_Dropdown control, List<string> values, ref string selected, string all, Func<string, string> caption)
        {
            int index = values.IndexOf(selected)+1; if (index == 0) selected = "";
            control.ClearOptions(); control.AddOptions(new[] { all }.Concat(values.Select(caption)).ToList());
            control.SetValueWithoutNotify(index); control.RefreshShownValue();
        }
        private void UpdateListLabels()
        {
            foreach (var row in listRows)
            {
                row.button.GetComponentInChildren<TMP_Text>().text = row.item.entry.name + "\n" + TypeCaption(row.item.entry.knowledge_type) + " · " + UsageCaption(row.item.entry.usage) + " · " + AccessCaption(row.item.entry.access.mode) + (row.item.hidden ? SettingsUiLanguage.T(" · 已隐藏", " · Hidden") : "");
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
            "coffee_reference" => SettingsUiLanguage.T("咖啡专业知识", "Coffee reference"),
            "personal_background" => SettingsUiLanguage.T("个人背景", "Personal background"),
            "place_background" => SettingsUiLanguage.T("地点背景", "Place background"),
            "shared_background" => SettingsUiLanguage.T("共同背景", "Shared background"),
            "shared_event" => SettingsUiLanguage.T("共同事件", "Shared event"),
            "custom" => SettingsUiLanguage.T("自定义", "Custom"),
            _ => type
        };
        public static string UsageCaption(string usage) => usage == "constant" ? SettingsUiLanguage.T("常驻知识", "Constant background") : SettingsUiLanguage.T("按需检索", "On-demand retrieval");
        public static string AccessCaption(string mode) => mode == "public" ? SettingsUiLanguage.T("公共知识", "Public knowledge") : SettingsUiLanguage.T("指定 NPC 知识", "Selected NPC knowledge");
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
            if (unsupportedScope)
            {
                Label(scopes, "原范围含未绑定角色或其他部署 / 场景。默认保留，可取消勾选后明确调整。", "Scope includes unbound characters or other deployments/scenes. Preserve it, or explicitly review a replacement.", 16, 66);
                string original = "CharacterId: " + string.Join(", ", access.character_ids) + "\nDeployment: " + string.Join(", ", access.deployment_ids) + "\nScene: " + string.Join(", ", access.scene_ids);
                var details = view.Text(scopes, "OriginalKnowledgeScope", original, original, 16); details.richText = false;
                details.textWrappingMode = TextWrappingModes.Normal;
            }
        }
        private void RefreshScopeControls()
        {
            if (preserveScope == null || restricted == null) return;
            preserveScope.transform.parent.gameObject.SetActive(unsupportedScope);
            var label = preserveScope.transform.parent.Find("ScopeLabel")?.GetComponent<TMP_Text>();
            if (label != null) label.text = SettingsUiLanguage.T("保留原访问范围（含未绑定条件）", "Preserve original scope (including unbound conditions)");
            bool preserve = unsupportedScope && preserveScope.isOn;
            accessMode.interactable = !busy && !preserve;
            foreach (var role in roles) { role.toggle.transform.parent.gameObject.SetActive(accessMode.value == 1); role.toggle.interactable = !busy && !preserve; }
            foreach (var scene in scenes) scene.toggle.interactable = !busy && !preserve;
            restricted.SetActive(true);
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
            view.RefreshLanguage();
            int preset = typePreset.value;
            typePreset.ClearOptions();
            typePreset.AddOptions(presetValues.Select(v => v.Length == 0 ? SettingsUiLanguage.T("其他自定义类型", "Other custom type") : TypeCaption(v)).ToList());
            typePreset.SetValueWithoutNotify(preset); typePreset.RefreshShownValue();
            RefreshFilterChoices(); UpdateListLabels(); FilterList(false); RefreshTypeEditor();
            foreach (var role in roles)
            {
                var label = role.toggle.transform.parent.Find("ScopeLabel")?.GetComponent<TMP_Text>();
                if (label != null) label.text = RoleCaption(role.id);
            }
            SettingsPanelTheme.Button(save, true);
            status.text = SettingsUiLanguage.T(statusZh, statusEn);
            if (catalog != null) { UpdateMetadata(); RefreshRelationPicker(); RefreshIndex(catalog.index); }
            RefreshScopeControls();
            if (dialogText != null) dialogText.text = SettingsUiLanguage.T(dialogZh, dialogEn);
        }
        private string RoleCaption(string id)
        {
            var definition = FindObjectsByType<EmbodiedNPC.Cognition.CognitiveContextComposer>(FindObjectsSortMode.None)
                .Select(x => x.CharacterDefinition).FirstOrDefault(x => x != null && x.CharacterId == id);
            return SettingsUiLanguage.NpcDisplayName(id, definition == null ? null :
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
            var transfers=CafeKnowledgeView.Node("KnowledgeTransferActions",footer.transform,typeof(HorizontalLayoutGroup));
            CafeKnowledgeView.Rect(transfers.transform,Vector2.zero,Vector2.right,new Vector2(20,70),new Vector2(-20,110));
            var transferLayout=transfers.GetComponent<HorizontalLayoutGroup>();transferLayout.spacing=12;transferLayout.childControlWidth=transferLayout.childControlHeight=true;transferLayout.childForceExpandWidth=true;
            foreach(var button in new[]{importCatalog,exportCatalog,restoreAll})
            {button.transform.SetParent(transfers.transform,false);var e=button.gameObject.AddComponent<LayoutElement>();e.flexibleWidth=1;}
            status.transform.SetParent(footer.transform,false);
            CafeKnowledgeView.Rect(status.transform,Vector2.zero,Vector2.right,new Vector2(20,110),new Vector2(-20,138));
            status.fontSize=18;status.textWrappingMode=TextWrappingModes.Normal;status.overflowMode=TextOverflowModes.Ellipsis;
            indexText.transform.SetParent(footer.transform,false);
            CafeKnowledgeView.Rect(indexText.transform,Vector2.zero,Vector2.right,new Vector2(20,138),new Vector2(-20,164));
            foreach(var scroll in new[] { list.GetComponentInParent<ScrollRect>(true), form.GetComponentInParent<ScrollRect>(true) })
            {var r=(RectTransform)scroll.transform;r.offsetMin=new Vector2(r.offsetMin.x,scroll.name == "KnowledgeList" ? 228 : 178);}
        }
        private void Label(Transform parent, string zh, string en, float size = 20, float height = 32)
        { var label = view.Text(parent, "KnowledgeLabel", zh, en, size); CafeKnowledgeView.Height(label.gameObject, height); }
        private static Transform Container(Transform parent, string name)
        { var go = CafeKnowledgeView.Node(name, parent, typeof(VerticalLayoutGroup), typeof(ContentSizeFitter)); Stack(go); return go.transform; }
        private static void Stack(GameObject go)
        { var layout = go.GetComponent<VerticalLayoutGroup>(); layout.spacing = 8; layout.childControlHeight = layout.childControlWidth = true; layout.childForceExpandHeight = false; go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize; }
        private static void Remove(GameObject go) { if (Application.isPlaying) { go.SetActive(false); Destroy(go); } else DestroyImmediate(go); }
        private static void Clear(Transform parent) { for (int i = parent.childCount - 1; i >= 0; i--) Remove(parent.GetChild(i).gameObject); }
    }
}
