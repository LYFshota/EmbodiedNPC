namespace EmbodiedNPC.Events
{
    /// <summary>Stable identifiers for the minimal behavior modes currently used by CafeDemo.</summary>
    public static class BehaviorModeIds
    {
        public const string Working = "working";
        public const string OffDuty = "off_duty";

        public static bool IsValid(string modeId)
        {
            if (string.IsNullOrEmpty(modeId) || modeId[0] < 'a' || modeId[0] > 'z') return false;
            for (int index = 1; index < modeId.Length; index++)
            {
                char value = modeId[index];
                if ((value < 'a' || value > 'z') && (value < '0' || value > '9') && value != '_')
                    return false;
            }
            return true;
        }
    }
}
