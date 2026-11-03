using System;
using UnityEngine;
using EmbodiedNPC.Routines;

namespace EmbodiedNPC.Animation
{
    public enum AmbientBodyUse { Arms, Navigation }

    public sealed partial class MotionChannelCoordinator
    {
        private MonoBehaviour ambientOwner;
        private long nextAmbientId;
        private AmbientBodyUse ambientUse;
        public long ActiveAmbientId { get; private set; }
        public string ActiveAmbientAction { get; private set; }
        public string LastAmbientEndReason { get; private set; }
        public bool IsAmbientRecovering { get; private set; }
        public bool IsAmbientNavigationPaused { get; private set; }
        public bool IsAmbientUsingArms => ActiveAmbientId != 0 && ambientUse == AmbientBodyUse.Arms;
        public bool IsAmbientNavigation => ActiveAmbientId != 0 && ambientUse == AmbientBodyUse.Navigation;
        public event Action<long, string> AmbientEnded;

        public bool TryBeginAmbient(MonoBehaviour owner, string action, AmbientBodyUse use, out long id)
        {
            id = 0;
            if (!isActiveAndEnabled || owner == null || !owner.isActiveAndEnabled || string.IsNullOrWhiteSpace(action) ||
                ActiveAmbientId != 0 || IsTaskMotionActive || emergency || carrying ||
                ActiveGestureId != 0 && (ActiveGesture != "nod" || use == AmbientBodyUse.Navigation)) return false;
            ambientOwner = owner; ambientUse = use; ActiveAmbientAction = action;
            id = ActiveAmbientId = ++nextAmbientId;
            return true;
        }
        public bool OwnsAmbient(MonoBehaviour owner, long id) => isActiveAndEnabled && id != 0 &&
            id == ActiveAmbientId && ambientOwner == owner && owner != null && owner.isActiveAndEnabled;
        public bool CompleteAmbient(MonoBehaviour owner, long id, string reason = "completed")
        {
            if (id == 0 || id != ActiveAmbientId || ambientOwner != owner) return false;
            CancelAmbient(reason); return true;
        }
        // Conversation may let the current owner finish its bounded return. Formal
        // task/emergency paths still use CancelAmbient and revoke immediately.
        public void RequestAmbientEnd(string reason)
        {
            if (ActiveAmbientId == 0 || IsAmbientRecovering) return;
            // A conversation pauses travel, rather than forgetting its selected work point.
            // The same revocable lease remains subject to every formal-task cancellation.
            if (IsAmbientNavigation && (reason == "conversation" || reason == "player_utterance"))
            { IsAmbientNavigationPaused = true; return; }
            long id = ActiveAmbientId;
            if (ambientOwner is IDailyAmbientPresenter presenter && ambientOwner.isActiveAndEnabled &&
                presenter.TryBeginRecovery(id) && ActiveAmbientId == id)
            { IsAmbientRecovering = true; return; }
            CancelAmbient(reason);
        }
        public bool ResumeAmbientNavigation(MonoBehaviour owner, long id)
        {
            if (!IsAmbientNavigationPaused || !OwnsAmbient(owner, id) || IsTaskMotionActive ||
                emergency || carrying || ActiveGestureId != 0) return false;
            IsAmbientNavigationPaused = false; return true;
        }
        public void CancelAmbient(string reason)
        {
            if (ActiveAmbientId == 0) return;
            long ended = ActiveAmbientId;
            ActiveAmbientId = 0; ambientOwner = null; ActiveAmbientAction = null; IsAmbientRecovering = false;
            IsAmbientNavigationPaused = false;
            LastAmbientEndReason = reason ?? "cancelled";
            // Revoke before notifying: old cleanup can never release a replacement lease.
            if (AmbientEnded != null)
                foreach (Delegate observer in AmbientEnded.GetInvocationList())
                    try { ((Action<long, string>)observer)(ended, LastAmbientEndReason); }
                    catch (Exception e) { Debug.LogWarning("[Ambient] Cleanup failed: " + e.GetType().Name, this); }
        }
    }
}
