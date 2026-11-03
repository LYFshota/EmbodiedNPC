using EmbodiedNPC.Animation;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.Routines;
using UnityEngine;

namespace CafeDemo
{
    /// <summary>Executes one selected local presentation. Selection, modes and cooldowns belong to DailyBehavior.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(155)]
    public sealed class CafeAmbientPresenter : MonoBehaviour, IDailyAmbientPresenter
    {
        [SerializeField] private Animator animator;
        [SerializeField] private MotionChannelCoordinator motion;
        [SerializeField] private ReturnToHomeRoutine navigation;
        [SerializeField] private CounterCleaningPresenter cleaning;
        [SerializeField] private Transform cup;
        [SerializeField] private Transform tableAnchor;
        [SerializeField] private Transform handAnchor;
        [SerializeField, Tooltip("Cup position in the evaluated hand's local space, calibrated at contact.")] private Vector3 gripPosition;
        [SerializeField] private Vector3 gripRotation;
        [SerializeField, Range(0, 30)] private float sipTiltDegrees = 12;
        public float SipTiltDegrees => sipTiltDegrees;
        [SerializeField] private string drinkLayer = "AmbientDrinkArms";
        [SerializeField] private float drinkSeconds = 6.2f;
        [SerializeField] private float contactSeconds = 1.1f;
        [SerializeField] private float releaseSeconds = 5.1f;
        private Transform rightHand, originalParent, workPoint;
        private Vector3 originalPosition, originalScale;
        private Quaternion originalRotation;
        private int layer = -1, drinkHash, emptyHash;
        private long instance;
        private DailyAmbientActivity activity;
        private float elapsed, dwell, layerBaseline;
        private bool capturedCup, attached, released, arrived;
        private bool recovering, fadingRecovery;
        private float recoveryFade, recoveryWeight;
        public bool IsRecovering => recovering;
        public long Instance => instance;
        public bool IsCupAttached => attached;
        public float Elapsed => elapsed;
        public Transform Cup => cup;
        public string Phase => instance == 0 ? "idle" : activity != DailyAmbientActivity.SeatedDrink ?
            (activity == DailyAmbientActivity.CounterWipe ? "wipe" : arrived ? "dwell" : "move") :
            recovering ? "recover" : elapsed < contactSeconds ? "reach" : elapsed < 2.2f ? "lift" : elapsed < 4.1f ? "sip" : elapsed < releaseSeconds ? "lower" : "recover";
        private void Awake() => Resolve();
        private void Resolve()
        {
            if (motion == null) motion = GetComponent<MotionChannelCoordinator>();
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid)
            {
                rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                layer = animator.runtimeAnimatorController == null ? -1 : animator.GetLayerIndex(drinkLayer);
            }
            drinkHash = Animator.StringToHash(drinkLayer + ".Drink"); emptyHash = Animator.StringToHash(drinkLayer + ".Empty");
        }
        public bool CanPresent(DailyAmbientActivity kind)
        {
            if (!isActiveAndEnabled || instance != 0 || animator == null || !animator.isActiveAndEnabled || !animator.isInitialized) return false;
            return kind switch {
                DailyAmbientActivity.CounterMove => navigation != null && navigation.isActiveAndEnabled,
                DailyAmbientActivity.CounterWipe => cleaning != null && cleaning.isActiveAndEnabled && cleaning.IsAmbientReady,
                DailyAmbientActivity.SeatedDrink => cup != null && cup.gameObject.activeInHierarchy && tableAnchor != null && handAnchor != null && rightHand != null &&
                    cup.GetComponentInParent<HeldItemState>() == null && layer >= 0 && animator.HasState(layer, drinkHash) &&
                    animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.SeatedIdle"),
                _ => false };
        }
        public bool BeginAmbient(DailyAmbientActivity kind, long id, Transform point, float dwellSeconds)
        {
            if (!CanPresent(kind) || motion == null || !motion.OwnsAmbient(this, id)) return false;
            instance = id; activity = kind; workPoint = point; dwell = dwellSeconds;
            elapsed = 0; arrived = attached = released = false;
            recovering = fadingRecovery = false; recoveryFade = 0;
            if (kind == DailyAmbientActivity.CounterMove) return navigation.BeginAmbientDestination(point, this, id);
            if (kind == DailyAmbientActivity.CounterWipe) return cleaning.BeginAmbient(this, id);
            originalParent = cup.parent; originalPosition = cup.localPosition; originalRotation = cup.localRotation;
            originalScale = cup.localScale; capturedCup = true;
            layerBaseline = animator.GetLayerWeight(layer);
            animator.Play(drinkHash, layer, 0); animator.SetLayerWeight(layer, 0);
            return true;
        }
        public bool IsContextValid(long id) => id != 0 && id == instance && isActiveAndEnabled && animator != null && animator.isActiveAndEnabled &&
            (activity == DailyAmbientActivity.SeatedDrink ? cup != null && cup.gameObject.activeInHierarchy && tableAnchor != null && tableAnchor.gameObject.activeInHierarchy &&
                handAnchor != null && rightHand != null && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.SeatedIdle") :
                workPoint != null && workPoint.gameObject.activeInHierarchy &&
                (activity == DailyAmbientActivity.CounterMove ? navigation != null && navigation.isActiveAndEnabled && !navigation.IsWaitingForNavigation :
                    cleaning != null && cleaning.isActiveAndEnabled && cleaning.IsAmbientReady));
        public bool TickAmbient(long id, float deltaTime)
        {
            if (id != instance || motion == null || !motion.OwnsAmbient(this, id)) return false;
            if (activity == DailyAmbientActivity.CounterMove)
            {
                if (!arrived) { arrived = navigation.IsAtDailyDestination; if (!arrived) return false; }
                elapsed += deltaTime; return elapsed >= dwell;
            }
            if (activity == DailyAmbientActivity.CounterWipe)
            {
                elapsed += deltaTime;
                if (elapsed >= dwell || recovering) cleaning.FinishAmbient(id);
                return cleaning.IsAmbientFinished;
            }
            if (fadingRecovery)
            {
                recoveryFade += deltaTime;
                animator.SetLayerWeight(layer, Mathf.Lerp(recoveryWeight, 0, Mathf.Clamp01(recoveryFade / .35f)));
                return recoveryFade >= .35f;
            }
            // Read the actual evaluated clip clock. A separate Update timer can get ahead
            // of Animator on its first frame and release the cup before the hand arrives.
            var state = animator.GetCurrentAnimatorStateInfo(layer);
            if (state.fullPathHash != drinkHash) return false;
            elapsed = state.normalizedTime * drinkSeconds;
            if (!attached && !released && elapsed >= contactSeconds)
            {
                // Follow the authored hand anchor without moving a scene prop into the actor's
                // hierarchy/scene. Unloading the actor must not destroy the borrowed cup.
                attached = true; FollowHand();
            }
            if (!released && !animator.IsInTransition(layer) && elapsed >= releaseSeconds) { RestoreCup(); released = true; }
            float weight = Mathf.Min(Mathf.Clamp01(elapsed / .25f), Mathf.Clamp01((drinkSeconds - elapsed) / .5f));
            animator.SetLayerWeight(layer, weight);
            return elapsed >= drinkSeconds;
        }
        public bool TryBeginRecovery(long id)
        {
            if (id == 0 || id != instance || motion == null || !motion.OwnsAmbient(this, id) || !IsContextValid(id)) return false;
            if (recovering) return true;
            if (activity == DailyAmbientActivity.CounterWipe)
            { recovering = true; cleaning.FinishAmbient(id); return true; }
            if (activity != DailyAmbientActivity.SeatedDrink) return false;
            recovering = true;
            if (!attached)
            { fadingRecovery = true; recoveryWeight = animator.GetLayerWeight(layer); return true; }
            if (elapsed < 4.1f)
            {
                // Blend into the existing lowering tail at a comparable height. Keep
                // the same arms lease and rigid hand grip until the hand reaches the table.
                float at = Mathf.Lerp(releaseSeconds, 4.1f, Mathf.InverseLerp(contactSeconds, 2.4f, elapsed));
                animator.CrossFadeInFixedTime(drinkHash, .18f, layer, Mathf.Max(0, at - .18f));
            }
            return true;
        }
        private void FollowHand()
        {
            if (rightHand == null || handAnchor == null) return;
            handAnchor.SetPositionAndRotation(rightHand.TransformPoint(gripPosition),
                rightHand.rotation * Quaternion.Euler(gripRotation));
            if (attached && cup != null) cup.SetPositionAndRotation(handAnchor.position, handAnchor.rotation);
        }
        private void LateUpdate()
        {
            if (attached && motion != null && motion.OwnsAmbient(this, instance)) FollowHand();
        }
        private void RestoreCup()
        {
            if (!capturedCup || cup == null) { attached = false; return; }
            cup.SetParent(originalParent, false); cup.localPosition = originalPosition;
            cup.localRotation = originalRotation; cup.localScale = originalScale; attached = false;
        }
        public void EndAmbient(long id)
        {
            if (id == 0 || id != instance) return;
            instance = 0;
            if (activity == DailyAmbientActivity.CounterMove) navigation?.EndAmbientDestination(id, arrived);
            if (activity == DailyAmbientActivity.CounterWipe) cleaning?.EndAmbient(id);
            if (activity == DailyAmbientActivity.SeatedDrink)
            {
                RestoreCup(); capturedCup = false;
                if (animator != null && animator.isInitialized && layer >= 0 && layer < animator.layerCount)
                { animator.SetLayerWeight(layer, layerBaseline); animator.Play(emptyHash, layer, 0); }
            }
            elapsed = 0; workPoint = null; recovering = fadingRecovery = false;
        }
        private void OnDisable()
        {
            long old = instance;
            if (old != 0) { motion?.CompleteAmbient(this, old, "presenter_disabled"); EndAmbient(old); }
        }
    }
}
