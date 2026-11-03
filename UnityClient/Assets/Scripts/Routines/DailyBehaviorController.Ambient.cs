using EmbodiedNPC.Animation;
using EmbodiedNPC.Cognition;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    public sealed partial class DailyBehaviorController
    {
        [SerializeField] private bool ambientEnabled;
        [SerializeField] private MonoBehaviour ambientPresenter;
        [SerializeField] private DailyAmbientWorkPoint[] ambientWorkPoints = System.Array.Empty<DailyAmbientWorkPoint>();
        [SerializeField, Min(.1f)] private float ambientIntervalScale = 1;
        private IDailyAmbientPresenter ambientExecution;
        private MotionChannelCoordinator ambientMotion;
        private TaskConsiderationSource ambientTasks;
        private long ambientInstance;
        private double ambientNextAt, ambientDeadline;
        private DailyAmbientActivity ambientActivity;
        private int lastAmbientPoint = -1;
        private bool pendingCounterWipe;
        public bool AmbientEnabled { get => ambientEnabled; set { ambientEnabled = value; if (!value) StopAmbient("disabled"); } }
        public long AmbientInstance => ambientInstance;
        public DailyAmbientActivity? ActiveAmbientActivity => ambientInstance == 0 ? null : ambientActivity;
        public int AmbientStartedCount { get; private set; }
        public int AmbientCompletedCount { get; private set; }
        public string LastAmbientReason { get; private set; }

        private void InitializeAmbient()
        {
            ambientExecution = ambientPresenter as IDailyAmbientPresenter;
            ambientMotion = GetComponent<MotionChannelCoordinator>();
            ambientTasks = GetComponent<TaskConsiderationSource>();
            ScheduleAmbient();
        }
        private void SubscribeAmbient()
        {
            if (ambientMotion == null) ambientMotion = GetComponent<MotionChannelCoordinator>();
            if (ambientMotion != null) { ambientMotion.AmbientEnded -= OnAmbientEnded; ambientMotion.AmbientEnded += OnAmbientEnded; }
        }
        private void UnsubscribeAmbient() { if (ambientMotion != null) ambientMotion.AmbientEnded -= OnAmbientEnded; }
        private static float Sample(Vector2 range) => Random.Range(Mathf.Max(.1f, range.x), Mathf.Max(.1f, range.x, range.y));
        private void ScheduleAmbient() => ambientNextAt = Time.unscaledTimeAsDouble +
            Sample(pendingCounterWipe ? new Vector2(1.5f, 3.5f) : profile == null ? new Vector2(22, 44) : profile.AmbientInterval) * Mathf.Max(.1f, ambientIntervalScale);
        private void RevalidateAmbient()
        {
            if (ambientInstance != 0 && (!ambientEnabled || profile == null || !profile.AllowsAmbient(CurrentMode, ambientActivity) ||
                ambientExecution == null || ambientPresenter == null || !ambientPresenter.isActiveAndEnabled ||
                !ambientExecution.IsContextValid(ambientInstance))) StopAmbient("mode_or_context_changed");
        }
        private void TickAmbient()
        {
            if (!ambientEnabled) { StopAmbient("disabled"); return; }
            if (ambientExecution == null || ambientMotion == null || admission == null) return;
            if (Time.deltaTime <= 0) return;
            if (ambientInstance != 0)
            {
                RevalidateAmbient();
                if (ambientInstance == 0) return;
                if (admission.IsAmbientActivityBlocked || !ambientMotion.OwnsAmbient(ambientPresenter, ambientInstance))
                {
                    if (ambientMotion.OwnsAmbient(ambientPresenter, ambientInstance) && admission.IsConversationBlockingAmbient)
                        ambientMotion.RequestAmbientEnd("conversation");
                    else StopAmbient("higher_priority");
                    if (ambientInstance == 0) return;
                }
                if (ambientMotion.IsAmbientNavigationPaused)
                {
                    routine?.SuspendRoutine();
                    // Conversation time does not spend the bounded travel deadline.
                    ambientDeadline = Time.unscaledTimeAsDouble + 45;
                    if (admission.IsAmbientActivityBlocked) return;
                    if (!ambientMotion.ResumeAmbientNavigation(ambientPresenter, ambientInstance)) return;
                }
                if (Time.unscaledTimeAsDouble > ambientDeadline) { StopAmbient("timeout"); return; }
                try
                {
                    if (ambientExecution.TickAmbient(ambientInstance, Time.deltaTime))
                    { AmbientCompletedCount++; StopAmbient("completed"); }
                }
                catch (System.Exception e)
                { StopAmbient("execution_exception"); Debug.LogException(e, this); }
                return;
            }
            if (profile == null || !profile.IsValid || admission.IsAmbientActivityBlocked || !CurrentActivity.HasValue ||
                ambientTasks != null && ambientTasks.isActiveAndEnabled && ambientTasks.HasPendingOpportunity)
            { ScheduleAmbient(); return; }
            if (Time.unscaledTimeAsDouble < ambientNextAt) return;
            ScheduleAmbient();
            if (pendingCounterWipe)
            {
                if (profile.AllowsAmbient(CurrentMode, DailyAmbientActivity.CounterWipe) && SelectWorkPoint(DailyAmbientActivity.CounterWipe) != null)
                {
                    if (TryStartAmbient(DailyAmbientActivity.CounterWipe)) pendingCounterWipe = false;
                    return;
                }
                pendingCounterWipe = false;
            }
            var choices = profile.AmbientActivities;
            if (choices == null || choices.Length == 0) return;
            int start = Random.Range(0, choices.Length);
            for (int offset = 0; offset < choices.Length; offset++)
            {
                var binding = choices[(start + offset) % choices.Length];
                if (binding != null && profile.AllowsAmbient(CurrentMode, binding.activity) && TryStartAmbient(binding.activity)) return;
            }
        }
        private Transform SelectWorkPoint(DailyAmbientActivity activity)
        {
            if (ambientWorkPoints == null || ambientWorkPoints.Length == 0) return null;
            int start = Random.Range(0, ambientWorkPoints.Length);
            for (int offset = 0; offset < ambientWorkPoints.Length; offset++)
            {
                int index = (start + offset) % ambientWorkPoints.Length;
                var candidate = ambientWorkPoints[index];
                if (candidate?.point == null || !candidate.point.gameObject.activeInHierarchy) continue;
                float distance = Vector3.ProjectOnPlane(candidate.point.position - transform.position, Vector3.up).magnitude;
                if (activity == DailyAmbientActivity.CounterWipe)
                { if (candidate.allowWipe && distance < .3f) return candidate.point; }
                else if (index != lastAmbientPoint && distance > .35f)
                { lastAmbientPoint = index; return candidate.point; }
            }
            return null;
        }
        // Same admission/selection boundary used by deterministic scene acceptance. Never submits a world action.
        public bool TryStartAmbient(DailyAmbientActivity activity)
        {
            bool navigation = activity == DailyAmbientActivity.CounterMove;
            if (!isActiveAndEnabled || !ambientEnabled || ambientInstance != 0 || profile == null ||
                !profile.AllowsAmbient(CurrentMode, activity) || ambientExecution == null || !ambientExecution.CanPresent(activity) ||
                admission == null || !admission.CanStartAmbient(navigation) || ambientMotion == null ||
                ambientTasks != null && ambientTasks.isActiveAndEnabled && ambientTasks.HasPendingOpportunity) return false;
            Transform point = activity == DailyAmbientActivity.SeatedDrink ? null : SelectWorkPoint(activity);
            if (activity != DailyAmbientActivity.SeatedDrink && point == null) return false;
            if (!ambientMotion.TryBeginAmbient(ambientPresenter, activity.ToString(), navigation ? AmbientBodyUse.Navigation : AmbientBodyUse.Arms, out long id)) return false;
            ambientInstance = id; ambientActivity = activity; ambientDeadline = Time.unscaledTimeAsDouble + 45;
            routine?.SuspendRoutine();
            bool began;
            try { began = ambientExecution.BeginAmbient(activity, id, point, Sample(profile.AmbientDwell)); }
            catch { StopAmbient("begin_exception"); throw; }
            if (!began) { StopAmbient("unavailable"); return false; }
            AmbientStartedCount++; LastAmbientReason = "started"; return true;
        }
        public void StopAmbient(string reason)
        {
            long id = ambientInstance;
            if (id == 0) return;
            if (ambientMotion != null && ambientMotion.CompleteAmbient(ambientPresenter, id, reason)) return;
            OnAmbientEnded(id, reason);
        }
        private void OnAmbientEnded(long id, string reason)
        {
            if (id != ambientInstance || id == 0) return;
            bool reachedWorkSurface = reason == "completed" && ambientActivity == DailyAmbientActivity.CounterMove &&
                profile != null && profile.AllowsAmbient(CurrentMode, DailyAmbientActivity.CounterWipe) &&
                SelectWorkPoint(DailyAmbientActivity.CounterWipe) != null;
            ambientInstance = 0; LastAmbientReason = reason;
            try { ambientExecution?.EndAmbient(id); }
            finally { pendingCounterWipe = reachedWorkSurface; ScheduleAmbient(); }
        }
    }
}
