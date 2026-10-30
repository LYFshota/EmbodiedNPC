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
        public bool IsPlaying => playback?.IsPlaying == true;
        public bool IsReady => playback?.IsReady == true;
        public bool PropVisible => rag != null && rag.activeInHierarchy;
        public float CurrentWeight => playback?.Weight ?? 0;
        public void RebindAnimator(Animator character) => Configure(runner, motion, capability, character, rag);

        public void Configure(GoalPlanRunner task, MotionChannelCoordinator channels, CounterCleaningCapability station, Animator character, GameObject prop)
        { Unsubscribe(); playback?.Reset(); runner = task; motion = channels; capability = station; animator = character; rag = prop; Initialize(); if (isActiveAndEnabled) Subscribe(); }
        private void Initialize() { playback?.Reset(); playback = new InteractionLayerPlayback(animator, LayerName, "CleanCounter", rag); }
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
            playback?.Tick(active, deltaTime);
        }
        private void Update() => Tick(Time.deltaTime);
        private void OnDisable() { Unsubscribe(); playback?.Reset(); }
    }
}
