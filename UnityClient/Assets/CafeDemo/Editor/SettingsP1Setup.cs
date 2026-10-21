using System;
using CafeDemo;
using EmbodiedNPC.Communication;
using EmbodiedNPC.Settings;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>Replace only the P0 copied settings surface with the original nested prefab.</summary>
public static class SettingsP1Setup
{
    [MenuItem("EmbodiedNPC/UI/Converge Title Settings P1")]
    public static void Apply()
    {
        var previous = SceneManager.GetActiveScene();
        if (EditorApplication.isPlaying || previous.isDirty) throw new InvalidOperationException("Keep current scene work first.");
        var root = PrefabUtility.LoadPrefabContents(TitleSceneSetup.UiPath);
        try
        {
            var host = root.GetComponentInChildren<TitleSettingsHost>(true);
            var so = new SerializedObject(host);
            var old = so.FindProperty("surface").objectReferenceValue as GameObject;
            if (old == null) throw new InvalidOperationException("Title settings surface is missing.");
            var settings = PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/CafeDemo/Prefabs/UI/DeveloperSettings.prefab"), host.transform) as GameObject;
            settings.name = "SharedSettings";
            var form = settings.GetComponent<FirstRunModelSetup>();
            var fs = new SerializedObject(form);
            fs.FindProperty("client").objectReferenceValue = null;
            fs.FindProperty("dialogueController").objectReferenceValue = null;
            fs.ApplyModifiedPropertiesWithoutUndo();
            so.FindProperty("surface").objectReferenceValue = settings;
            so.FindProperty("form").objectReferenceValue = form;
            so.FindProperty("closeButton").objectReferenceValue = settings.transform.Find("Window/Close").GetComponent<Button>();
            so.FindProperty("saveButton").objectReferenceValue = fs.FindProperty("saveButton").objectReferenceValue;
            so.ApplyModifiedPropertiesWithoutUndo();
            Object.DestroyImmediate(old);
            settings.SetActive(false);
            PrefabUtility.SaveAsPrefabAsset(root, TitleSceneSetup.UiPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        var title = EditorSceneManager.OpenScene(TitleSceneSetup.ScenePath, OpenSceneMode.Additive);
        try
        {
            ModelConfigClient client = null; FirstRunModelSetup form = null;
            foreach (var go in title.GetRootGameObjects())
            {
                if (go.GetComponent<ModelConfigClient>() != null) client = go.GetComponent<ModelConfigClient>();
                var candidate = go.GetComponentInChildren<FirstRunModelSetup>(true); if (candidate != null) form = candidate;
            }
            if (client == null || form == null) throw new InvalidOperationException("Title service/form binding missing.");
            var serialized = new SerializedObject(form); serialized.FindProperty("client").objectReferenceValue = client;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(form);
            EditorSceneManager.SaveScene(title);
        }
        finally { EditorSceneManager.CloseScene(title, true); SceneManager.SetActiveScene(previous); }
    }
}
