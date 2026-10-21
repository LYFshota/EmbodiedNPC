using System;
using EmbodiedNPC.Animation;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CafeDemo
{
    [DisallowMultipleComponent]
    public sealed class PlayerEmoteController : MonoBehaviour
    {
        public const string LayerName = "EmoteUpperBody";
        private const string WaveState = LayerName + ".Wave";
        private const string EmptyState = LayerName + ".Empty";
        [SerializeField] private CafeDemoPlayerController movement;
        [SerializeField] private WorldEntity actor;
        [SerializeField] private Animator animator;
        [SerializeField] private InputActionAsset inputActions;
        private InputAction wave;
        private int layer = -1;
        private bool published;
        private float requestedAt;
        private string activeEmote = "wave";
        private string activeState = WaveState;
        private DialogueParticipant emoteFocus;
        private readonly RaycastHit[] socialHits = new RaycastHit[8];
        public bool IsActive { get; private set; }
        public bool MenuHandlesInput { get; set; }
        public string ActiveEmote => IsActive ? activeEmote : string.Empty;
        public event Action<bool> ActivityChanged;
        public event Action<PlayerEmotePerformed> EmotePerformed;

        private void OnEnable()
        {
            layer = animator == null ? -1 : animator.GetLayerIndex(LayerName);
            wave = inputActions == null ? null : inputActions.FindAction("Player/Wave", true).Clone();
            wave?.Enable();
        }
        public bool TryWave() => TryPlay("wave");
        public bool TryPlay(string emoteId)
        {
            string state = emoteId == "wave" ? WaveState : emoteId == "cheer" ? LayerName + ".Cheer" :
                emoteId == "proud" || emoteId == "flex" ? LayerName + ".Proud" : null;
            if (!isActiveAndEnabled || IsActive || movement == null || !movement.isActiveAndEnabled ||
                movement.IsInputSuspended || actor == null || !actor.IsAgent(AgentRole.Player) ||
                string.IsNullOrWhiteSpace(actor.EntityId) || animator == null || !animator.isActiveAndEnabled ||
                !animator.isInitialized || animator.speed <= 0 || layer < 0 || state == null ||
                !animator.HasState(layer, Animator.StringToHash(state))) return false;
            activeEmote = emoteId; activeState = state;
            // Capture before the panel's activity lock clears the normal interaction focus.
            emoteFocus = GetComponent<PlayerDialogueInteractor>()?.CurrentCandidate;
            IsActive = true; published = false; requestedAt = Time.unscaledTime;
            animator.CrossFadeInFixedTime(activeState, .1f, layer, 0f);
            ActivityChanged?.Invoke(true);
            return true;
        }
        private void Update()
        {
            // The scene's panel owns G when present; direct TryWave remains available to existing callers.
            if (!MenuHandlesInput && wave != null && wave.WasPressedThisFrame()) TryWave();
            if (!IsActive) return;
            if (animator == null || !animator.isActiveAndEnabled || !animator.isInitialized) { ResetWave(); return; }
            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
            bool inWave = current.IsName(activeState);
            bool enteringWave = animator.IsInTransition(layer) && animator.GetNextAnimatorStateInfo(layer).IsName(activeState);
            bool leavingWave = inWave && animator.IsInTransition(layer) && !enteringWave;
            float target = leavingWave || (inWave && current.normalizedTime >= 1f) ? 0f : 1f;
            animator.SetLayerWeight(layer, Mathf.MoveTowards(animator.GetLayerWeight(layer), target, Time.deltaTime / .12f));
            if (inWave && !published && animator.GetLayerWeight(layer) > 0f)
            {
                published = true;
                PublishVisualStart();
            }
            if ((published && !inWave && !enteringWave) || (!published && Time.unscaledTime - requestedAt > 1f) ||
                Time.unscaledTime - requestedAt > 8f) ResetWave();
        }
        private void PublishVisualStart()
        {
            string id = activeEmote == "proud" ? "flex" : activeEmote;
            var observers = FindObjectsByType<PlayerEmoteAgentEventBridge>(FindObjectsSortMode.None);
            foreach (var bridge in observers) bridge.RefreshVision();
            var dialogue = FindAnyObjectByType<DialogueController>();
            WorldEntity target = null;
            if (id == "wave")
            {
                // A camera-selected focus is only a hint: actual body facing and LOS must agree.
                foreach (var bridge in observers)
                    if (bridge.Observer != null && emoteFocus != null && bridge.Observer == emoteFocus.Entity &&
                        IsWaveCandidate(bridge.Observer, dialogue, 30f)) { target = bridge.Observer; break; }
                if (target == null)
                {
                    float bestAngle = float.PositiveInfinity, secondAngle = float.PositiveInfinity;
                    foreach (var bridge in observers)
                    {
                        WorldEntity candidate = bridge.Observer;
                        if (!IsWaveCandidate(candidate, dialogue, 25f)) continue;
                        float angle = Vector3.Angle(Vector3.ProjectOnPlane(actor.transform.forward, Vector3.up),
                            Vector3.ProjectOnPlane(candidate.transform.position - actor.transform.position, Vector3.up));
                        if (angle < bestAngle) { secondAngle = bestAngle; bestAngle = angle; target = candidate; }
                        else secondAngle = Mathf.Min(secondAngle, angle);
                    }
                    // Close directions are ambiguous; do not use distance to invent a target.
                    if (secondAngle - bestAngle < 8f) target = null;
                }
            }
            PlayerEmoteAgentEventBridge responder = null;
            float bestScore = float.PositiveInfinity;
            foreach (var bridge in observers)
            {
                WorldEntity candidate = bridge.Observer;
                if (candidate == null || !bridge.CanSee(actor.EntityId) || !bridge.CanRespond ||
                    (id == "wave" && candidate != target)) continue;
                float distance = Vector3.Distance(actor.transform.position, candidate.transform.position);
                if (distance > 4f) continue;
                var attention = candidate.GetComponent<AttentionController>();
                float score = distance - (attention != null && attention.IsLiveTracking &&
                    attention.CurrentTargetEntityId == actor.EntityId ? 2f : 0f);
                if (score < bestScore || (Mathf.Approximately(score, bestScore) &&
                    string.CompareOrdinal(candidate.EntityId, responder?.Observer.EntityId) < 0))
                { responder = bridge; bestScore = score; }
            }
            EmotePerformed?.Invoke(new PlayerEmotePerformed($"player-emote-{Guid.NewGuid():N}", actor.EntityId,
                id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, target?.EntityId, responder?.Observer.EntityId));
        }
        private bool IsWaveCandidate(WorldEntity candidate, DialogueController dialogue, float halfAngle)
        {
            if (candidate == null || !candidate.isActiveAndEnabled || dialogue == null) return false;
            var participant = candidate.GetComponent<DialogueParticipant>();
            var visible = candidate.GetComponent<PerceivableEntity>();
            var body = actor.GetComponent<CharacterController>();
            Vector3 eye = actor.transform.position + Vector3.up * (body == null ? 1.5f : body.height * .9f);
            return participant != null && dialogue.CheckEligibility(participant).IsEligible && visible != null &&
                visible.isActiveAndEnabled && VisionSensorMath.IsWithinRange(actor.transform.position, candidate.transform.position, 4f) &&
                VisionSensorMath.IsInsideHorizontalFov(eye, actor.transform.forward, visible.VisualTargetPosition, halfAngle) &&
                VisionSensor.HasClearLineOfSight(eye, visible.VisualTargetPosition, actor.transform, candidate.transform, ~0, socialHits);
        }
        public void Cancel() => ResetWave();
        private void ResetWave()
        {
            bool wasActive = IsActive;
            if (animator != null && animator.isInitialized && layer >= 0 && layer < animator.layerCount)
            {
                animator.SetLayerWeight(layer, 0f);
                animator.Play(EmptyState, layer, 0f);
            }
            IsActive = false; published = false; emoteFocus = null;
            if (wasActive) ActivityChanged?.Invoke(false);
        }
        private void OnDisable()
        {
            wave?.Disable(); wave?.Dispose(); wave = null;
            ResetWave();
        }
    }
}
