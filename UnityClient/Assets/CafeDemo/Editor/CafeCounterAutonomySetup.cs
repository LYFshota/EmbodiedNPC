using System;
using System.Linq;
using EmbodiedNPC.Actions;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Events;
using EmbodiedNPC.Routines;
using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class CafeCounterAutonomySetup
{
    [MenuItem("Tools/CafeDemoV2/Configure counter autonomy (P0)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (Application.isPlaying || scene.path != "Assets/Scenes/CafeDemoV2.unity") throw new InvalidOperationException("Open CafeDemoV2 in Edit Mode.");
        var merchant = UnityEngine.Object.FindObjectsByType<WorldEntity>(FindObjectsSortMode.None).Single(e => e.EntityId == "npc_cafe_01");
        var source = merchant.GetComponent<CounterCleaningCognitionSource>() ?? merchant.gameObject.AddComponent<CounterCleaningCognitionSource>();
        source.Configure(merchant.GetComponent<CounterCleaningCapability>(), UnityEngine.Object.FindAnyObjectByType<EntityRegistry>(),
            UnityEngine.Object.FindAnyObjectByType<AgentEventRouter>(), UnityEngine.Object.FindAnyObjectByType<DialogueController>());
        var profile = merchant.GetComponent<AgentEventReceiver>().InterestProfile;
        if (!profile.Rules.Any(r => r.EventType == AgentEventTypes.CounterNeedsCleaning))
            profile.Configure(profile.FallbackDecision, profile.Rules.Concat(new[] {
                new EventInterestRule(AgentEventTypes.CounterNeedsCleaning, EventInterestDecision.Wake, "area_cafe_counter", EventInterestRelevance.TargetSelf, "work") }).ToArray());
        EditorUtility.SetDirty(profile); AssetDatabase.SaveAssetIfDirty(profile); EditorUtility.SetDirty(source);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
    }
}
