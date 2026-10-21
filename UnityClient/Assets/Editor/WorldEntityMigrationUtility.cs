using EmbodiedNPC.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EmbodiedNPC.Editor
{
    internal static class WorldEntityMigrationUtility
    {
        [MenuItem("Tools/EmbodiedNPC/Migrate Open Scene WorldEntity v1 to v2")]
        private static void MigrateOpenScene()
        {
            int migrated = 0;
            UnityEngine.SceneManagement.Scene activeScene = EditorSceneManager.GetActiveScene();
            foreach (WorldEntity entity in Object.FindObjectsByType<WorldEntity>(FindObjectsInactive.Include))
            {
                if (entity.gameObject.scene != activeScene || !entity.HasLegacyKind) continue;
                Undo.RecordObject(entity, "Migrate WorldEntity identity");
                if (!entity.MigrateLegacyIdentity()) continue;
                EditorUtility.SetDirty(entity);
                migrated++;
            }

            if (migrated > 0) EditorSceneManager.MarkSceneDirty(activeScene);
            Debug.Log($"[WorldEntityMigration] Migrated {migrated} WorldEntity component(s) in the open scene.");
        }
    }
}
