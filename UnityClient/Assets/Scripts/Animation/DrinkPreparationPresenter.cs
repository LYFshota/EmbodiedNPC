using EmbodiedNPC.Actions;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Read-only projection of the real preparation interaction onto one masked Animator layer.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(30)]
    public sealed class DrinkPreparationPresenter : MonoBehaviour
    {
        public const string LayerName = "DrinkPreparationUpperBody";
        public const string StateName = LayerName + ".PrepareDrink";
        [SerializeField] private GoalPlanRunner runner;
        [SerializeField] private MotionChannelCoordinator motion;
        [SerializeField] private DrinkPreparationCapability capability;
        [SerializeField] private Animator animator;
        [SerializeField] private ParticleSystem steam;
        [SerializeField] private GameObject preparationProp;
        private InteractionLayerPlayback playback;
        public float CurrentWeight => playback?.Weight ?? 0;
        public bool IsPlaying => playback?.IsPlaying == true;
        public bool IsReady => playback?.IsReady == true;
        public bool SteamActive => steam != null && steam.isPlaying;
        public bool PropVisible => preparationProp != null && preparationProp.activeInHierarchy;

        public void Configure(GoalPlanRunner task, MotionChannelCoordinator channels, DrinkPreparationCapability station,
            Animator character, ParticleSystem effect = null, GameObject prop = null)
        {
            Unsubscribe(); ResetPresentation(); runner = task; motion = channels; capability = station;
            animator = character; steam = effect; preparationProp = prop; Initialize();
            ResetPresentation();
            if (isActiveAndEnabled) Subscribe();
        }
        private void Awake() => Initialize();
        private void OnEnable() { Initialize(); Subscribe(); }
        private void Subscribe() { if (runner != null) { runner.GoalOutcomePublished -= OnOutcome; runner.GoalOutcomePublished += OnOutcome; } }
        private void Unsubscribe() { if (runner != null) runner.GoalOutcomePublished -= OnOutcome; }
        private void OnOutcome(GoalOutcome outcome) => ResetPresentation();
        private void Initialize()
        { playback?.Reset(); playback = new InteractionLayerPlayback(animator, LayerName, "PrepareDrink", preparationProp, steam); }
        public void Tick(float deltaTime)
        {
            var task = runner == null ? null : runner.CurrentTaskFeedback;
            bool prepare = isActiveAndEnabled && IsReady && runner != null && runner.isActiveAndEnabled && runner.IsExecuting &&
                task != null && task.ActionId == "prepare_drink" && task.Progress.Action == PlannerActionType.PrepareDrink &&
                capability != null && capability.IsAuthorized && motion != null && motion.isActiveAndEnabled &&
                motion.IsInteractionMotionActive && !motion.IsEmergencyMotionActive && !motion.IsNavigationActive &&
                !motion.IsCarryPresentationActive;
            playback?.Tick(prepare, deltaTime);
        }
        public void ResetPresentation() => playback?.Reset();
        private void Update() => Tick(Time.deltaTime);
        private void OnDisable() { Unsubscribe(); ResetPresentation(); }
    }
}
