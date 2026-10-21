using System;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Events;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Targeted authoring for CafeDemoV2 P1. No runtime identity or seat manager.</summary>
public static class CafeV2NpcP1Setup
{
    const string Scene = "Assets/Scenes/CafeDemoV2.unity";
    const string Characters = "Assets/CafeDemo/V2/Characters";
    const string Sitting = "Assets/CafeDemo/Animations/ThirdParty/Quaternius/Sitting_Idle_Loop.anim";
    const string SeatedController = "Assets/CafeDemo/Animations/CafeV2Seated.controller";
    const string StandingController = "Assets/CafeDemo/Animations/CafeV2Expressive.controller";

    [MenuItem("Tools/CafeDemoV2/Configure four NPCs (P1)")]
    public static void Apply()
    {
        if (Application.isPlaying || EditorSceneManager.GetActiveScene().path != Scene)
            throw new InvalidOperationException("Open CafeDemoV2 in Edit Mode first.");
        var merchant = GameObject.Find("CafeDemo/Navigation/NavMeshTestAgent");
        var customer = GameObject.Find("CafeDemo/Navigation/RegularCustomer");
        if (!merchant || !customer || GameObject.Find("CafeDemo/Navigation/CompanionCustomer"))
            throw new InvalidOperationException("Expected the original Merchant/RegularCustomer and no P1 copies.");
        // Complete the import gate before changing the scene.
        foreach (var model in new[] {Characters+"/薇薇.vrm", Characters+"/Ao.vrm", Characters+"/桜田 史利矢.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm"})
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(model);
            var animator = prefab ? prefab.GetComponentInChildren<Animator>(true) : null;
            if (!animator || !animator.avatar || !animator.avatar.isHuman || !animator.avatar.isValid)
                throw new InvalidOperationException("Import/Avatar blocked: " + model);
        }
        var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(Sitting);
        var standing = AssetDatabase.LoadAssetAtPath<AnimatorController>(StandingController);
        if (!idle || !idle.humanMotion || !idle.isLooping || !standing)
            throw new InvalidOperationException("Validated sitting clip and standing controller are required.");
        var seated = CreateSeatedController(standing, idle);
        // V2 definitions keep the original persistent IDs but do not rewrite canonical CafeDemo defaults.
        var merchantDefinition = Persona("CafeV2MerchantCharacter", "cafe.merchant", "林薇薇",
            "22岁的咖啡馆店员，负责接待顾客与店内日常服务。",
            "在一家温馨的小咖啡馆工作，喜欢咖啡和甜点，对常来的客人比较熟悉。沈知意和温予安经常一起光顾，她已经认识她们一段时间。",
            "热情、开朗、细心、亲切。喜欢让顾客感到放松，也会适当地开玩笑。",
            "自然活泼、友好、简洁，不机械重复欢迎语，不每次都主动推销咖啡。",
            "不强制每句话提及身份。不编造未发生过的玩家互动或共同记忆。人物关系背景不代表真实 Memory/Relationship 历史。不声称完成尚未发生的动作；不懂就承认，不假装具有不存在的能力。");
        var aDefinition = Persona("CafeV2RegularCustomerCharacter", "cafe.regular_customer", "沈知意",
            "14岁的沈家大小姐，仍在学习阶段；咖啡馆的普通顾客，不属于店内工作人员。",
            "出身家境优渥的沈家，从小接受严格的家庭教育。经常由温予安陪同来到林薇薇工作的咖啡馆。她喜欢咖啡馆轻松自在的氛围，因为在这里不必时刻保持大小姐的仪态。",
            "有教养、好奇、有主见，也有符合14岁年龄的好胜与孩子气，犯错后可以逐渐反省。",
            "礼貌自然，偶尔有些任性，但不傲慢。对熟悉的人更放松。称呼温予安为“予安”，林薇薇为“薇薇”。",
            CustomerBoundaries + "不强制大小姐总是表现得高傲；保持14岁未成年人的适龄生活与关系，不成人化或暧昧化。");
        var bDefinition = Persona("CafeV2CompanionCustomerCharacter", "cafe.companion_customer", "温予安",
            "26岁的沈家随行女仆，长期陪伴14岁的沈知意；在咖啡馆是普通顾客，不是咖啡馆工作人员。",
            "长期陪伴沈知意，负责照顾她的日常生活，两人拥有深厚的信任关系。来到咖啡馆是陪同沈知意休息，不承担咖啡馆服务工作。",
            "沉稳、温柔、认真、细致，有独立判断能力，不会无条件附和沈知意。",
            "有礼貌，表达清晰，日常语气柔和，与沈知意独处时更轻松。通常称沈知意为“小姐”，私下亲近时可以称“知意”；称林薇薇为“薇薇”。",
            CustomerBoundaries + "不每句话都使用刻板的敬语，不无条件附和。");
        var cDefinition = Persona("CafeV2QuietCustomerCharacter", "cafe.quiet_customer", "江亦舟",
            "21岁的普通咖啡馆常客，不属于店内工作人员。",
            "喜欢一个人到咖啡馆看书、休息或思考。享受安静环境，也愿意与主动搭话的人交流。",
            "温和、随和、慢热，善于倾听。",
            "自然、简洁、略带幽默，不故作深沉。",
            CustomerBoundaries);
        var b = UnityEngine.Object.Instantiate(customer, customer.transform.parent);
        b.name = "CompanionCustomer"; Undo.RegisterCreatedObjectUndo(b,"Add companion customer");
        var c = UnityEngine.Object.Instantiate(customer, customer.transform.parent);
        c.name = "QuietCustomer"; Undo.RegisterCreatedObjectUndo(c,"Add quiet customer");
        NewIdentity(b,"npc_cafe_companion_01","character.npc.companion_customer","cafe.companion_customer.001");
        NewIdentity(c,"npc_cafe_quiet_01","character.npc.quiet_customer","cafe.quiet_customer.001");
        Set(merchant.GetComponent<CognitiveContextComposer>(),"characterDefinition",merchantDefinition);
        Set(customer.GetComponent<CognitiveContextComposer>(),"characterDefinition",aDefinition);
        Set(b.GetComponent<CognitiveContextComposer>(),"characterDefinition",bDefinition);
        Set(c.GetComponent<CognitiveContextComposer>(),"characterDefinition",cDefinition);
        ReplaceModel(merchant,Characters+"/薇薇.vrm",standing,1.65f);
        Seat(customer,"Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",seated,new Vector3(-4.426427f,.475784f,.067218f),90,1.575f,"Table1_A");
        Seat(b,Characters+"/Ao.vrm",seated,new Vector3(-2.931287f,.475866f,.090797f),270,1.65f,"Table1_B");
        Seat(c,Characters+"/桜田 史利矢.vrm",seated,new Vector3(.722806f,.478984f,1.988254f),180,1.75f,"Table2_C");
        var interactor=UnityEngine.Object.FindAnyObjectByType<PlayerDialogueInteractor>();
        var so=new SerializedObject(interactor);var candidates=so.FindProperty("candidates");candidates.arraySize=4;
        var actors=new[]{merchant,customer,b,c};
        for(int i=0;i<4;i++)candidates.GetArrayElementAtIndex(i).objectReferenceValue=actors[i].GetComponent<DialogueParticipant>();
        so.ApplyModifiedProperties();
        var router=UnityEngine.Object.FindAnyObjectByType<AgentEventRouter>();
        var routes=new SerializedObject(router);var receivers=routes.FindProperty("receivers");
        int previousCount=receivers.arraySize;receivers.arraySize=previousCount+2;
        receivers.GetArrayElementAtIndex(previousCount).objectReferenceValue=b.GetComponent<AgentEventReceiver>();
        receivers.GetArrayElementAtIndex(previousCount+1).objectReferenceValue=c.GetComponent<AgentEventReceiver>();
        routes.ApplyModifiedProperties();
        var background=GameObject.Find("CafeDemo/Navigation/BackgroundNpc");
        if(background){Undo.RecordObject(background,"Disable background test actor");background.SetActive(false);}
        AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(merchant.scene);
        EditorSceneManager.SaveScene(merchant.scene);
    }
    const string CustomerBoundaries = "不负责咖啡馆制作、拿取或递送饮品，没有店员服务权限。不强制每句话提及身份。不编造未发生的玩家互动记忆；人物关系背景与真实 Memory/Relationship 历史分离。不使用硬编码对话回复，不知道的信息自然承认。";
    static CharacterDefinition Persona(string assetName,string id,string name,string role,string history,string traits,string style,string limits)
    {
        var folder=Characters+"/Personas"; if(!AssetDatabase.IsValidFolder(folder))AssetDatabase.CreateFolder(Characters,"Personas");
        var path=folder+"/"+assetName+".asset";
        if(AssetDatabase.LoadAssetAtPath<CharacterDefinition>(path))throw new InvalidOperationException("Persona already exists: "+path);
        var definition=ScriptableObject.CreateInstance<CharacterDefinition>();definition.name=assetName;
        definition.Configure(id,name,role,history,traits,style,limits);
        if(!definition.TryValidateConfiguration(out var error))throw new InvalidOperationException(error);
        AssetDatabase.CreateAsset(definition,path);return definition;
    }
    static AnimatorController CreateSeatedController(AnimatorController source,AnimationClip idle)
    {
        if(!AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source),SeatedController))throw new InvalidOperationException("Cannot create seated controller");
        var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(SeatedController);
        var layers=controller.layers;layers[0].iKPass=true;controller.layers=layers;
        var machine=controller.layers[0].stateMachine;
        foreach(var state in machine.states.ToArray())machine.RemoveState(state.state);
        var seated=machine.AddState("SeatedIdle");seated.motion=idle;machine.defaultState=seated;
        EditorUtility.SetDirty(controller);return controller;
    }
    static void NewIdentity(GameObject actor,string entityId,string definitionId,string persistentId)
    {
        var so=new SerializedObject(actor.GetComponent<WorldEntity>());
        so.FindProperty("entityId").stringValue=entityId;so.FindProperty("definitionId").stringValue=definitionId;
        so.FindProperty("persistentIdentity.persistentId").stringValue=persistentId;so.ApplyModifiedProperties();
    }
    static Animator ReplaceModel(GameObject actor,string path,RuntimeAnimatorController controller,float height)
    {
        var visual=actor.transform.Find("Visual");if(!visual)throw new InvalidOperationException("Missing Visual on "+actor.name);
        var old=visual.GetComponentsInChildren<Animator>(true);
        foreach(var previous in old)previous.gameObject.SetActive(false);
        var model=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path),visual);
        model.name=Path.GetFileNameWithoutExtension(path);model.transform.localPosition=Vector3.zero;model.transform.localRotation=Quaternion.identity;
        var animator=model.GetComponentInChildren<Animator>();
        float scale=height/CafeDemoCharacterSetup.Measure(model).size.y;model.transform.localScale=Vector3.one*scale;
        animator.runtimeAnimatorController=controller;animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind();animator.Update(0);
        model.transform.localPosition=Vector3.up*(-CafeDemoCharacterSetup.Measure(model).min.y*scale);
        // Replace all references to the previous model Animator within this actor, preserving shared services.
        foreach(var component in actor.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if(!component || component.transform.IsChildOf(model.transform))continue;
            var so=new SerializedObject(component);var prop=so.GetIterator();
            while(prop.Next(true))if(prop.propertyType==SerializedPropertyType.ObjectReference && old.Any(x=>x && prop.objectReferenceValue==x))prop.objectReferenceValue=animator;
            so.ApplyModifiedProperties();
        }
        Set(actor.GetComponent<GestureController>(),"waveController",controller);
        PrefabUtility.RecordPrefabInstancePropertyModifications(model.transform);PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
        var bubble=actor.transform.Find("DialogueBubbleAnchor");
        if(bubble)bubble.position=animator.GetBoneTransform(HumanBodyBones.Head).position+Vector3.up*.25f;
        return animator;
    }
    static void Seat(GameObject actor,string modelPath,RuntimeAnimatorController controller,Vector3 surface,float yaw,float height,string label)
    {
        Undo.RecordObject(actor.transform,"Align seated customer");actor.transform.SetPositionAndRotation(new Vector3(surface.x,.03f,surface.z),Quaternion.Euler(0,yaw,0));
        var animator=ReplaceModel(actor,modelPath,controller,height);animator.Update(.5f);
        var hips=animator.GetBoneTransform(HumanBodyBones.Hips);
        // The hip joint is inside the pelvis; seat surface is the underside of the body, not the joint.
        var target=surface+Vector3.up*.075f;
        animator.transform.position+=target-hips.position;
        PrefabUtility.RecordPrefabInstancePropertyModifications(animator.transform);
        // Ankle targets use the model's own T-pose sole clearance, so different leg lengths stay on the floor.
        var source=AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        var probe=UnityEngine.Object.Instantiate(source);
        float leftClearance,rightClearance;
        try{
            var pa=probe.GetComponentInChildren<Animator>();var bounds=CafeDemoCharacterSetup.Measure(probe);
            leftClearance=(pa.GetBoneTransform(HumanBodyBones.LeftFoot).position.y-bounds.min.y)*animator.transform.localScale.y;
            rightClearance=(pa.GetBoneTransform(HumanBodyBones.RightFoot).position.y-bounds.min.y)*animator.transform.localScale.y;
        }finally{UnityEngine.Object.DestroyImmediate(probe);}
        var leftKnee=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position;
        var rightKnee=animator.GetBoneTransform(HumanBodyBones.RightLowerLeg).position;
        var left=FootTarget(leftKnee,animator.GetBoneTransform(HumanBodyBones.LeftFoot).position,.015f+leftClearance,actor.transform.forward);
        var right=FootTarget(rightKnee,animator.GetBoneTransform(HumanBodyBones.RightFoot).position,.015f+rightClearance,actor.transform.forward);
        var feet=Undo.AddComponent<CafeSeatedFootPose>(animator.gameObject);
        feet.Configure(animator,animator.transform.InverseTransformPoint(left),animator.transform.InverseTransformPoint(right),animator.transform.InverseTransformPoint(leftKnee),animator.transform.InverseTransformPoint(rightKnee));EditorUtility.SetDirty(feet);
        var visual=actor.transform.Find("Visual");Set(visual.GetComponent<FacingController>(),"maxVisualYawDegrees",0f);
        var bubble=actor.transform.Find("DialogueBubbleAnchor");
        if(bubble)bubble.position=animator.GetBoneTransform(HumanBodyBones.Head).position+Vector3.up*.25f;
        var vision=actor.GetComponent<VisionSensor>();if(vision)Set(vision,"eyeHeight",animator.GetBoneTransform(HumanBodyBones.Head).position.y-actor.transform.position.y);
        var capsule=actor.GetComponent<CapsuleCollider>();if(capsule){Undo.RecordObject(capsule,"Adjust seated presentation collider");capsule.height=1.2f;capsule.center=new Vector3(0,.6f,0);capsule.radius=.24f;}
        var anchor=new GameObject("SeatAnchor_"+label);Undo.RegisterCreatedObjectUndo(anchor,"Add seat alignment reference");
        anchor.transform.SetParent(GameObject.Find("Presentation").transform);anchor.transform.SetPositionAndRotation(surface,Quaternion.Euler(0,yaw,0));
    }
    static Vector3 FootTarget(Vector3 knee,Vector3 foot,float floorAnkleY,Vector3 forward)
    {
        // Keep the sampled knee height; a long shin needs the foot farther under the table, not a lifted knee.
        float shin=Vector3.Distance(knee,foot),vertical=knee.y-floorAnkleY;
        float reach=Mathf.Sqrt(Mathf.Max(0,shin*shin-vertical*vertical));
        var target=knee+forward*reach;target.y=floorAnkleY;return target;
    }
    static void Set(UnityEngine.Object target,string field,UnityEngine.Object value)
    {var so=new SerializedObject(target);var p=so.FindProperty(field);if(p==null)throw new InvalidOperationException(target.name+" missing "+field);p.objectReferenceValue=value;so.ApplyModifiedProperties();}
    static void Set(UnityEngine.Object target,string field,float value)
    {var so=new SerializedObject(target);var p=so.FindProperty(field);if(p==null)throw new InvalidOperationException(target.name+" missing "+field);p.floatValue=value;so.ApplyModifiedProperties();}
}
