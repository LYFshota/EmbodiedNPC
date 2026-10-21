using EmbodiedNPC.Communication;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Settings;
using UnityEditor;
using UnityEngine;

namespace CafeDemo.Tests
{
    /// <summary>Animation scene fixtures have no network or settings UI dependency.</summary>
    internal static class AnimationSceneTestIsolation
    {
        public static void Prepare()
        {
            var backend = Object.FindAnyObjectByType<BackendWebSocketClient>();
            var serialized = new SerializedObject(backend);
            serialized.FindProperty("connectOnStart").boolValue = false;
            serialized.FindProperty("serverUrl").stringValue = "ws://127.0.0.1:0/ws";
            serialized.ApplyModifiedPropertiesWithoutUndo(); backend.enabled = false;
            Object.FindAnyObjectByType<CafeDemoDeveloperPanel>().enabled = false;
            Object.FindAnyObjectByType<FirstRunModelSetup>(FindObjectsInactive.Include).enabled = false;
            var trace = Object.FindAnyObjectByType<RuntimeTraceRecorder>();
            if (trace != null)
            {
                var traceFields = new SerializedObject(trace);
                string isolatedEvidence = System.Environment.GetEnvironmentVariable("EMBODIEDNPC_ANIMATION_EVIDENCE_ROOT");
                traceFields.FindProperty("outputDirectoryOverride").stringValue = System.IO.Path.GetFullPath(
                    string.IsNullOrWhiteSpace(isolatedEvidence)
                        ? System.IO.Path.Combine(Application.dataPath, "../../InternalDocs/Reports/NaturalInteractionP0Evidence/Traces")
                        : System.IO.Path.Combine(isolatedEvidence, "Traces"));
                traceFields.ApplyModifiedPropertiesWithoutUndo();
            }
            Object.FindAnyObjectByType<LocalTestIdentityProvider>()?.Configure("natural-p0-isolated-player", "测试玩家", 1,
                System.DateTimeOffset.UnixEpoch);
        }
        public static void Ready()
        {
            // Bypass only the startup UI/service prerequisite in an offline fixture; action authority is unchanged.
            Object.FindAnyObjectByType<DialogueController>().SetSetupReady(true);
        }
    }
}
