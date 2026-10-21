using System;
using System.Collections;
using System.IO;
using System.Linq;
using EmbodiedNPC.World;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Newtonsoft.Json;
using Object=UnityEngine.Object;

namespace CafeDemo.Tests
{
    public sealed class OpenSourceAnimationCompatibilityTests
    {
        [UnitySetUp] public IEnumerator Prepare()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();yield return new EnterPlayMode();
            AnimationSceneTestIsolation.Ready();yield return null;
        }
        [UnityTearDown] public IEnumerator Finish(){if(Application.isPlaying)yield return new ExitPlayMode();}
        [UnityTest] public IEnumerator FiveActors_FormalControllersAndChineseFont()
        {
            var actors=Object.FindObjectsByType<WorldEntity>().Where(x=>x.EntityId=="player_01"||x.EntityId.StartsWith("npc_cafe_")).ToArray();
            Assert.That(actors.Length,Is.EqualTo(5));
            foreach(var actor in actors)
            {
                var animator=actor.GetComponentsInChildren<Animator>().Single(a=>a.enabled);
                Assert.That(animator.avatar!=null&&animator.avatar.isValid&&animator.avatar.isHuman,Is.True,actor.EntityId);
                Assert.That(animator.applyRootMotion,Is.False,actor.EntityId);
                Assert.That(actor.GetComponentsInChildren<SkinnedMeshRenderer>().Any(r=>r.enabled&&r.sharedMesh!=null),Is.True,actor.EntityId);
                foreach(var bone in new[]{HumanBodyBones.Hips,HumanBodyBones.Head,HumanBodyBones.LeftHand,HumanBodyBones.RightHand,HumanBodyBones.LeftFoot,HumanBodyBones.RightFoot})
                {
                    var t=animator.GetBoneTransform(bone);Assert.That(t,Is.Not.Null,actor.EntityId+" "+bone);
                    Assert.That(float.IsFinite(t.position.x)&&float.IsFinite(t.position.y)&&float.IsFinite(t.position.z),Is.True);
                }
            }
            const string idle="Assets/CafeDemo/Animations/ThirdParty/Quaternius/Idle_Loop.anim";
            var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(idle);
            Assert.That(clip!=null&&clip.isHumanMotion&&clip.isLooping,Is.True);
            Assert.That(clip.events,Is.Empty);Assert.That(AssetDatabase.GetDependencies(idle,true),Is.EqualTo(new[]{idle}));
            var dependencies=AssetDatabase.GetDependencies("Assets/Scenes/CafeDemoV2.unity",true);
            Assert.That(dependencies.Any(p=>p.Contains("Pack_Character_LowPoly")||p.Contains("msyh")||p.Contains("MicrosoftYaHei")),Is.False);
            var fontSource=AssetDatabase.LoadAssetAtPath<Font>("Assets/Fonts/NotoSansCJKsc/NotoSansCJKsc-Regular.otf");
            Assert.That(fontSource,Is.Not.Null);var font=TMP_FontAsset.CreateFontAsset(fontSource);
            try {Assert.That(font.TryAddCharacters("咖啡馆林薇薇沈知意温予安江亦舟玩家记忆任务设置你好，世界！",out string missing),Is.True,missing);}
            finally{Object.Destroy(font);}
            for(int i=0;i<45;i++)yield return null;
            var camera=Camera.main;Assert.That(camera,Is.Not.Null);
            string evidence=Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT");
            if(string.IsNullOrEmpty(evidence))yield break;
            var rt=new RenderTexture(1280,720,24);var tex=new Texture2D(1280,720,TextureFormat.RGB24,false);var previous=camera.targetTexture;var active=RenderTexture.active;
            try {camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;tex.ReadPixels(new Rect(0,0,1280,720),0,0);tex.Apply();File.WriteAllBytes(Path.Combine(evidence,"CafeDemoV2-after-idle.png"),tex.EncodeToPNG());}
            finally{camera.targetTexture=previous;RenderTexture.active=active;Object.Destroy(rt);Object.Destroy(tex);}
            foreach(var actor in actors)
                yield return CaptureHumanoidLoop(actor,clip,evidence);
        }
        static IEnumerator CaptureHumanoidLoop(WorldEntity actor,AnimationClip clip,string evidence)
        {
            var original=actor.GetComponentsInChildren<Animator>().Single(a=>a.enabled);
            var clone=Object.Instantiate(original.gameObject);
            foreach(var behaviour in clone.GetComponentsInChildren<MonoBehaviour>(true))behaviour.enabled=false;
            clone.transform.SetPositionAndRotation(new Vector3(1000,100,1000),Quaternion.identity);
            clone.transform.localScale=original.transform.lossyScale;
            foreach(var t in clone.GetComponentsInChildren<Transform>(true))t.gameObject.layer=31;
            foreach(var renderer in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))renderer.updateWhenOffscreen=true;
            var animator=clone.GetComponent<Animator>();animator.runtimeAnimatorController=null;animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
            var go=new GameObject("IsolatedAnimationEvidenceCamera");var camera=go.AddComponent<Camera>();
            camera.cullingMask=1<<31;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.16f,.18f,.2f);camera.fieldOfView=36;
            var center=clone.transform.position+Vector3.up*.85f;
            camera.transform.position=center+Vector3.forward*3.5f+Vector3.right*.6f;camera.transform.LookAt(center);
            var graph=PlayableGraph.Create("CC0IdleRuntimeEvidence");graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var motion=AnimationClipPlayable.Create(graph,clip);AnimationPlayableOutput.Create(graph,"Humanoid",animator).SetSourcePlayable(motion);graph.Play();
            var feet=new System.Collections.Generic.List<object>();var initial=clone.transform.position;
            try
            {
                for(int i=0;i<=120;i++)
                {
                    motion.SetTime(clip.length*i/60.0);graph.Evaluate(0);
                    feet.Add(new{sample=i,left=animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.ToString("F6"),right=animator.GetBoneTransform(HumanBodyBones.RightFoot).position.ToString("F6")});
                    if(i%30!=0)continue;
                    yield return null; // Let skinning update in an actual player frame before rendering.
                    var rt=new RenderTexture(640,800,24);var image=new Texture2D(640,800,TextureFormat.RGB24,false);var active=RenderTexture.active;
                    try{camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,640,800),0,0);image.Apply();File.WriteAllBytes(Path.Combine(evidence,actor.EntityId+"-runtime-idle-"+i+".png"),image.EncodeToPNG());}
                    finally{camera.targetTexture=null;RenderTexture.active=active;Object.Destroy(rt);Object.Destroy(image);}
                }
                Assert.That(Vector3.Distance(initial,clone.transform.position),Is.LessThan(.0001f),actor.EntityId);
                File.WriteAllText(Path.Combine(evidence,actor.EntityId+"-runtime-idle.json"),JsonConvert.SerializeObject(new{clip=clip.name,seconds=clip.length*2,actorRootDrift=Vector3.Distance(initial,clone.transform.position),feet},Formatting.Indented));
            }
            finally{graph.Destroy();Object.Destroy(clone);Object.Destroy(go);}
        }
    }
}
