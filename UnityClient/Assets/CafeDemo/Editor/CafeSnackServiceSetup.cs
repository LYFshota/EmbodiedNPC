using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditor.Animations;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.World;

public static class CafeSnackServiceSetup
{
    [MenuItem("Tools/CafeDemoV2/Configure snack service")]
    public static void Apply()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit Mode required.");
        var scene=EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
        var actor=UnityEngine.Object.FindObjectsByType<WorldEntity>().Single(x=>x.EntityId=="npc_cafe_01");
        var station=actor.GetComponent<DrinkPreparationCapability>().Station.Entity;
        Transform Anchor(string name,Vector3 position)
        {
            var t=station.transform.Find(name);
            if(t==null) { t=new GameObject(name).transform; t.SetParent(station.transform,false); }
            t.position=position; t.rotation=Quaternion.identity; return t;
        }
        var showcase=Anchor("SnackShowcaseApproach",new Vector3(-.05f,.0025f,4.65f));
        var showcaseHand=Anchor("SnackShowcaseOperation",new Vector3(-.05f,1.13f,3.98f));
        var rack=Anchor("SnackRackApproach",new Vector3(-1.04f,.0025f,4.65f));
        var rackHand=Anchor("SnackRackOperation",new Vector3(-1.04f,1.13f,3.98f));
        var profile=actor.GetComponent<AgentActionCapabilityProfile>();
        profile.Configure(profile.AllowedActionIds.Concat(new[]{"serve_snack"}).Distinct().ToArray());
        var cap=actor.GetComponent<SnackServiceCapability>() ?? actor.gameObject.AddComponent<SnackServiceCapability>();
        cap.Configure(station,showcase,showcaseHand,rack,rackHand);
        var animator=actor.GetComponentsInChildren<Animator>().Single(a=>a.isHuman&&a.gameObject.activeInHierarchy);
        var controller=animator.runtimeAnimatorController as AnimatorController;
        if(controller==null) throw new InvalidOperationException("Existing Humanoid controller required.");
        const string folder="Assets/CafeDemo/Animations/";
        var source=AssetDatabase.LoadAssetAtPath<AnimationClip>(folder+"CafePrepareDrink.anim");
        var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(folder+"CafeServeSnack.anim");
        if(clip==null) { clip=new AnimationClip { name="CafeServeSnack",frameRate=30 }; AssetDatabase.CreateAsset(clip,folder+"CafeServeSnack.anim"); }
        var idle=AssetDatabase.LoadAssetAtPath<AnimationClip>(folder+"ThirdParty/KayKit/Idle_Quiet_Vivi.anim");
        // With Write Defaults off this shared arms mask can retain another state's free-arm pose.
        // Bake the real relaxed idle for both arms, then replace only the right-arm reach curves.
        var idleBindings=AnimationUtility.GetCurveBindings(idle).Where(b=>
            b.propertyName.StartsWith("Left ") || b.propertyName.StartsWith("Right ") ||
            b.propertyName.StartsWith("LeftHand.") || b.propertyName.StartsWith("RightHand.")).ToArray();
        foreach(var binding in idleBindings)
            AnimationUtility.SetEditorCurve(clip,binding,AnimationCurve.Constant(0,2.4f,AnimationUtility.GetEditorCurve(idle,binding).Evaluate(.4f)));
        foreach(var binding in AnimationUtility.GetCurveBindings(source).Where(b=>b.propertyName.StartsWith("Right ")))
        {
            var curve=AnimationUtility.GetEditorCurve(source,binding);
            var idleCurve=AnimationUtility.GetEditorCurve(idle,binding);
            float rest=idleCurve==null?curve.Evaluate(0):idleCurve.Evaluate(.4f);
            var keys=new[]{new Keyframe(0,rest),new Keyframe(.55f,curve.Evaluate(.4f)),
                new Keyframe(1.25f,curve.Evaluate(.4f)),new Keyframe(2.4f,rest)};
            AnimationUtility.SetEditorCurve(clip,binding,new AnimationCurve(keys));
        }
        var settings=AnimationUtility.GetAnimationClipSettings(clip); settings.loopTime=false; AnimationUtility.SetAnimationClipSettings(clip,settings);
        var layer=controller.layers.Single(l=>l.name==DrinkPreparationPresenter.LayerName);
        var state=layer.stateMachine.states.Select(s=>s.state).FirstOrDefault(s=>s.name=="ServeSnack") ?? layer.stateMachine.AddState("ServeSnack");
        state.motion=clip; state.speed=1; state.writeDefaultValues=false;
        EditorUtility.SetDirty(clip); EditorUtility.SetDirty(controller); EditorUtility.SetDirty(profile); EditorUtility.SetDirty(cap);
        AssetDatabase.SaveAssetIfDirty(clip); AssetDatabase.SaveAssetIfDirty(controller);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }
}
