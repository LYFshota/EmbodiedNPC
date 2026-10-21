using System;
using System.Linq;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Events;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>Explicit authoring for the current V2 scene, without touching other scenes or action configuration.</summary>
public static class CafeV2DailyBehaviorSetup
{
    [MenuItem("Tools/CafeDemoV2/Configure daily behavior P0")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/Scenes/CafeDemoV2.unity")
            throw new InvalidOperationException("Open CafeDemoV2 in Edit Mode.");
        var actors = UnityEngine.Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None)
            .Where(x => x.IsAgent(AgentRole.Npc) && x.GetComponent<ExpressiveController>() != null).ToArray();
        if (actors.Length != 4) throw new InvalidOperationException("Expected four V2 NPCs.");
        var merchant = actors.Single(x => x.EntityId == "npc_cafe_01");
        var point = new Vector3(1.45f, 0, -.35f);
        if (!NavMesh.SamplePosition(point, out NavMeshHit hit, .2f, NavMesh.AllAreas))
            throw new InvalidOperationException("Window rest point is not on the current NavMesh.");
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(merchant.transform.position, hit.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete)
            throw new InvalidOperationException("Window rest point has no complete path from the counter.");

        var work = LoadOrCreate("Assets/CafeDemo/Profiles/MerchantDailyBehaviorProfile.asset");
        work.Configure(DailyBehaviorMode.Work, new[] { DailyBehaviorMode.Work, DailyBehaviorMode.Rest }, new[] {
            new DailyBehaviorTransition(DailyBehaviorMode.Work, DailyBehaviorMode.Rest, AgentEventTypes.WorkBreakStarted),
            new DailyBehaviorTransition(DailyBehaviorMode.Rest, DailyBehaviorMode.Work, AgentEventTypes.WorkBreakEnded) });
        var leisure = LoadOrCreate("Assets/CafeDemo/Profiles/CustomerDailyBehaviorProfile.asset");
        leisure.Configure(DailyBehaviorMode.Leisure, new[] { DailyBehaviorMode.Leisure }, Array.Empty<DailyBehaviorTransition>());
        EditorUtility.SetDirty(work); EditorUtility.SetDirty(leisure);
        var root = scene.GetRootGameObjects().FirstOrDefault(x => x.name == "DailyBehaviorAnchors");
        if (root == null) { root = new GameObject("DailyBehaviorAnchors"); Undo.RegisterCreatedObjectUndo(root, "Daily behavior anchors"); }
        // Look through the opening, clearing the south wooden return wall from the rest position.
        var lookPosition = new Vector3(3.6f, 1.55f, 1.1f);
        var rest = Anchor(root.transform, "WindowRestPoint", hit.position,
            Quaternion.LookRotation(Vector3.ProjectOnPlane(lookPosition - hit.position, Vector3.up)));
        var look = Anchor(root.transform, "WindowLookPoint", lookPosition, Quaternion.identity);
        foreach (var actor in actors)
        {
            var controller = actor.GetComponent<DailyBehaviorController>() ?? Undo.AddComponent<DailyBehaviorController>(actor.gameObject);
            var so = new SerializedObject(controller);
            so.FindProperty("profile").objectReferenceValue = actor == merchant ? work : leisure;
            so.FindProperty("windowRestPoint").objectReferenceValue = actor == merchant ? rest : null;
            so.FindProperty("windowLookPoint").objectReferenceValue = actor == merchant ? look : null;
            so.ApplyModifiedProperties();
        }
        var trigger = merchant.GetComponent<DailyBehaviorDemoTrigger>() ?? Undo.AddComponent<DailyBehaviorDemoTrigger>(merchant.gameObject);
        var fields = new SerializedObject(trigger);
        fields.FindProperty("router").objectReferenceValue = UnityEngine.Object.FindAnyObjectByType<AgentEventRouter>();
        fields.FindProperty("automaticDemo").boolValue = false;
        fields.FindProperty("breakStartsAfterSeconds").floatValue = 15;
        fields.FindProperty("breakDurationSeconds").floatValue = 35;
        fields.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        Debug.Log("[DailyBehaviorP0] Four profiles and one local window-break demo configured.");
    }
    private static DailyBehaviorProfile LoadOrCreate(string path)
    {
        var profile = AssetDatabase.LoadAssetAtPath<DailyBehaviorProfile>(path);
        if (profile == null) { profile = ScriptableObject.CreateInstance<DailyBehaviorProfile>(); AssetDatabase.CreateAsset(profile, path); }
        return profile;
    }
    private static Transform Anchor(Transform parent, string name, Vector3 point, Quaternion rotation)
    {
        var value = parent.Find(name);
        if (value == null) { var go = new GameObject(name); Undo.RegisterCreatedObjectUndo(go, "Daily behavior anchor"); value = go.transform; value.SetParent(parent); }
        Undo.RecordObject(value, "Daily behavior anchor"); value.SetPositionAndRotation(point, rotation); return value;
    }
}
