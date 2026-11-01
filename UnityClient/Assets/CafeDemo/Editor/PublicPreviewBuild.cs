using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public static class PublicPreviewBuild
{
    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "-public-build-output");
        if (index < 0 || index + 1 >= args.Length) throw new Exception("Missing build output");
        var output = Path.GetFullPath(args[index + 1]);
        if (output.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase)) throw new Exception("Build outside Assets");
        ValidateModels();
        Build(output);
    }

    public static void ValidateModels()
    {
        string[] models = {
            "Assets/CafeDemo/V2/Characters/薇薇.vrm", "Assets/CafeDemo/V2/Characters/Ao.vrm",
            "Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm", "Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm",
            "Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb", "Assets/VRMCompatibilityTest/Xingmang.vrm" };
        foreach (var path in models)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) throw new Exception("Model import failed: " + path);
            if (path.EndsWith(".vrm"))
            {
                bool valid = false;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset is Avatar a && a.isHuman && a.isValid) valid = true;
                if (!valid) throw new Exception("Humanoid avatar missing: " + path);
            }
        }
        Debug.Log("PUBLIC_PREVIEW_MODELS_VALIDATED: six imports, five valid Humanoid avatars");
    }

    private static void Build(string output)
    {
        if (PlayerSettings.bundleVersion != "0.1.4-preview")
            throw new Exception("Release application version mismatch");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes = new[] { "Assets/Scenes/TitleScene.unity", "Assets/Scenes/CafeDemoV2.unity" },
            locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None });
        var summary = report.summary;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "build-result.json"),
            "{\"version\":\"" + PlayerSettings.bundleVersion + "\",\"result\":\"" + summary.result + "\",\"errors\":" + summary.totalErrors + ",\"warnings\":" + summary.totalWarnings + ",\"bytes\":" + summary.totalSize + "}");
        if (summary.result != BuildResult.Succeeded) throw new Exception("Windows build failed");
        Debug.Log("PUBLIC_PREVIEW_BUILD_SUCCEEDED: " + output);
    }
}

// Performance Testing writes host metadata even for ordinary Player builds (order 0).
// Run after its setup so generated test Resources cannot enter a public Player.
public sealed class PublicPreviewBuildResourceGuard : IPreprocessBuildWithReport
{
    public int callbackOrder => int.MaxValue;
    public void OnPreprocessBuild(BuildReport report)
    {
        if ((report.summary.options & BuildOptions.IncludeTestAssemblies) != 0) return;
        foreach (var name in new[] { "PerformanceTestRunInfo.json", "PerformanceTestRunSettings.json" })
        {
            string path = "Assets/Resources/" + name;
            if (File.Exists(path) && !AssetDatabase.DeleteAsset(path))
                throw new Exception("Cannot exclude generated performance-test metadata: " + name);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("PUBLIC_PREVIEW_TEST_METADATA_EXCLUDED");
    }
}
