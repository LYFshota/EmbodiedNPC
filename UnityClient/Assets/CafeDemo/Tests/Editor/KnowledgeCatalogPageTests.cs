using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CafeDemo;
using EmbodiedNPC.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Only the existing page, no scene replacement, Backend or model calls.</summary>
    public sealed class KnowledgeCatalogPageTests
    {
        private GameObject root;
        private bool wasEnglish;
        private CafeWorldKnowledgePanel page;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private T Find<T>(string name) where T : Component => page.GetComponentsInChildren<T>(true).Single(x => x.name == name);
        private void Click(string name) => Find<Button>(name).onClick.Invoke();
        private void Apply(KnowledgeCatalogView catalog) => typeof(CafeWorldKnowledgePanel).GetMethod("ApplyCatalog", Private).Invoke(page, new object[] { catalog, null });
        private KnowledgeEditView Draft() => (KnowledgeEditView)typeof(CafeWorldKnowledgePanel).GetMethod("Edit", Private).Invoke(page, null);
        private Button[] Rows => page.GetComponentsInChildren<Button>(true).Where(x => x.name == "KnowledgeItem").ToArray();

        [SetUp] public void Setup()
        {
            wasEnglish = SettingsUiLanguage.IsEnglish;
            root = new GameObject("KnowledgeStage3", typeof(RectTransform), typeof(Canvas)); root.SetActive(false);
            page = root.AddComponent<CafeWorldKnowledgePanel>();
            page.Initialize(TMP_Settings.defaultFontAsset, new KnowledgeManagementClient(() => "http://127.0.0.1:1"));
        }
        [TearDown] public void Cleanup() { Object.DestroyImmediate(root); SettingsUiLanguage.SetEnglish(wasEnglish); }

        [Test] public void LocalizedTypesAndNpcCaptionsKeepProtocolSelectionsAndDrafts()
        {
            SettingsUiLanguage.SetEnglish(false);
            var data = Catalog(3);
            data.scopes.character_ids = new[] { "cafe.merchant", "cafe.regular_customer", "cafe.companion_customer", "cafe.quiet_customer", "new.character" };
            data.items[0].entry.knowledge_type = "coffee_reference";
            data.items[1].entry.knowledge_type = "personal_background";
            data.items[2].entry.knowledge_type = "玩家自定类型";
            Apply(data);
            var types = Find<TMP_Dropdown>("KnowledgeTypeFilter");
            Assert.That(types.options.Select(o => o.text), Does.Contain("咖啡专业知识"));
            Assert.AreEqual("咖啡专业知识", Find<TMP_Dropdown>("KnowledgeTypePreset").captionText.text);
            Assert.That(Find<TMP_Dropdown>("KnowledgeNpcFilter").options.Select(o => o.text),
                Is.EqualTo(new[] { "NPC 可知：全部 NPC", "林薇薇", "沈知意", "温予安", "江亦舟", "new.character" }));
            types.value = 1;
            Find<TMP_InputField>("KnowledgeName").text = "保留草稿";
            SettingsUiLanguage.SetEnglish(true);
            typeof(CafeWorldKnowledgePanel).GetMethod("RefreshLanguage", Private).Invoke(page, null);
            Assert.AreEqual(1, types.value);
            Assert.AreEqual("Coffee reference", types.captionText.text);
            Assert.That(types.options.Select(o => o.text), Does.Contain("玩家自定类型"));
            Assert.AreEqual("coffee_reference", Draft().knowledge_type);
            Assert.AreEqual("保留草稿", Draft().name);
            Assert.AreEqual("Lin Weiwei", Find<TMP_Dropdown>("KnowledgeNpcFilter").options[1].text);
            Assert.IsFalse(page.GetComponentsInChildren<TMP_InputField>(true).Any(x => x.name == "KnowledgeSource"));
            Assert.That(Find<TMP_Text>("KnowledgeMetadata").text, Does.Not.Contain("source.a"));
            Assert.That(Rows[0].GetComponentInChildren<TMP_Text>().text, Does.Contain("Constant background").And.Contain("Public knowledge"));
        }

        [Test] public void AccessAndNpcFiltersIntersectTrustedScope()
        {
            var data = Catalog();
            data.items[2].entry.access.deployment_ids = new[] { "other" };
            data.items[3].entry.access.character_ids = new[] { "character.1" };
            data.items[4].entry.access.scene_ids = new[] { "other.scene" };
            data.items[5].entry.access.deployment_ids = new[] { "other" };
            Apply(data);
            Find<TMP_Dropdown>("KnowledgeNpcFilter").value = 1;
            Assert.AreEqual(2, Rows.Length, "public plus the selected NPC's explicitly restricted record");
            Find<TMP_Dropdown>("KnowledgeAccessFilter").value = 1;
            Assert.AreEqual(1, Rows.Length); Assert.That(Rows[0].GetComponentInChildren<TMP_Text>().text, Does.StartWith("Name 0"));
            Find<TMP_Dropdown>("KnowledgeAccessFilter").value = 2;
            Assert.AreEqual(1, Rows.Length); Assert.That(Rows[0].GetComponentInChildren<TMP_Text>().text, Does.StartWith("Name 1"));
            Assert.AreEqual("character.0", data.items[1].entry.access.character_ids.Single());
        }

        [Test] public void ManyTypesUseBoundedScrollableTemplatesWithoutFooterOffsets()
        {
            var data = Catalog(80);
            for (int i = 0; i < data.items.Length; i++) data.items[i].entry.knowledge_type = "custom.type." + i;
            Apply(data);
            Assert.AreEqual(81, Find<TMP_Dropdown>("KnowledgeTypeFilter").options.Count);
            foreach (var dropdown in page.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                Assert.That(dropdown.template.rect.height, Is.GreaterThan(0).And.LessThanOrEqualTo(240), dropdown.name);
                var scroll = dropdown.template.GetComponent<ScrollRect>();
                Assert.IsTrue(scroll.vertical); Assert.IsFalse(scroll.horizontal);
                Assert.Greater(scroll.scrollSensitivity, 0);
                Assert.AreEqual(ScrollRect.ScrollbarVisibility.Permanent, scroll.verticalScrollbarVisibility);
                Assert.IsTrue(scroll.verticalScrollbar.gameObject.activeSelf);
                var thumb = scroll.verticalScrollbar.handleRect.GetComponent<Image>();
                Assert.AreEqual(SettingsPanelTheme.Muted, thumb.color, "thumb is visible without hover");
                Assert.AreEqual(SettingsPanelTheme.Row, scroll.verticalScrollbar.GetComponent<Image>().color);
                Assert.AreEqual(Selectable.Transition.None, scroll.verticalScrollbar.transition);
            }
        }

        private static KnowledgeCatalogView Catalog(int count = 6, int roles = 6)
        {
            var characterIds = Enumerable.Range(0, roles).Select(i => "character." + i).ToArray();
            return new KnowledgeCatalogView { schema_version = 2, storage_mode = "current", revision = 3,
                scopes = new KnowledgeScopesView { deployment_id = "test", character_ids = characterIds },
                limits = new KnowledgeLimitsView { name=160, content=2000, knowledge_type=80, attribute_keys=32,
                    attribute_key_length=80, attributes_chars=2000, related_ids=16, source=240, keywords=32, keyword_length=120 },
                index = new KnowledgeIndexView { mode="disabled", total=count },
                items = Enumerable.Range(0, count).Select(i => new KnowledgeItemView {
                    origin="current", entry = new KnowledgeEntryView { knowledge_id="knowledge."+i, name="Name "+i,
                        content="Body "+i, knowledge_type=i%2==0?"reference":"history", source=i%2==0?"source.a":"source.b",
                        usage=i%2==0?"constant":"retrieval", version=1,
                        access = new KnowledgeAccessView { mode=i%2==0?"public":"restricted",
                            character_ids=i%2==0?Array.Empty<string>():new[]{"character.0"}, deployment_ids=new[]{"test"} } }
                }).ToArray() };
        }

        [Test] public void OldWhitelistStaysRestrictedAndUsageIsEditable()
        {
            foreach (int count in new[] {1,4,6})
            {
                var data=Catalog(1,count); data.items[0].entry.access.mode="restricted";
                data.items[0].entry.access.character_ids=data.scopes.character_ids; data.items[0].entry.usage="retrieval";
                Apply(data);
                Assert.AreEqual(1,Find<TMP_Dropdown>("KnowledgeAccessMode").value);
                Assert.AreEqual(count,page.GetComponentsInChildren<Toggle>(true).Count(t=>t.name=="KnowledgeRole"));
                Assert.AreEqual("restricted",Draft().access_mode);
                Find<TMP_Dropdown>("KnowledgeUsageMode").value=0;
                Assert.AreEqual("constant",Draft().usage);
                Find<TMP_Dropdown>("KnowledgeAccessMode").value=0;
                Assert.AreEqual("public",Draft().access_mode);
                Assert.IsEmpty(Draft().character_ids);
                Assert.IsTrue(page.IsDirty);
            }
        }

        [Test] public void SearchFiltersAndPaginationKeepTheDraft()
        {
            Apply(Catalog(3000)); Assert.AreEqual(40,Rows.Length);
            Click("KnowledgeNextPage"); Assert.AreEqual(40,Rows.Length);
            Assert.That(Rows[0].GetComponentInChildren<TMP_Text>().text,Does.StartWith("Name 40"));
            Find<TMP_InputField>("KnowledgeName").text="unsaved draft";
            Find<TMP_InputField>("KnowledgeSearch").text="knowledge.2999";
            Assert.AreEqual(1,Rows.Length); Assert.That(Rows[0].GetComponentInChildren<TMP_Text>().text,Does.StartWith("Name 2999"));
            Assert.AreEqual("unsaved draft",page.DraftName); Assert.IsTrue(page.IsDirty);
            Find<TMP_InputField>("KnowledgeSearch").text="Body 2998"; Assert.AreEqual(1,Rows.Length);
            Find<TMP_InputField>("KnowledgeSearch").text="";
            Find<TMP_Dropdown>("KnowledgeUsageFilter").value=1;
            Assert.IsTrue(Rows.All(r=>r.GetComponentInChildren<TMP_Text>().text.Contains(CafeWorldKnowledgePanel.UsageCaption("constant"))));
            Find<TMP_Dropdown>("KnowledgeAccessFilter").value=2; Assert.IsEmpty(Rows);
            Find<TMP_Dropdown>("KnowledgeAccessFilter").value=1;
            Find<TMP_Dropdown>("KnowledgeNpcFilter").value=6; Assert.AreEqual(40,Rows.Length);
            Find<TMP_Dropdown>("KnowledgeTypeFilter").value=1; // history conflicts with constant references
            Assert.IsEmpty(Rows);
            Find<TMP_Dropdown>("KnowledgeTypeFilter").value=0;
            Assert.IsFalse(page.GetComponentsInChildren<TMP_Dropdown>(true).Any(d=>d.name=="KnowledgeSourceFilter"));
        }

        [Test] public void UnknownScopeIsPreservedUntilExplicitReview()
        {
            var data=Catalog(1); data.items[0].entry.access=new KnowledgeAccessView {mode="restricted",
                character_ids=new[]{"not.registered"},deployment_ids=new[]{"other"},scene_ids=new[]{"other.scene"}};
            Apply(data);
            Assert.IsTrue(Draft().preserve_access);
            Assert.IsFalse(Find<TMP_Dropdown>("KnowledgeAccessMode").interactable);
            Find<Toggle>("PreserveKnowledgeScope").isOn=false;
            Assert.IsTrue(page.ConfirmationVisible); Click("ConfirmChoice1");
            Assert.IsTrue(Draft().preserve_access);
            Find<Toggle>("PreserveKnowledgeScope").isOn=false; Click("ConfirmChoice0");
            Assert.IsTrue(Find<TMP_Dropdown>("KnowledgeAccessMode").interactable);
            Assert.IsTrue(page.IsDirty);
        }

        [Test] public void TransfersAndRestoreHonorUnsavedDraftCancellation()
        {
            Apply(Catalog()); Find<TMP_InputField>("KnowledgeContent").text="keep this draft";
            int dialogs=0; page.SelectJsonFile=_=>{dialogs++;return null;};
            foreach(Action action in new Action[]{page.ImportWorldKnowledge,page.ExportWorldKnowledge,page.RestoreSystemKnowledge})
            {
                action(); Assert.IsTrue(page.ConfirmationVisible); Click("ConfirmChoice2");
                Assert.AreEqual("keep this draft",page.DraftContent); Assert.IsTrue(page.IsDirty);
            }
            Assert.AreEqual(0,dialogs);
        }

        [Test] public void LegacyDtoDefaultsAndCompleteMetadataStayIntact()
        {
            var legacy=JsonConvert.DeserializeObject<KnowledgeEntryView>("{\"knowledge_id\":\"old\",\"access\":{\"character_ids\":[\"a\"]}}");
            Assert.AreEqual("restricted",legacy.access.mode);Assert.AreEqual("retrieval",legacy.usage);
            var data=Catalog(1);data.items[0].entry.keywords=new[]{"alias"};Apply(data);
            Assert.AreEqual("source.a",Draft().source);Assert.AreEqual(new[]{"alias"},Draft().keywords);
            Assert.That(Find<TMP_Text>("KnowledgeIndexStatus").text,Does.Not.Contain("缺少 Embedding"));
        }

        [Test] public void Utf8TransferFilesPreserveRawJsonAndRejectOversize()
        {
            string directory=Path.Combine(Path.GetTempPath(),"KnowledgeStage3Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                string path=Path.Combine(directory,"knowledge.json");
                CafeKnowledgeFiles.Write(path,"{\"a\":1,\"a\":2,\"name\":\"知识\"}");
                Assert.AreEqual("{\"a\":1,\"a\":2,\"name\":\"知识\"}",CafeKnowledgeFiles.Read(path));
                CafeKnowledgeFiles.Write(path,"{}");Assert.AreEqual("{}",CafeKnowledgeFiles.Read(path));
                using(var stream=new FileStream(path,FileMode.Create))stream.SetLength(CafeKnowledgeFiles.MaxBytes+1);
                Assert.Throws<IOException>(()=>CafeKnowledgeFiles.Read(path));
            }
            finally
            {
                string resolved=Path.GetFullPath(directory);
                Assert.IsTrue(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(Path.GetFileName(resolved).StartsWith("KnowledgeStage3Test-",StringComparison.Ordinal));
                Directory.Delete(resolved,true); // Verified GUID-owned test folder only.
            }
        }
    }
}
