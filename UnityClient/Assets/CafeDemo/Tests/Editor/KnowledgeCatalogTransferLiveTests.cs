using System;
using System.Collections;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Settings;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace CafeDemo.Tests
{
    /// <summary>Opt-in actual page/HTTP operations in an empty scene and private Fake-Provider Backend.</summary>
    public sealed class KnowledgeCatalogTransferLiveTests
    {
        private static string Root => Environment.GetEnvironmentVariable("EMBODIEDNPC_KNOWLEDGE_UI_TEST_ROOT");
        private CafeWorldKnowledgePanel page;
        private GameObject root;
        private KnowledgeManagementClient client;
        private T Find<T>(string name) where T:Component => page.GetComponentsInChildren<T>(true).Single(x=>x.name==name && (x.name.StartsWith("ConfirmChoice") ? x.gameObject.activeInHierarchy : true));
        private void Click(string name) => Find<Button>(name).onClick.Invoke();
        private static IEnumerator Wait(Func<bool> condition, string description)
        {
            float until=Time.realtimeSinceStartup+30;
            while(!condition() && Time.realtimeSinceStartup<until)yield return null;
            Assert.IsTrue(condition(),description);
        }
        private IEnumerator Saved()
        {
            yield return Wait(()=>!page.IsBusy,"page operation completed");
            Assert.IsFalse(page.IsDirty,Find<TMP_Text>("KnowledgeSaveStatus").text);
        }
        private IEnumerator Catalog(Action<KnowledgeCatalogView,string> done)
        {
            bool completed=false;
            page.StartCoroutine(client.Catalog((data,code)=>{done(data,code);completed=true;}));
            yield return Wait(()=>completed,"catalog transport completed");
        }
        private IEnumerator Capture(string name)
        {
            bool completed=false;
            page.StartCoroutine(CaptureFrame(name,()=>completed=true));
            yield return Wait(()=>completed,"capture current page frame");
        }
        private static IEnumerator CaptureFrame(string name,Action complete)
        {
            yield return null; Canvas.ForceUpdateCanvases(); yield return null;
            var image=ScreenCapture.CaptureScreenshotAsTexture();
            if(image!=null)
            {
                File.WriteAllBytes(Path.Combine(Root,name),image.EncodeToPNG());Object.Destroy(image);
            }
            complete();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if(root!=null)Object.Destroy(root);
            if(Application.isPlaying)yield return new ExitPlayMode();
            string previous = UnityEditor.SessionState.GetString("KnowledgeTransfer.PreviousScene", "");
            if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous);
        }
        [UnitySetUp] public IEnumerator LoadIsolatedPage()
        {
            if(string.IsNullOrEmpty(Root) || !File.Exists(Path.Combine(Root,"session.json")))
                Assert.Ignore("Requires an explicitly isolated Stage 3 Fake-Provider Backend.");
            Assert.IsFalse(EditorSceneManager.GetActiveScene().isDirty, "Never replace an unsaved scene.");
            UnityEditor.SessionState.SetString("KnowledgeTransfer.PreviousScene", EditorSceneManager.GetActiveScene().path);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            yield return new EnterPlayMode();
            var gameType=typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
            var gameView=UnityEditor.EditorWindow.GetWindow(gameType);
            gameView.position=new Rect(0,0,1280,720);gameView.Focus();gameView.Repaint();
            root=new GameObject("Stage3ActualKnowledgePage",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster),typeof(Image));
            root.SetActive(false);root.GetComponent<Image>().color=SettingsPanelTheme.Paper;
            root.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            root.GetComponent<CanvasScaler>().uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            root.GetComponent<CanvasScaler>().referenceResolution=new Vector2(1920,1080);
            page=root.AddComponent<CafeWorldKnowledgePanel>();
            client=new KnowledgeManagementClient(()=>"http://127.0.0.1:18889",Path.Combine(Root,"session.json"));
            var eventSystem=new GameObject("Stage3EventSystem",typeof(EventSystem),typeof(InputSystemUIInputModule));
            eventSystem.transform.SetParent(root.transform,false);
            page.Initialize(TMP_Settings.defaultFontAsset,client);
            try { root.SetActive(true); }
            catch(Exception activation) { Assert.Fail("Page activation failed: " + activation); }
            Debug.Log("Stage3 page activated; awaiting local catalog.");
            yield return Wait(()=>!page.IsBusy && page.SelectedId!=null,"initial full catalog load");
        }
        [UnityTest] public IEnumerator EditModesExportPreviewImportAndRestoreThroughTheActualPage()
        {
            var types = Find<TMP_Dropdown>("KnowledgeTypeFilter");
            types.Show(); yield return null;
            var popup = types.transform.Find("Dropdown List").GetComponent<RectTransform>();
            var popupScroll = popup.GetComponent<ScrollRect>();
            Assert.That(popup.rect.height, Is.GreaterThan(0).And.LessThanOrEqualTo(240));
            Assert.IsTrue(popupScroll.verticalScrollbar.gameObject.activeInHierarchy);
            if (types.options.Count > 20)
            {
                float initial = popupScroll.verticalNormalizedPosition;
                popupScroll.OnScroll(new PointerEventData(EventSystem.current) { scrollDelta = new Vector2(0, -8) });
                yield return null;
                Assert.Less(popupScroll.verticalNormalizedPosition, initial, "mouse wheel moves the long option list");
            }
            var corners = new Vector3[4]; popup.GetWorldCorners(corners);
            var footerCorners = new Vector3[4];
            page.transform.Find("KnowledgeFooter").GetComponent<RectTransform>().GetWorldCorners(footerCorners);
            Assert.GreaterOrEqual(corners[0].y, footerCorners[1].y, "popup stays above the action bar");
            yield return Capture("unity-dropdown-scroll.png"); types.Hide(); yield return null;
            page.NewKnowledge();Find<TMP_InputField>("KnowledgeName").text="Stage3 actual UI knowledge";
            Find<TMP_InputField>("KnowledgeContent").text="Stage3 isolated complete fact.";
            Find<TMP_InputField>("KnowledgeKeywords").text="stage3-alias";
            Assert.IsFalse(page.GetComponentsInChildren<TMP_InputField>(true).Any(f=>f.name=="KnowledgeSource"));
            Find<TMP_Dropdown>("KnowledgeUsageMode").value=0;
            Find<TMP_Dropdown>("KnowledgeAccessMode").value=0;
            page.Save();yield return Saved();string key=page.SelectedId;
            KnowledgeCatalogView catalog=null;string error=null;
            yield return Catalog((data,code)=>{catalog=data;error=code;});Assert.IsNull(error);
            var entry=catalog.items.Single(i=>i.entry.knowledge_id==key).entry;
            Assert.AreEqual("public",entry.access.mode);Assert.AreEqual("constant",entry.usage);
            Assert.AreEqual("custom",entry.source);Assert.IsEmpty(entry.access.character_ids);Assert.AreEqual(new[]{"stage3-alias"},entry.keywords);
            Find<TMP_InputField>("KnowledgeSearch").text=key;
            yield return null;
            Assert.AreEqual(1,page.GetComponentsInChildren<Button>().Count(b=>b.name=="KnowledgeItem"));
            Find<TMP_Dropdown>("KnowledgeUsageMode").value=1;Find<TMP_Dropdown>("KnowledgeAccessMode").value=1;
            var roles=page.GetComponentsInChildren<Toggle>(true).Where(t=>t.name=="KnowledgeRole").ToArray();
            foreach(var role in roles)role.isOn=false;roles[0].isOn=true;
            page.Save();yield return Saved();
            string export=Path.Combine(Root,"unity-export.json");page.SelectJsonFile=save=>save?export:Path.Combine(Root,"unity-import.json");
            page.ExportWorldKnowledge();yield return Wait(()=>!page.IsBusy && File.Exists(export),"export file created");
            var document=JObject.Parse(File.ReadAllText(export));Assert.AreEqual(2,(int)document["schema_version"]);
            var exported=(JObject)document["entries"].Single(e=>(string)e["knowledge_id"]==key);
            Assert.AreEqual("restricted",(string)exported["access"]["mode"]);Assert.AreEqual("retrieval",(string)exported["usage"]);
            document["entries"]=new JArray(exported);File.WriteAllText(Path.Combine(Root,"unity-import.json"),document.ToString());
            Find<TMP_InputField>("KnowledgeContent").text="unsaved draft must survive cancel";
            page.ImportWorldKnowledge();Assert.IsTrue(page.ConfirmationVisible);Click("ConfirmChoice2");
            Assert.IsTrue(page.IsDirty);Assert.AreEqual("unsaved draft must survive cancel",page.DraftContent);
            page.ImportWorldKnowledge();Click("ConfirmChoice1"); // Explicitly discard before preview.
            yield return Wait(()=>page.ConfirmationVisible && !page.IsBusy,"signed difference preview");
            yield return Capture("unity-preview.png");
            Click("ConfirmChoice0");yield return Saved();
            yield return Catalog((data,code)=>{catalog=data;error=code;});Assert.IsNull(error);Assert.AreEqual(1,catalog.items.Length);
            Assert.AreEqual(key,catalog.items[0].entry.knowledge_id);
            Click("DeleteKnowledge"); Click("ConfirmChoice0"); yield return Saved();
            yield return Catalog((data,code)=>{catalog=data;error=code;}); Assert.IsNull(error); Assert.IsEmpty(catalog.items);
            page.RestoreSystemKnowledge();Assert.IsTrue(page.ConfirmationVisible);Click("ConfirmChoice0");yield return Saved();
            yield return Catalog((data,code)=>{catalog=data;error=code;});Assert.IsNull(error);
            Assert.AreEqual(100,catalog.items.Length,"restored installed private test template exactly");
            Assert.IsFalse(catalog.items.Any(i=>i.entry.knowledge_id==key));
            Find<TMP_InputField>("KnowledgeSearch").text="";
            yield return Capture("unity-page.png");
            File.WriteAllText(Path.Combine(Root,"unity-result.json"),new JObject { ["result"]="PASS",["template_entries"]=catalog.items.Length,
                ["search"]="PASS",["edit"]="PASS",["usage_access_switch"]="PASS",["export_import_restore"]="PASS",["draft_guard"]="PASS"}.ToString());
        }
    }
}
