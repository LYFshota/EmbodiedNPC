using System;
using System.IO;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Identity;
using EmbodiedNPC.Settings;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CafeDemo.Tests
{
    public static class PlayerVrmAcceptanceBuild
    {
        public static void Build()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/CafeDemoV2.unity");
            AnimationSceneTestIsolation.Prepare();
            foreach (var driver in UnityEngine.Object.FindObjectsByType<CognitiveRunDriver>()) driver.enabled = false;
            var trace = UnityEngine.Object.FindAnyObjectByType<CafeDemo.RuntimeTraceRecorder>();
            if (trace != null) trace.enabled = false;
            // The settings page itself is live; only startup requests are disabled in this offline acceptance fixture.
            var settings = UnityEngine.Object.FindAnyObjectByType<CafeDemoDeveloperPanel>();
            settings.enabled = true;
            UnityEngine.Object.FindAnyObjectByType<LocalTestIdentityProvider>().Configure("avatar-standalone-test", "Avatar Test", 1, DateTimeOffset.UnixEpoch);
            new GameObject("Offline VRM Player acceptance").AddComponent<PlayerVrmStandaloneProbe>();
            const string generated = "Assets/CafeDemo/Tests/GeneratedPlayerVrm/CafeDemoV2.unity";
            Directory.CreateDirectory(Path.GetDirectoryName(generated)); EditorSceneManager.SaveScene(scene, generated);
            string output = Path.GetFullPath("../PlayerVrmEvidence/Player/EmbodiedNPC.exe");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { generated },
                locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
            Directory.CreateDirectory("../PlayerVrmEvidence");
            File.WriteAllText("../PlayerVrmEvidence/build-result.txt", report.summary.result + "; errors=" + report.summary.totalErrors + "; size=" + report.summary.totalSize);
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("VRM acceptance Player build failed");
        }
    }
}
