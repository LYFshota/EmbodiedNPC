using System;
using UnityEngine;

namespace EmbodiedNPC.Animation
{
    [Flags]
    public enum MotionChannel { None = 0, Locomotion = 1 << 0, Gesture = 1 << 1, Interaction = 1 << 2, Emergency = 1 << 3 }

    /// <summary>Per-actor presentation arbiter; never owns gameplay work.</summary>
    [DisallowMultipleComponent]
    public sealed class MotionChannelCoordinator : MonoBehaviour
    {
        private bool legacyTask, navigation, interaction, emergency, carrying;
        private long nextGestureId;
        private double gestureDeadline;
        public event Action<string> GestureApproved;
        public event Action<long> GestureEnded;
        public long ActiveGestureId { get; private set; }
        public string ActiveGesture { get; private set; }
        public MotionChannel OccupiedChannels =>
            (legacyTask ? MotionChannel.Locomotion : MotionChannel.None) |
            (navigation ? MotionChannel.Locomotion : MotionChannel.None) |
            (interaction ? MotionChannel.Interaction : MotionChannel.None) |
            (emergency ? MotionChannel.Emergency | MotionChannel.Locomotion : MotionChannel.None) |
            (ActiveGestureId != 0 ? MotionChannel.Gesture : MotionChannel.None);
        public MotionChannel ActiveBodyChannel => emergency ? MotionChannel.Emergency :
            interaction ? MotionChannel.Interaction : navigation || legacyTask ? MotionChannel.Locomotion :
            ActiveGestureId != 0 ? MotionChannel.Gesture : MotionChannel.None;
        public bool IsTaskMotionActive => legacyTask || navigation || interaction;
        public bool IsEmergencyMotionActive => emergency;
        public bool IsNavigationActive => navigation;
        public bool IsFormalTaskMotionActive => legacyTask;
        public bool IsInteractionMotionActive => interaction;
        public bool IsCarryPresentationActive => carrying;
        // Holding is a visual occupancy, not a new task or world-state authority.
        public void SetCarryPresentationActive(bool active) { carrying = active; if (active) CancelGesture(); }

        // Compatibility only: old callers describe a whole task, not an atomic interaction phase.
        public void BeginTaskMotion() { legacyTask = true; CancelGesture(); }
        public void EndTaskMotion() => legacyTask = false;
        public void SetNavigationActive(bool active) { navigation = active; if (active) CancelGesture(); }
        public void SetInteractionActive(bool active) { interaction = active; if (active) CancelGesture(); }
        public void BeginEmergencyMotion()
        {
            legacyTask = navigation = interaction = false;
            emergency = true;
            CancelGesture();
        }
        public void EndEmergencyMotion() => emergency = false;

        public bool TryBeginGesture(string gesture, bool accompaniesGoalProposal, double now,
            float timeoutSeconds, out long id)
        {
            id = 0;
            if (double.IsNaN(now) || double.IsInfinity(now) || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds) ||
                !isActiveAndEnabled || (gesture != "wave" && gesture != "nod") || accompaniesGoalProposal ||
                IsTaskMotionActive || emergency || carrying || ActiveGestureId != 0) return false;
            id = ActiveGestureId = ++nextGestureId;
            ActiveGesture = gesture;
            gestureDeadline = now + Mathf.Clamp(timeoutSeconds, 0.1f, 10f);
            return true;
        }
        public bool CompleteGesture(long id)
        {
            if (id == 0 || id != ActiveGestureId) return false;
            ActiveGestureId = 0; ActiveGesture = null;
            if (GestureEnded != null)
                foreach (Delegate observer in GestureEnded.GetInvocationList())
                    try { ((Action<long>)observer)(id); }
                    catch (Exception exception) { Debug.LogWarning($"[Motion] observer failed: {exception.GetType().Name}", this); }
            return true;
        }
        public void CancelGesture() { if (ActiveGestureId != 0) CompleteGesture(ActiveGestureId); }
        public void Tick(double now) { if (ActiveGestureId != 0 && now >= gestureDeadline) CancelGesture(); }
        public bool RequestGesture(string gesture, bool accompaniesGoalProposal)
        {
            if (!TryBeginGesture(gesture, accompaniesGoalProposal, Time.unscaledTimeAsDouble, 3f, out long id)) return false;
            try { GestureApproved?.Invoke(gesture); }
            catch { CompleteGesture(id); throw; }
            return true;
        }
        private void Update() => Tick(Time.unscaledTimeAsDouble);
        private void OnDisable()
        {
            legacyTask = navigation = interaction = emergency = carrying = false;
            CancelGesture();
        }
    }
}
