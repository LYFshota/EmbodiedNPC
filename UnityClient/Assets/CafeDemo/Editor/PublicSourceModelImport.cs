using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

/// <summary>One bounded recovery for incomplete public models after a cold Library import.</summary>
public static class PublicSourceModelImport
{
    private const string AttemptDirectory = "Library/PublicSourceModelImport";
    private static int idleUpdates;
    private static bool running;
    private static readonly string[] ShaderPaths = {
        "Packages/com.vrmc.gltf/Resources/UniGLTF/StandardMapImporter.shader",
        "Packages/com.vrmc.gltf/UniUnlit/Shaders/UniUnlit.shader",
        "Packages/com.vrmc.vrm/MToon10/Shaders/vrmc_materials_mtoon.shader",
        "Packages/com.vrmc.vrm/MToon10/Shaders/vrmc_materials_mtoon_urp.shader",
        "Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader" };

    [InitializeOnLoadMethod]
    private static void Schedule()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess()) return;
        EditorApplication.update += WhenIdle;
    }

    private static bool Busy => EditorApplication.isCompiling || EditorApplication.isUpdating ||
        ShaderUtil.anythingCompiling || EditorApplication.isPlayingOrWillChangePlaymode;

    private static void WhenIdle()
    {
        if (Busy) { idleUpdates = 0; return; }
        if (++idleUpdates < 2) return;
        EditorApplication.update -= WhenIdle;
        try { ValidateBatch(); }
        catch (Exception error) { Debug.LogException(error); }
    }

    // For CI: executeMethod runs after the initial AssetDatabase refresh; -quit need not
    // service EditorApplication.update. Exceptions deliberately fail the batch invocation.
    public static void ValidateBatch()
    {
        if (Busy || AssetDatabase.IsAssetImportWorkerProcess() || running)
            throw new InvalidOperationException("Public source models require an idle Editor (imports, scripts and shaders complete).");
        running = true;
        try
        {
            var packages = PackageInfo.GetAllRegisteredPackages();
            foreach (var name in new[] { "com.vrmc.gltf", "com.vrmc.vrm", "com.unity.render-pipelines.universal" })
                if (!packages.Any(p => p.name == name && Directory.Exists(p.resolvedPath)))
                    throw new InvalidOperationException("Public source model prerequisite package is unavailable: " + name + ". Check Package Manager / Editor.log; no recovery attempted.");

            // Shader import records alone do not guarantee Shader.Find availability during
            // the initial refresh. Load the fixed package assets before asking UniVRM to retry.
            var signature = new StringBuilder("public-model-import-v1|").Append(Application.unityVersion);
            foreach (var path in ShaderPaths)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null || Shader.Find(shader.name) == null || ShaderUtil.ShaderHasError(shader))
                    throw new InvalidOperationException("Public source model prerequisite shader is unavailable or failed: " + path + ". Check its import log; no recovery attempted.");
                signature.Append('|').Append(AssetDatabase.GetAssetDependencyHash(path));
            }

            var invalid = PublicSourceModels.FindInvalidModels();
            foreach (var entry in invalid)
            {
                string path = entry.Key;
                if (!File.Exists(path))
                    throw new FileNotFoundException("Tracked public source model missing; restore the source checkout.", path);
                string fingerprint = Hash128.Compute(signature + "|" + SourceHash(path) + "|" + SourceHash(path + ".meta")).ToString();
                string attempt = Path.Combine(AttemptDirectory, AssetDatabase.AssetPathToGUID(path) + "-" + fingerprint + ".attempt");
                if (File.Exists(attempt))
                    throw new InvalidOperationException("Public source model remains invalid after its single automatic recovery: " + path + " (" + entry.Value + "). See Editor.log and " + attempt + ". No automatic retry until source/shader dependencies change.");
                Directory.CreateDirectory(AttemptDirectory);
                // Write before ImportAsset: survives domain reload, process interruption and
                // reopening, so a failing importer cannot produce an automatic import loop.
                File.WriteAllText(attempt, path + "\n" + entry.Value + "\n");
                Debug.Log("PUBLIC_SOURCE_MODEL_REPAIR: " + path + " — " + entry.Value);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            PublicSourceModels.Validate();
            Debug.Log("PUBLIC_SOURCE_MODEL_IMPORT_READY: repaired=" + invalid.Count + "; no source assets saved");
        }
        finally { running = false; }
    }

    private static string SourceHash(string path)
    {
        using var hash = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(hash.ComputeHash(stream));
    }
}
