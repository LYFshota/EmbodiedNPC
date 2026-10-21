using System;
using UnityEngine;

namespace EmbodiedNPC.Events
{
    /// <summary>Explicit current work/life context. Scheduling and transition policy are out of scope.</summary>
    [DisallowMultipleComponent]
    public sealed class CurrentBehaviorMode : MonoBehaviour
    {
        [SerializeField] private string currentModeId = BehaviorModeIds.Working;

        public string CurrentModeId => currentModeId ?? string.Empty;
        public event Action<string, string> ModeChanged;

        public bool TrySetMode(string modeId)
        {
            if (!BehaviorModeIds.IsValid(modeId)) return false;
            if (string.Equals(CurrentModeId, modeId, StringComparison.Ordinal)) return true;

            string previousModeId = CurrentModeId;
            currentModeId = modeId;
            ModeChanged?.Invoke(previousModeId, currentModeId);
            return true;
        }

        public bool TryValidateConfiguration(out string error)
        {
            if (!BehaviorModeIds.IsValid(CurrentModeId))
            {
                error = "CurrentBehaviorMode requires a lowercase mode id using letters, digits, or underscores.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
