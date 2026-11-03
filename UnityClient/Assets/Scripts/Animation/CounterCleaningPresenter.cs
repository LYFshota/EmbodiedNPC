using EmbodiedNPC.Actions;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using UnityEngine;

namespace EmbodiedNPC.Animation
{
    [DisallowMultipleComponent, DefaultExecutionOrder(30)]
    public sealed class CounterCleaningPresenter : MonoBehaviour
    {
        public const string LayerName = "CounterCleaningUpperBody";
        [SerializeField] private GoalPlanRunner runner;
        [SerializeField] private MotionChannelCoordinator motion;
        [SerializeField] private CounterCleaningCapability capability;
        [SerializeField] private Animator animator;
        [SerializeField] private GameObject rag;
        private InteractionLayerPlayback playback;
        private MonoBehaviour ambientOwner;
        private long ambientId;
        private bool ambientFinishing;
        private Transform ambientFinger;
        private Vector3 ragRestPosition;
        private Quaternion ragRestRotation;
        private bool ragPoseCaptured;
        public bool BeginAmbient(MonoBehaviour owner, long id)
        {
            if (!IsAmbientReady || motion == null || !motion.OwnsAmbient(owner, id)) return false;
            ambientOwner = owner; ambientId = id; ambientFinishing = false;
            if (rag != null) { ragRestPosition = rag.transform.localPosition; ragRestRotation = rag.transform.localRotation; ragPoseCaptured = true; }
            return true;
        }
        public void EndAmbient(long id)
        {
            if (id == 0 || id != ambientId) return;
            ambientOwner = null; ambientId = 0;
            RestoreRagPose();
            if (runner == null || !runner.HasActiveTask) playback?.Reset();
        }
        public bool IsPlaying => playback?.IsPlaying == true;
        public bool IsReady => playback?.IsReady == true;
        public bool IsAmbientReady => isActiveAndEnabled && playback?.IsAlternateReady == true && ambientFinger != null;
        public Transform AmbientRag => rag == null ? null : rag.transform;
        public bool PropVisible => rag != null && rag.activeInHierarchy;
        public float CurrentWeight => playback?.Weight ?? 0;
        public float PlaybackSeconds => playback?.PlaybackSeconds ?? 0;
        public bool IsAmbientFinished => ambientFinishing && playback != null && !playback.IsTransitioning && PlaybackSeconds >= 2.5f;
        public void FinishAmbient(long id)
        {
            if (id != ambientId || id == 0 || ambientFinishing) return;
            ambientFinishing = true; playback?.FinishAlternate(1.9f);
        }
        public void RebindAnimator(Animator character) => Configure(runner, motion, capability, character, rag);

        public void Configure(GoalPlanRunner task, MotionChannelCoordinator channels, CounterCleaningCapability station, Animator character, GameObject prop)
        { Unsubscribe(); playback?.Reset(); runner = task; motion = channels; capability = station; animator = character; rag = prop; Initialize(); if (isActiveAndEnabled) Subscribe(); }
        private void Initialize()
        {
            RestoreRagPose(); playback?.Reset();
            ambientFinger = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightMiddleDistal) : null;
            playback = new InteractionLayerPlayback(animator, LayerName, "CleanCounter", rag, alternateState: "AmbientClean");
        }
        private void Awake() => Initialize();
        private void OnEnable() { if (playback == null) Initialize(); Subscribe(); }
        private void Subscribe() { if (runner != null) { runner.GoalOutcomePublished -= Outcome; runner.GoalOutcomePublished += Outcome; } }
        private void Unsubscribe() { if (runner != null) runner.GoalOutcomePublished -= Outcome; }
        private void Outcome(GoalOutcome outcome) => playback?.Reset();
        public void Tick(float deltaTime)
        {
            var task = runner == null ? null : runner.CurrentTaskFeedback;
            bool active = isActiveAndEnabled && runner != null && runner.isActiveAndEnabled && runner.IsExecuting &&
                task != null && task.ActionId == "clean_counter" && task.Progress.Action == PlannerActionType.CleanCounter &&
                capability != null && capability.IsAuthorized && motion != null && motion.isActiveAndEnabled && motion.IsInteractionMotionActive &&
                !motion.IsEmergencyMotionActive && !motion.IsNavigationActive && !motion.IsCarryPresentationActive;
            bool ambient = ambientId != 0 && motion != null && motion.OwnsAmbient(ambientOwner, ambientId) &&
                (runner == null || !runner.HasActiveTask) && !motion.IsNavigationActive;
            playback?.Tick(active || ambient, deltaTime, ambient && !active);
            if (ambient && !ambientFinishing) playback?.LoopAlternate(.7f, 1.9f);
        }
        private void Update() => Tick(Time.deltaTime);
        private void LateUpdate()
        {
            if (ambientId == 0 || motion == null || !motion.OwnsAmbient(ambientOwner, ambientId) || rag == null || ambientFinger == null) return;
            // VRM wrist origins are above the contact patch. Follow the real fingers and
            // keep the cloth flat; formal cleaning retains its original authored binding.
            rag.transform.SetPositionAndRotation(ambientFinger.position + Vector3.down * .02f, transform.rotation);
        }
        private void RestoreRagPose()
        {
            if (!ragPoseCaptured || rag == null) return;
            rag.transform.localPosition = ragRestPosition; rag.transform.localRotation = ragRestRotation; ragPoseCaptured = false;
        }
        private void OnDisable() { Unsubscribe(); ambientId = 0; ambientOwner = null; RestoreRagPose(); playback?.Reset(); }
    }
}
