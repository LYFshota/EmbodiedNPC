using System.Linq;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue.Presentation;
using UnityEditor;
using UnityEngine;

namespace CafeDemo.Editor
{
    /// <summary>Manual visual audition only; never sends a model request or fabricates history.</summary>
    public sealed class CafeEmotionIconPreview : EditorWindow
    {
        private DialogueEmotionIconPresenter[] actors = System.Array.Empty<DialogueEmotionIconPresenter>();
        private int selected;
        [MenuItem("EmbodiedNPC/表现/情绪图标预览")]
        public static void Open()
        {
            var window = GetWindow<CafeEmotionIconPreview>(true, "情绪图标预览");
            window.minSize = new Vector2(240, 190); window.maxSize = new Vector2(300, 230);
            window.RefreshActors(); window.ShowUtility();
        }
        private void OnEnable() { EditorApplication.playModeStateChanged += Changed; RefreshActors(); }
        private void OnDisable() => EditorApplication.playModeStateChanged -= Changed;
        private void Changed(PlayModeStateChange _) { RefreshActors(); Repaint(); }
        private void RefreshActors()
        {
            actors = Object.FindObjectsByType<DialogueEmotionIconPresenter>(FindObjectsSortMode.None);
            selected = Mathf.Clamp(selected, 0, Mathf.Max(0, actors.Length - 1));
        }
        private static string Caption(DialogueEmotionIconPresenter actor)
        {
            var definition = actor.GetComponent<CognitiveContextComposer>()?.CharacterDefinition;
            return definition == null ? actor.name : CharacterProfileResolver.Resolve(definition).display_name;
        }
        private void OnGUI()
        {
            EditorGUILayout.HelpBox("仅视觉预览，不生成对话或写入记忆。\n请让所选 NPC 头部位于 Game 画面内。", MessageType.Info);
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
            {
                if (actors.Length == 0 || actors.Any(a => a == null)) RefreshActors();
                if (actors.Length == 0) { GUILayout.Label("进入 CafeDemoV2 Play Mode 后可预览。"); return; }
                selected = EditorGUILayout.Popup("角色", selected, actors.Select(Caption).ToArray());
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("♪ 开心", GUILayout.Height(30))) actors[selected].PreviewForEditor("happy");
                if (GUILayout.Button("? 疑惑", GUILayout.Height(30))) actors[selected].PreviewForEditor("confused");
                GUILayout.EndHorizontal(); GUILayout.BeginHorizontal();
                if (GUILayout.Button("! 惊讶", GUILayout.Height(30))) actors[selected].PreviewForEditor("surprised");
                if (GUILayout.Button("… 思考", GUILayout.Height(30))) actors[selected].PreviewForEditor("thinking");
                GUILayout.EndHorizontal();
                if (GUILayout.Button("取消显示")) foreach (var actor in actors) actor.ClearPresentation();
            }
        }
    }
}
