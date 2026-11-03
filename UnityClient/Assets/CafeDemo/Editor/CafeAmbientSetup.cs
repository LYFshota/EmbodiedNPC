using System;
using System.IO;
using System.Linq;
using CafeDemo;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

public static class CafeAmbientSetup
{
    private const string Animations = "Assets/CafeDemo/Animations/";
    public static void ConfigureJiangDrink()
    {
        Directory.CreateDirectory(Evidence);
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
        var actor = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "npc_cafe_quiet_01");
        var animator = actor.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
        var root = GameObject.Find("DailyBehaviorAnchors").transform;
        var decoration = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(x => x.name == "Cup.001_5_13");
        var cup = root.Find("AmbientQuietCup");
        if (cup == null)
        {
            cup = ((GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CafeDemo/Prefabs/cup-coffee_0.095_0.095_0.126.prefab"), root)).transform;
            cup.name = "AmbientQuietCup";
            foreach (var collider in cup.GetComponentsInChildren<Collider>()) collider.enabled = false;
        }
        // Keep the cup on his existing table, a little closer than the static decoration
        // so the shorter seated avatar does not have to lock its elbow to reach it.
        cup.position = new Vector3(.70f, .7346694f, 1.54f);
        cup.rotation = actor.transform.rotation * Quaternion.Euler(0, 90, 0);
        decoration.gameObject.SetActive(false);
        var table = Anchor(root, "AmbientQuietCupTable", cup.position, cup.rotation);
        var hand = Anchor(actor.transform, "AmbientCupHand", cup.position, cup.rotation);
        var presenter = actor.GetComponent<CafeAmbientPresenter>() ?? actor.gameObject.AddComponent<CafeAmbientPresenter>();
        Set(presenter, "animator", animator, "motion", actor.GetComponent<MotionChannelCoordinator>(),
            "cup", cup, "tableAnchor", table, "handAnchor", hand);
        var clip = CreateDrink(animator, actor.transform, cup.position, presenter.SipTiltDegrees, "CafeSeatedDrinkQuiet", .13f);
        var baseController = animator.runtimeAnimatorController is AnimatorOverrideController prior ? prior.runtimeAnimatorController : animator.runtimeAnimatorController;
        string controllerPath = Animations + "CafeQuietDrink.overrideController";
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(controllerPath);
        if (controller == null) { controller = new AnimatorOverrideController(baseController); AssetDatabase.CreateAsset(controller, controllerPath); }
        controller.runtimeAnimatorController = baseController;
        controller[AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "CafeSeatedDrink.anim")] = clip;
        animator.runtimeAnimatorController = controller;
        var calibration = CalibrateGrip(animator, clip, cup, actor.transform);
        var fields = new SerializedObject(presenter);
        fields.FindProperty("gripPosition").vector3Value = calibration.Item1;
        fields.FindProperty("gripRotation").vector3Value = calibration.Item2;
        fields.ApplyModifiedPropertiesWithoutUndo();
        var admission = actor.GetComponent<NpcDefaultRoutineController>() ?? actor.gameObject.AddComponent<NpcDefaultRoutineController>();
        Set(admission, "owner", actor, "orchestrator", actor.GetComponent<AgentOrchestrator>(),
            "motionChannels", actor.GetComponent<MotionChannelCoordinator>(), "expressive", actor.GetComponent<ExpressiveController>(),
            "dialogue", Object.FindAnyObjectByType<DialogueController>(), "entityRegistry", Object.FindAnyObjectByType<EntityRegistry>());
        var admissionFields = new SerializedObject(admission);
        admissionFields.FindProperty("stationaryAmbientOnly").boolValue = true; admissionFields.ApplyModifiedPropertiesWithoutUndo();
        Configure(actor, presenter, Array.Empty<Transform>());
        // Existing per-actor scheduler samples independently. Jiang takes longer breaks
        // (43.2–87.75 seconds) than Wen (32–65), without a shared drinking clock.
        var daily = new SerializedObject(actor.GetComponent<DailyBehaviorController>());
        daily.FindProperty("ambientIntervalScale").floatValue = 1.35f; daily.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(controller); AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }
    public static void RebuildWipe()
    {
        Directory.CreateDirectory(Evidence);
        EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
        var actor = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "npc_cafe_01");
        CreateWipe(actor.GetComponentsInChildren<Animator>().Single(x => x.isHuman), actor.transform);
        AssetDatabase.SaveAssets();
    }
    // Rebuild only Wen's authored motion and its grip calibration during visual iteration.
    public static void RebuildDrink()
    {
        Directory.CreateDirectory(Evidence);
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
        var actor = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(x => x.EntityId == "npc_cafe_companion_01");
        var animator = actor.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
        var presenter = actor.GetComponent<CafeAmbientPresenter>();
        var cup = presenter.Cup;
        var clip = CreateDrink(animator, actor.transform, cup.position, presenter.SipTiltDegrees);
        var calibration = CalibrateGrip(animator, clip, cup, actor.transform);
        var fields = new SerializedObject(presenter);
        fields.FindProperty("gripPosition").vector3Value = calibration.Item1;
        fields.FindProperty("gripRotation").vector3Value = calibration.Item2;
        fields.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }
    public static string Evidence => Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT") ??
        Path.GetFullPath("../AmbientEvidence");
    [MenuItem("Tools/CafeDemoV2/Configure minimal ambient")]
    public static void Apply()
    {
        Directory.CreateDirectory(Evidence);
        EnsureModels();
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
        var entities = Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None);
        var merchant = entities.Single(x => x.EntityId == "npc_cafe_01");
        var customer = entities.Single(x => x.EntityId == "npc_cafe_companion_01");
        var animator = customer.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
        var root = GameObject.Find("DailyBehaviorAnchors").transform;
        var p0 = Anchor(root, "AmbientCounterHome", new Vector3(.97f, .03f, 5.1f), Quaternion.Euler(0, 180, 0));
        var p1 = Anchor(root, "AmbientCounterWest", new Vector3(-2.08f, .03f, 3.18f), Quaternion.Euler(0, 90, 0));
        var p2 = Anchor(root, "AmbientCounterEast", new Vector3(.82f, .03f, 4.55f), Quaternion.Euler(0, 180, 0));
        var agent = merchant.GetComponent<NavMeshAgent>();
        var path = new NavMeshPath();
        foreach (var point in new[] { p0, p1, p2 })
        {
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            if (!NavMesh.SamplePosition(point.position, out var hit, .18f, filter) ||
                !NavMesh.CalculatePath(merchant.transform.position, hit.position, filter, path) || path.status != NavMeshPathStatus.PathComplete)
                throw new InvalidOperationException("Ambient counter point unreachable: " + point.name);
            point.position = hit.position;
            if (path.corners.Any(c => c.z < 4.3f && c.x > -1.85f)) throw new InvalidOperationException("Counter route entered the central customer aisle.");
        }
        // The environment cups have an offset mesh and include their saucer. Keep that source intact,
        // replace only Wen's visible decoration with one existing cup-only prefab, authored once.
        var decoration = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).Single(x => x.name == "Cup.003_7_17");
        decoration.gameObject.SetActive(false);
        var cup = root.Find("AmbientCompanionCup");
        if (cup == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/cup-coffee_0.095_0.095_0.126.prefab");
            cup = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, root)).transform; cup.name = "AmbientCompanionCup";
            foreach (var collider in cup.GetComponentsInChildren<Collider>()) collider.enabled = false;
        }
        cup.position = new Vector3(-3.28f, .7346694f, .13f);
        // Inspect the actual prefab vertices: the handle protrudes along local Z.
        // Face that protrusion toward the right hand, rather than toward the NPC/table.
        var vertices = cup.GetComponentsInChildren<MeshFilter>().SelectMany(f => f.sharedMesh.vertices.Select(v =>
            cup.InverseTransformPoint(f.transform.TransformPoint(v)))).ToArray();
        var vertexZ = vertices.Select(v => v.z).OrderBy(z => z).ToArray();
        float bottom = vertices.Min(v => v.y);
        var baseRing = vertices.Where(v => v.y < bottom + .015f).ToArray();
        float bodyZ = (baseRing.Min(v => v.z) + baseRing.Max(v => v.z)) * .5f;
        float handleYaw = vertexZ[vertexZ.Length - 1] - bodyZ > bodyZ - vertexZ[0] ? 90 : -90;
        cup.rotation = customer.transform.rotation * Quaternion.Euler(0, handleYaw, 0);
        File.WriteAllText(Path.Combine(Evidence,"cup-handle-calibration.txt"), $"mesh local Z min={vertexZ[0]:F4}, body_base_center={bodyZ:F4}, max={vertexZ[vertexZ.Length - 1]:F4}; yaw={handleYaw}; handle faces actor right.");
        var table = Anchor(root, "AmbientCompanionCupTable", cup.position, cup.rotation);
        var hand = Anchor(customer.transform, "AmbientCupHand", cup.position, cup.rotation);
        var clip = CreateDrink(animator, customer.transform, cup.position);
        var controller = (AnimatorController)animator.runtimeAnimatorController;
        if (!controller.layers.Any(x => x.name == "AmbientDrinkArms"))
        {
            controller.AddLayer("AmbientDrinkArms");
            var layers = controller.layers; var layer = layers[layers.Length - 1];
            layer.defaultWeight = 0; layer.avatarMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(Animations + "CafeConversationArms.mask");
            var empty = layer.stateMachine.AddState("Empty"); empty.writeDefaultValues = false; layer.stateMachine.defaultState = empty;
            var drink = layer.stateMachine.AddState("Drink"); drink.motion = clip; drink.writeDefaultValues = false;
            controller.layers = layers;
        }
        var mp = merchant.GetComponent<CafeAmbientPresenter>() ?? merchant.gameObject.AddComponent<CafeAmbientPresenter>();
        var ma = merchant.GetComponentsInChildren<Animator>().Single(x => x.isHuman);
        var workController = (AnimatorController)ma.runtimeAnimatorController;
        var workLayer = workController.layers.Single(x => x.name == CounterCleaningPresenter.LayerName);
        var wipeState = workLayer.stateMachine.states.Select(x => x.state).FirstOrDefault(x => x.name == "AmbientClean") ?? workLayer.stateMachine.AddState("AmbientClean");
        wipeState.motion = CreateWipe(ma, merchant.transform); wipeState.writeDefaultValues = false;
        EditorUtility.SetDirty(workController);
        Set(mp, "animator", ma, "motion", merchant.GetComponent<MotionChannelCoordinator>(),
            "navigation", merchant.GetComponent<ReturnToHomeRoutine>(), "cleaning", merchant.GetComponent<CounterCleaningPresenter>());
        var cp = customer.GetComponent<CafeAmbientPresenter>() ?? customer.gameObject.AddComponent<CafeAmbientPresenter>();
        Set(cp, "animator", animator, "motion", customer.GetComponent<MotionChannelCoordinator>(),
            "cup", cup, "tableAnchor", table, "handAnchor", hand);
        var fields = new SerializedObject(cp);
        var gripCalibration = CalibrateGrip(animator, clip, cup, customer.transform);
        fields.FindProperty("gripPosition").vector3Value = gripCalibration.Item1;
        fields.FindProperty("gripRotation").vector3Value = gripCalibration.Item2;
        fields.ApplyModifiedPropertiesWithoutUndo();
        var admission = customer.GetComponent<NpcDefaultRoutineController>() ?? customer.gameObject.AddComponent<NpcDefaultRoutineController>();
        Set(admission, "owner", customer, "orchestrator", customer.GetComponent<AgentOrchestrator>(),
            "motionChannels", customer.GetComponent<MotionChannelCoordinator>(), "expressive", customer.GetComponent<ExpressiveController>(),
            "dialogue", Object.FindAnyObjectByType<DialogueController>(), "entityRegistry", Object.FindAnyObjectByType<EntityRegistry>());
        var a = new SerializedObject(admission); a.FindProperty("stationaryAmbientOnly").boolValue = true; a.ApplyModifiedPropertiesWithoutUndo();
        Configure(merchant, mp, new[] { p0, p1, p2 });
        Configure(customer, cp, Array.Empty<Transform>());
        var work = merchant.GetComponent<DailyBehaviorController>().Profile;
        work.ConfigureAmbient(new[] { new DailyAmbientBinding(DailyBehaviorMode.Work, DailyAmbientActivity.CounterMove),
            new DailyAmbientBinding(DailyBehaviorMode.Work, DailyAmbientActivity.CounterWipe) }, new Vector2(18, 38), new Vector2(7, 11));
        var leisure = customer.GetComponent<DailyBehaviorController>().Profile;
        leisure.ConfigureAmbient(new[] { new DailyAmbientBinding(DailyBehaviorMode.Leisure, DailyAmbientActivity.SeatedDrink) },
            new Vector2(32, 65), new Vector2(4, 9));
        EditorUtility.SetDirty(work); EditorUtility.SetDirty(leisure); EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        File.WriteAllText(Path.Combine(Evidence, "setup.txt"), "Three complete paths remain behind the counter or in its left service-side strip (z>=4.3 or x<=-1.85, allowing NavMesh corner steering); no central customer aisle crossing. West uses a separate ambient anchor at the existing spare-surface approach; formal work anchors unchanged. One existing cup-only prefab replaces Wen decoration; no runtime instantiation. Work/Rest/Leisure unchanged.");
        Debug.Log("AMBIENT_SETUP_COMPLETE");
    }
    private static void Configure(WorldEntity actor, CafeAmbientPresenter presenter, Transform[] points)
    {
        var daily = actor.GetComponent<DailyBehaviorController>(); var so = new SerializedObject(daily);
        so.FindProperty("ambientEnabled").boolValue = true; so.FindProperty("ambientPresenter").objectReferenceValue = presenter;
        var array = so.FindProperty("ambientWorkPoints"); array.arraySize = points.Length;
        for (int i = 0; i < points.Length; i++)
        { array.GetArrayElementAtIndex(i).FindPropertyRelative("point").objectReferenceValue = points[i];
          array.GetArrayElementAtIndex(i).FindPropertyRelative("allowWipe").boolValue = i != 0; }
        so.ApplyModifiedPropertiesWithoutUndo();
    }
    private static Transform Anchor(Transform parent, string name, Vector3 position, Quaternion rotation)
    {
        var t = parent.Find(name); if (t == null) { t = new GameObject(name).transform; t.SetParent(parent, false); }
        t.SetPositionAndRotation(position, rotation); return t;
    }
    private static void Set(Object target, params object[] fields)
    {
        var so = new SerializedObject(target);
        for (int i = 0; i < fields.Length; i += 2) so.FindProperty((string)fields[i]).objectReferenceValue = (Object)fields[i + 1];
        so.ApplyModifiedPropertiesWithoutUndo();
    }
    private static void EnsureModels()
    {
        foreach (var path in new[] { "Assets/CafeDemo/V2/Characters/Ao.vrm", "Assets/CafeDemo/V2/Characters/薇薇.vrm",
            "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm", "Assets/VRMCompatibilityTest/Xingmang.vrm" })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<Animator>()?.avatar == null)
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
    }
    private static AnimationClip CreateDrink(Animator source, Transform actor, Vector3 cup, float sipTiltDegrees = 12,
        string clipName = "CafeSeatedDrink", float sipForward = .19f)
    {
        string path = Animations + clipName + ".anim";
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        var clone = Object.Instantiate(source.gameObject, source.transform.parent);
        clone.name = "AmbientAuthoringOnly";
        foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) script.enabled = false;
        var animator = clone.GetComponent<Animator>(); animator.applyRootMotion = false;
        var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "ThirdParty/Quaternius/Sitting_Idle_Loop.anim");
        var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        var upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        var lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        float[] times = { 0, .7f, 1.1f, 1.7f, 2.4f, 3.4f, 4.1f, 4.8f, 5.1f, 5.7f, 6.2f };
        var poses = new float[times.Length][];
        try
        {
            idle.SampleAnimation(clone, .4f);
            Vector3 rest = hand.position;
            // The wrist stays behind and outside the cup: fingers wrap its side horizontally,
            // rather than reaching past the cup and folding the wrist to point fingers upward.
            Vector3 grip = cup + Vector3.up * .045f + actor.right * .065f - actor.forward * .065f;
            // The cup pivot is near its base; leave room for the rim below the eyes.
            Vector3 sip = head.position + actor.forward * sipForward - Vector3.up * .115f + actor.right * .01f -
                Quaternion.AngleAxis(-sipTiltDegrees, actor.right) * (cup - grip);
            Vector3 fingerAxis = hand.InverseTransformDirection(animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal).position - hand.position).normalized;
            Vector3 across = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal).position - animator.GetBoneTransform(HumanBodyBones.RightLittleProximal).position;
            Vector3 palmAxis = hand.InverseTransformDirection(Vector3.Cross(hand.TransformDirection(fingerAxis), across)).normalized;
            Quaternion localHandFrame = Quaternion.LookRotation(fingerAxis, palmAxis);
            for (int i = 0; i < times.Length; i++)
            {
                idle.SampleAnimation(clone, .4f);
                Quaternion restingHand = hand.rotation;
                Vector3 target = i switch { 0 => rest, 1 => grip - actor.forward * .045f + actor.right * .015f, 2 => grip,
                    3 => Vector3.Lerp(grip, sip, .45f), 4 => sip, 5 => sip,
                    6 => sip, 7 => grip + Vector3.up * .08f, 8 => grip,
                    9 => grip - actor.forward * .07f + actor.right * .02f - Vector3.up * .025f, _ => rest };
                if (i != 0 && i != times.Length - 1)
                {
                    SolveArm(upper, lower, hand, target, upper.position - Vector3.up * .6f + actor.right * .12f - actor.forward * .03f);
                    float tilt = i >= 4 && i <= 6 ? -sipTiltDegrees : 0;
                    Vector3 fingersForward = Quaternion.AngleAxis(tilt, actor.right) *
                        (actor.forward + Vector3.up * .1f).normalized;
                    Quaternion sideGrip = Quaternion.LookRotation(fingersForward, -actor.right) * Quaternion.Inverse(localHandFrame);
                    float turn = i == 1 || i == 9 ? .65f : 1;
                    hand.rotation = Quaternion.Slerp(restingHand, sideGrip, turn);
                }
                var pose = new HumanPose(); poseHandler.GetHumanPose(ref pose); poses[i] = (float[])pose.muscles.Clone();
                if (i >= 2 && i <= 8)
                    for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                        if (IsRightFinger(HumanTrait.MuscleName[muscle]))
                        {
                            string name = HumanTrait.MuscleName[muscle];
                            poses[i][muscle] = name.Contains("Spread") ? 0 : name.Contains("Thumb") ? -.25f : -.65f;
                        }
            }
            var clip = existing == null ? new AnimationClip { name = clipName, frameRate = 30 } : existing;
            clip.ClearCurves();
            for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
            {
                string name = HumanTrait.MuscleName[muscle];
                if (!(name.StartsWith("Right Arm") || name.StartsWith("Right Forearm") || name.StartsWith("Right Hand") ||
                    name.StartsWith("Right Shoulder") || IsRightFinger(name))) continue;
                var keys = times.Select((t, i) => new Keyframe(t, poses[i][muscle])).ToArray();
                var curve = new AnimationCurve(keys);
                for (int k = 0; k < keys.Length; k++) { AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto); AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto); }
                // HumanTrait uses "Right Index 1 Stretched"; Animator's serialized binding
                // uses "RightHand.Index.1 Stretched". Preserve that distinction for fingers.
                int gap = name.IndexOf(' ', 6);
                string binding = IsRightFinger(name) ? "RightHand." + name.Substring(6, gap - 6) + "." + name.Substring(gap + 1) : name;
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), binding), curve);
            }
            if (existing == null) AssetDatabase.CreateAsset(clip, path);
            else EditorUtility.SetDirty(clip);
            return clip;
        }
        finally { poseHandler.Dispose(); Object.DestroyImmediate(clone); }
    }
    private static bool IsRightFinger(string name) => name.StartsWith("Right Thumb ") || name.StartsWith("Right Index ") ||
        name.StartsWith("Right Middle ") || name.StartsWith("Right Ring ") || name.StartsWith("Right Little ");
    private static AnimationClip CreateWipe(Animator source, Transform actor)
    {
        string path = Animations + "CafeAmbientWipe.anim";
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        bool create = clip == null;
        if (create) clip = new AnimationClip { name = "CafeAmbientWipe", frameRate = 30 };
        clip.ClearCurves();
        var idle = AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "ThirdParty/KayKit/Idle_Quiet_Vivi.anim");
        var original = AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "CafeCleanCounter.anim");
        var bindings = AnimationUtility.GetCurveBindings(original).Where(b => b.propertyName.StartsWith("Right")).ToArray();
        float[] times = { 0, .4f, .7f, 1, 1.3f, 1.6f, 1.9f, 2.2f, 2.5f };
        var poses = new float[times.Length][];
        var clone = Object.Instantiate(source.gameObject, source.transform.parent);
        foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) script.enabled = false;
        var rig = clone.GetComponent<Animator>(); rig.applyRootMotion = false;
        var indexes = new[] { "Right Arm Down-Up", "Right Arm Front-Back", "Right Forearm Stretch" }
            .Select(n => Array.FindIndex(bindings, b => b.propertyName == n)).ToArray();
        var report = new System.Collections.Generic.List<string>();
        try
        {
            for (int k = 0; k < times.Length; k++)
            {
                var values = bindings.Select(b => AnimationUtility.GetEditorCurve(original, b).Evaluate(times[k])).ToArray();
                if (k == 0 || k == times.Length - 1)
                {
                    for (int m = 0; m < bindings.Length; m++)
                    { var resting = AnimationUtility.GetEditorCurve(idle, bindings[m]); if (resting != null) values[m] = resting.Evaluate(.4f); }
                }
                else
                {
                    Vector3 target = actor.position + Vector3.up * 1.0475f + actor.forward * .43f + actor.right * (.02f + .045f * Mathf.Sin(times[k] * Mathf.PI * 1.6f));
                    for (int iteration = 0; iteration < 24; iteration++)
                    {
                        Vector3 position = EvaluateArm(rig, idle, bindings, values);
                        Vector3 error = target - position;
                        if (error.magnitude < .002f) break;
                        var derivative = new Vector3[3];
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int index = indexes[axis]; float saved = values[index]; values[index] += .02f;
                            derivative[axis] = (EvaluateArm(rig, idle, bindings, values) - position) / .02f; values[index] = saved;
                        }
                        Vector3 a = derivative[0], b = derivative[1], c = derivative[2];
                        float determinant = Vector3.Dot(a, Vector3.Cross(b, c));
                        Vector3 step = Mathf.Abs(determinant) > .000001f ? new Vector3(Vector3.Dot(error, Vector3.Cross(b,c)),
                            Vector3.Dot(a, Vector3.Cross(error,c)), Vector3.Dot(a, Vector3.Cross(b,error))) / determinant :
                            new Vector3(Vector3.Dot(a,error), Vector3.Dot(b,error), Vector3.Dot(c,error)) * 2;
                        for (int axis = 0; axis < 3; axis++) values[indexes[axis]] = Mathf.Clamp(values[indexes[axis]] + Mathf.Clamp(step[axis], -.15f, .15f), -1, 1);
                    }
                    float miss = Vector3.Distance(target, EvaluateArm(rig, idle, bindings, values));
                    report.Add($"time={times[k]:F2} hand_error={miss:F4} target={target:F3}");
                    if (miss > .015f) { File.WriteAllLines(Path.Combine(Evidence,"wipe-authoring-contact.txt"),report); throw new InvalidOperationException("Ambient wipe contact calibration failed: " + miss); }
                }
                poses[k] = values;
            }
            for (int m = 0; m < bindings.Length; m++)
            {
                var curve = new AnimationCurve(times.Select((t, k) => new Keyframe(t, poses[k][m])).ToArray());
                for (int k = 0; k < times.Length; k++) { AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto); AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto); }
                AnimationUtility.SetEditorCurve(clip, bindings[m], curve);
            }
            // This layer also masks the left arm. With Write Defaults off, omitting it
            // inherits the formal CleanCounter state's lifted supporting arm. Explicitly
            // author its relaxed idle pose here; no runtime bone writer is needed.
            foreach (var binding in AnimationUtility.GetCurveBindings(idle).Where(b =>
                b.propertyName.StartsWith("Left Shoulder ") || b.propertyName.StartsWith("Left Arm ") ||
                b.propertyName.StartsWith("Left Forearm ") || b.propertyName.StartsWith("Left Hand ") || b.propertyName.StartsWith("LeftHand.")))
                AnimationUtility.SetEditorCurve(clip, binding,
                    AnimationCurve.Constant(0, times[times.Length - 1], AnimationUtility.GetEditorCurve(idle, binding).Evaluate(.4f)));
            File.WriteAllLines(Path.Combine(Evidence,"wipe-authoring-contact.txt"),report);
        }
        finally { Object.DestroyImmediate(clone); }
        if (create) AssetDatabase.CreateAsset(clip, path); else EditorUtility.SetDirty(clip);
        return clip;
    }
    // Offline fitting evaluates the real Avatar and the same masked layer as runtime.
    // Only baked muscle curves are shipped; no per-frame IK or mesh search is added.
    private static Vector3 EvaluateArm(Animator rig, AnimationClip idle, EditorCurveBinding[] bindings, float[] values)
    {
        var scratch = new AnimationClip(); var graph = PlayableGraph.Create("Ambient authoring fit");
        try
        {
            for (int i = 0; i < bindings.Length; i++) AnimationUtility.SetEditorCurve(scratch, bindings[i], AnimationCurve.Constant(0, 2.5f, values[i]));
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var mixer = AnimationLayerMixerPlayable.Create(graph, 2);
            var basePose = AnimationClipPlayable.Create(graph, idle); basePose.SetTime(.4); basePose.SetApplyFootIK(false);
            var arm = AnimationClipPlayable.Create(graph, scratch); arm.SetTime(.5); arm.SetApplyFootIK(false);
            graph.Connect(basePose, 0, mixer, 0); graph.Connect(arm, 0, mixer, 1);
            mixer.SetInputWeight(0,1); mixer.SetInputWeight(1,1);
            mixer.SetLayerMaskFromAvatarMask(1, AssetDatabase.LoadAssetAtPath<AvatarMask>(Animations + "CafePreparationArms.mask"));
            var output = AnimationPlayableOutput.Create(graph,"fit",rig); output.SetSourcePlayable(mixer);
            graph.Play(); graph.Evaluate(0);
            return rig.GetBoneTransform(HumanBodyBones.RightMiddleDistal).position;
        }
        finally { graph.Destroy(); Object.DestroyImmediate(scratch); }
    }
    private static void SolveArm(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 hint)
    {
        Vector3 root = upper.position, direction = target - root;
        float a = Vector3.Distance(root, lower.position), b = Vector3.Distance(lower.position, hand.position);
        float distance = Mathf.Clamp(direction.magnitude, Mathf.Abs(a - b) + .001f, a + b - .001f);
        Vector3 axis = direction.normalized;
        Vector3 bend = Vector3.ProjectOnPlane(hint - root, axis).normalized;
        float along = (a * a - b * b + distance * distance) / (2 * distance);
        Vector3 elbow = root + axis * along + bend * Mathf.Sqrt(Mathf.Max(0, a * a - along * along));
        upper.rotation = Quaternion.FromToRotation(lower.position - root, elbow - root) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, target - lower.position) * lower.rotation;
    }
    private static Tuple<Vector3, Vector3> CalibrateGrip(Animator source, AnimationClip clip, Transform cup, Transform actor)
    {
        var clone = Object.Instantiate(source.gameObject, source.transform.parent);
        foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) script.enabled = false;
        var graph = PlayableGraph.Create("Ambient grip authoring");
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var mixer = AnimationLayerMixerPlayable.Create(graph, 2);
            var idle = AnimationClipPlayable.Create(graph, AssetDatabase.LoadAssetAtPath<AnimationClip>(Animations + "ThirdParty/Quaternius/Sitting_Idle_Loop.anim"));
            var drink = AnimationClipPlayable.Create(graph, clip); idle.SetTime(.4); drink.SetTime(1.1);
            graph.Connect(idle, 0, mixer, 0); graph.Connect(drink, 0, mixer, 1);
            mixer.SetInputWeight(0, 1); mixer.SetInputWeight(1, 1);
            mixer.SetLayerMaskFromAvatarMask(1, AssetDatabase.LoadAssetAtPath<AvatarMask>(Animations + "CafeConversationArms.mask"));
            var animator = clone.GetComponent<Animator>();
            var output = AnimationPlayableOutput.Create(graph, "grip", animator); output.SetSourcePlayable(mixer);
            graph.Play(); graph.Evaluate(0);
            var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            return Tuple.Create(hand.InverseTransformPoint(cup.position), (Quaternion.Inverse(hand.rotation) * cup.rotation).eulerAngles);
        }
        finally { graph.Destroy(); Object.DestroyImmediate(clone); }
    }
}
