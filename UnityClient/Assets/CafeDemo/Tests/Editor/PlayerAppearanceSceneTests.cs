using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Events;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class PlayerAppearanceSceneTests
    {
        const string S="AppearanceMvp.Test.";
        const string Description="银白色长发，穿浅蓝色外套和黑色短裙。";
        string Root=>SessionState.GetString(S+"root","");
        static string Evidence=>Environment.GetEnvironmentVariable("EMBODIEDNPC_APPEARANCE_EVIDENCE");
        WorldEntity player,observer;
        PlayerVrmAppearance appearance;
        static void Open()
        {
            foreach(string path in new[]{"Assets/VRMCompatibilityTest/Xingmang.vrm","Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
                "Assets/CafeDemo/V2/Characters/薇薇.vrm","Assets/CafeDemo/V2/Characters/Ao.vrm","Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm"})
                if(AssetDatabase.LoadAssetAtPath<GameObject>(path)==null)AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity"); AnimationSceneTestIsolation.Prepare();
            foreach(var driver in Object.FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None))driver.enabled=false;
            foreach(var trace in Object.FindObjectsByType<RuntimeTraceRecorder>(FindObjectsSortMode.None))trace.enabled=false;
        }
        void Bind()
        {
            AnimationSceneTestIsolation.Ready();
            player=Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x=>x.EntityId=="player_01");
            observer=Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x=>x.EntityId=="npc_cafe_01");
            appearance=player.GetComponent<PlayerVrmAppearance>();
            foreach(var routine in Object.FindObjectsByType<EmbodiedNPC.Routines.NpcDefaultRoutineController>(FindObjectsSortMode.None))routine.enabled=false;
            foreach(var driver in Object.FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None))driver.enabled=false;
            var agent=observer.GetComponent<NavMeshAgent>();if(agent!=null)agent.enabled=false;
            player.GetComponent<CafeDemoPlayerController>().enabled=false;
            player.GetComponent<CharacterController>().enabled=false;
            observer.transform.position=new Vector3(1000,0,1000); observer.transform.rotation=Quaternion.identity;
            Place(true);
        }
        void Place(bool visible)
        { player.transform.position=observer.transform.position+(visible?Vector3.forward:Vector3.back)*3;Physics.SyncTransforms(); }
        CognitiveRequest Capture(string name)
        {
            var composer=observer.GetComponent<CognitiveContextComposer>();
            observer.GetComponent<AgentOrchestrator>().CancelCurrentRun();
            Assert.IsTrue(observer.GetComponent<AgentEventReceiver>().Receive(new AgentEvent("appearance_"+name,AgentEventTypes.PlayerUtterance,"player_01",observer.EntityId,"",
                new Dictionary<string,string>{{"text","你能说说我现在的发型和衣服吗？"},{"conversation_id","appearance-isolated-test"}},Time.unscaledTimeAsDouble)));
            Assert.IsNotNull(composer.LastRequest);
            if(!string.IsNullOrEmpty(Evidence))File.WriteAllText(Path.Combine(Evidence,name+"-unity-request.json"),composer.LastRequest.Serialize());
            return composer.LastRequest;
        }
        static string DescriptionOf(CognitiveRequest req)=>req.context.perception.entities.FirstOrDefault(x=>x.entity_id=="player_01")?.appearance_description;
        static IEnumerator Complete(System.Threading.Tasks.Task<bool> task)
        { double end=Time.realtimeSinceStartupAsDouble+75;while(!task.IsCompleted&&Time.realtimeSinceStartupAsDouble<end)yield return null;Assert.IsTrue(task.IsCompleted);Assert.IsTrue(task.Result); }
        [UnitySetUp] public IEnumerator Setup()
        {
            SessionState.SetString(S+"root",Path.Combine(Path.GetTempPath(),"AppearanceMvp-"+Guid.NewGuid().ToString("N")));
            SessionState.SetString(S+"oldRoot",Environment.GetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT")??"");
            SessionState.SetString(S+"oldKey",Environment.GetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY")??"");
            SessionState.SetString(S+"key",S+Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT",Root);
            Environment.SetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY",SessionState.GetString(S+"key",""));
            Open();yield return new EnterPlayMode();yield return null;Bind();
            var host=Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            typeof(CafeDemoDeveloperPanel).GetField("startupTransportReady",flags).SetValue(host,(Func<bool>)(()=>true));
            typeof(CafeDemoDeveloperPanel).GetField("startupGetConfig",flags).SetValue(host,
                (Func<Action<ModelConfigView,string>,int,IEnumerator>)((callback,generation)=>Configured(callback)));
            host.enabled=true;yield return null;host.ApplyStartupConfig(new ModelConfigView{configured=true});host.Close();
        }
        static IEnumerator Configured(Action<ModelConfigView,string> callback)
        { callback(new ModelConfigView{configured=true},null);yield break; }
        static void CaptureSettings(TMP_InputField field)
        {
            if(string.IsNullOrEmpty(Evidence))return;
            var canvas=field.GetComponentInParent<Canvas>().rootCanvas;var oldMode=canvas.renderMode;var oldCamera=canvas.worldCamera;
            var go=new GameObject("Appearance UI evidence camera");var camera=go.AddComponent<Camera>();
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.1f,.12f,.14f);camera.cullingMask=1<<31;
            var parts=canvas.GetComponentsInChildren<Transform>(true);var layers=parts.Select(x=>x.gameObject.layer).ToArray();
            foreach(var part in parts)part.gameObject.layer=31;
            var rt=RenderTexture.GetTemporary(1280,720,24);var image=new Texture2D(1280,720,TextureFormat.RGB24,false);var old=RenderTexture.active;
            try
            {
                camera.targetTexture=rt;canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;
                Canvas.ForceUpdateCanvases();camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();
                File.WriteAllBytes(Path.Combine(Evidence,"appearance-settings.png"),image.EncodeToPNG());
                File.WriteAllText(Path.Combine(Evidence,"appearance-field.json"),Newtonsoft.Json.JsonConvert.SerializeObject(new{
                    value=field.text,text=field.textComponent.text,font=field.textComponent.font.name,cull=field.textComponent.canvasRenderer.cull,
                    position=field.textComponent.rectTransform.anchoredPosition.ToString(),viewport=field.textViewport.rect.ToString(),textRect=field.textComponent.rectTransform.rect.ToString(),
                    characters=field.textComponent.textInfo.characterCount,vertices=field.textComponent.textInfo.meshInfo[0].vertexCount}));
            }
            finally
            { for(int i=0;i<parts.Length;i++)parts[i].gameObject.layer=layers[i];canvas.renderMode=oldMode;canvas.worldCamera=oldCamera;RenderTexture.active=old;camera.targetTexture=null;RenderTexture.ReleaseTemporary(rt);Object.Destroy(image);Object.Destroy(go); }
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if(Application.isPlaying)yield return new ExitPlayMode();
            PlayerPrefs.DeleteKey(SessionState.GetString(S+"key",""));PlayerPrefs.Save();
            string old=SessionState.GetString(S+"oldRoot","");Environment.SetEnvironmentVariable("EMBODIEDNPC_AVATAR_EDITOR_ROOT",old.Length==0?null:old);
            old=SessionState.GetString(S+"oldKey","");Environment.SetEnvironmentVariable("EMBODIEDNPC_PLAYER_NAME_EDITOR_KEY",old.Length==0?null:old);
            if(Root.StartsWith(Path.Combine(Path.GetTempPath(),"AppearanceMvp-"),StringComparison.Ordinal)&&Directory.Exists(Root))Directory.Delete(Root,true);
        }
        [UnityTest] public IEnumerator ExistingHeadVisionSettingsModelSwitchAndRestart()
        {
            Assert.IsNull(DescriptionOf(Capture("unconfigured")));
            Assert.IsTrue(PlayerNameSettings.TrySave("小羽",out _));
            string uid=player.GetComponent<PlayerIdentityBinding>().CurrentIdentity.PlayerUid,id=player.EntityId;
            SessionState.SetString(S+"uid",uid);SessionState.SetString(S+"entity",id);
            var firstAnimator=appearance.CurrentAnimator;
            var originalAnchor=player.GetComponent<PerceivableEntity>().VisualAnchor;
            bool anchorWasHead=originalAnchor==firstAnimator.GetBoneTransform(HumanBodyBones.Head);
            // Exercise the actual ordinary appearance settings Save button.
            var host=Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();host.Open();host.PlayerNavigation.ShowAppearance();yield return null;
            var field=Object.FindObjectsByType<TMP_InputField>(FindObjectsInactive.Include,FindObjectsSortMode.None).Single(x=>x.name=="AppearanceDescriptionInput");
            Assert.AreEqual(TMP_InputField.LineType.MultiLineNewline,field.lineType);
            var appearanceNav=Object.FindObjectsByType<Button>(FindObjectsInactive.Include,FindObjectsSortMode.None).Single(x=>x.name=="AppearanceSettingsNav");
            var customize=appearanceNav.transform.parent.GetComponentsInChildren<TMP_Text>(true).Single(x=>x.text=="内容自定义");
            Assert.Greater(appearanceNav.transform.GetSiblingIndex(),customize.transform.GetSiblingIndex());
            Canvas.ForceUpdateCanvases();Assert.Greater(((RectTransform)field.transform).rect.width,100);Assert.Greater(((RectTransform)field.transform).rect.height,40);
            field.text=string.Join("\n",Enumerable.Repeat("银白色长发。",12));field.ForceLabelUpdate();field.textComponent.ForceMeshUpdate();field.MoveTextStart(false);yield return null;
            Assert.Greater(field.textComponent.preferredHeight,field.textViewport.rect.height);
            float beforeScroll=field.textComponent.rectTransform.anchoredPosition.y;
            field.OnScroll(new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current){scrollDelta=Vector2.down});
            Assert.AreNotEqual(beforeScroll,field.textComponent.rectTransform.anchoredPosition.y,"text scrolls within the input field");
            field.text=Description;field.transform.parent.GetComponentsInChildren<Button>(true).Single(x=>x.name=="SaveAppearanceDescription").onClick.Invoke();
            yield return null;field.ForceLabelUpdate();field.textComponent.ForceMeshUpdate();yield return null;
            Assert.AreEqual(Description,field.text);Assert.AreEqual(Vector2.zero,field.textComponent.rectTransform.anchoredPosition);
            field.GetComponentInParent<ScrollRect>().verticalNormalizedPosition=0;Canvas.ForceUpdateCanvases();CaptureSettings(field);
            Assert.AreEqual(Description,appearance.AppearanceDescription);host.Close();
            var before=Capture("visible");Assert.AreEqual(Description,DescriptionOf(before));string frozen=before.Serialize();
            Assert.IsTrue(observer.GetComponent<VisionSensor>().CurrentVisibleEntityIds.Contains(id));
            Assert.AreSame(firstAnimator,appearance.CurrentAnimator);
            // Head occlusion still uses the existing LOS rule, even with nearby evidence.
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.transform.position=observer.transform.position+new Vector3(0,1.5f,1.5f);wall.transform.localScale=new Vector3(4,3,.2f);Physics.SyncTransforms();
            Assert.IsNull(DescriptionOf(Capture("occluded")));Object.Destroy(wall);yield return null;
            Place(false);Assert.IsNull(DescriptionOf(Capture("hidden")));
            Assert.IsTrue(appearance.SaveAppearanceDescription("黑色短发。"));
            Assert.IsNull(DescriptionOf(Capture("hidden_changed")));
            Place(true);Assert.AreEqual("黑色短发。",DescriptionOf(Capture("updated")));Assert.AreEqual(frozen,before.Serialize());
            Assert.IsTrue(appearance.SaveAppearanceDescription("银白色长发。"));Capture("hair_only");
            Assert.IsTrue(appearance.SaveAppearanceDescription(""));Assert.IsNull(DescriptionOf(Capture("cleared")));
            Assert.IsTrue(appearance.SaveAppearanceDescription(Description));
            yield return Complete(appearance.ImportPathAsync("Assets/CafeDemo/V2/Characters/薇薇.vrm"));
            Assert.IsTrue(appearance.IsCustom);Assert.IsNull(appearance.AppearanceDescription);
            Assert.IsNull(DescriptionOf(Capture("switched")));
            Assert.AreSame(anchorWasHead?appearance.CurrentAnimator.GetBoneTransform(HumanBodyBones.Head):originalAnchor,player.GetComponent<PerceivableEntity>().VisualAnchor);
            Assert.IsTrue(appearance.SaveAppearanceDescription(Description));
            var imported=appearance.CurrentAnimator;
            Assert.IsTrue(PlayerNameSettings.TrySave("91651",out _));Assert.AreSame(imported,appearance.CurrentAnimator);
            Assert.AreEqual(SessionState.GetString(S+"uid",""),player.GetComponent<PlayerIdentityBinding>().CurrentIdentity.PlayerUid);Assert.AreEqual(SessionState.GetString(S+"entity",""),player.EntityId);
            SessionState.SetString(S+"selected",appearance.Store.Selected);
            yield return new ExitPlayMode();Open();yield return new EnterPlayMode();yield return null;Bind();
            double end=Time.realtimeSinceStartupAsDouble+75;while(!appearance.RestorationComplete&&Time.realtimeSinceStartupAsDouble<end)yield return null;
            Assert.IsTrue(appearance.RestorationComplete);Assert.IsTrue(appearance.IsCustom,appearance.Diagnostic);
            Assert.AreEqual(SessionState.GetString(S+"selected",""),appearance.Store.Selected);
            Assert.AreEqual(Description,appearance.AppearanceDescription);Assert.AreEqual(Description,DescriptionOf(Capture("restored")));
            Assert.AreEqual("91651",PlayerNameSettings.DisplayName);Assert.AreEqual(SessionState.GetString(S+"uid",""),player.GetComponent<PlayerIdentityBinding>().CurrentIdentity.PlayerUid);Assert.AreEqual(SessionState.GetString(S+"entity",""),player.EntityId);
            Assert.IsTrue(appearance.RestoreDefault());Assert.IsNull(DescriptionOf(Capture("default_restored")));
        }
    }
}
