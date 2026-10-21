using System;
using System.Collections.Generic;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    public enum DailyBehaviorMode { Work, Rest, Leisure }
    // Preserve the serialized P0 values. Environment observation is an overlay, not a locomotion job.
    public enum DailyBehaviorActivity { HomeIdle, WindowIdle, SeatedIdle, NaturalIdle, ObserveEnvironment }

    [Serializable]
    public sealed class DailyBehaviorGoal
    {
        public DailyBehaviorMode mode;
        public string objective;
        public string tendency;
        [HideInInspector] public string[] consideredActivities; // Legacy authoring; wire activities come from bindings.
        public DailyBehaviorGoal(DailyBehaviorMode mode, string objective, string tendency, params string[] activities)
        { this.mode = mode; this.objective = objective; this.tendency = tendency; consideredActivities = activities; }
    }

    [Serializable]
    public sealed class DailyBehaviorActivityBinding
    {
        public DailyBehaviorMode mode;
        public DailyBehaviorActivity activity;
        public string objective;
        public string Id => activity switch {
            DailyBehaviorActivity.HomeIdle => "home_idle", DailyBehaviorActivity.WindowIdle => "window_idle",
            DailyBehaviorActivity.SeatedIdle => "seated_idle", DailyBehaviorActivity.NaturalIdle => "natural_idle",
            DailyBehaviorActivity.ObserveEnvironment => "observe_environment", _ => string.Empty };
        public bool IsOverlay => activity == DailyBehaviorActivity.ObserveEnvironment;
        public string ExecutionRequirement => activity switch {
            DailyBehaviorActivity.HomeIdle => "ReturnToHomeRoutine: original home, complete NavMesh path, free motion channel",
            DailyBehaviorActivity.WindowIdle => "ReturnToHomeRoutine: active rest/look anchors, complete NavMesh path, free motion channel",
            DailyBehaviorActivity.SeatedIdle => "Existing seated Idle; preserve seat and never acquire navigation",
            DailyBehaviorActivity.NaturalIdle => "Existing standing Idle in place; no navigation or new animation",
            DailyBehaviorActivity.ObserveEnvironment => "AttentionController overlay: bounded unobstructed look point, free gaze",
            _ => string.Empty };
        public DailyBehaviorActivityBinding(DailyBehaviorMode mode, DailyBehaviorActivity activity)
        {
            this.mode = mode; this.activity = activity;
            objective = activity switch {
                DailyBehaviorActivity.HomeIdle => "在工作区域待机。", DailyBehaviorActivity.WindowIdle => "在窗边放松观景。",
                DailyBehaviorActivity.SeatedIdle => "保持已有座位自然待机。", DailyBehaviorActivity.NaturalIdle => "在当前位置自然待机。",
                _ => "低频观察周围环境，随时让出交谈。" };
        }
    }

    [Serializable]
    public sealed class DailyBehaviorTransition
    {
        public DailyBehaviorMode from;
        public DailyBehaviorMode to;
        public string systemEventType;

        public DailyBehaviorTransition(DailyBehaviorMode from, DailyBehaviorMode to, string eventType)
        { this.from = from; this.to = to; systemEventType = eventType; }
    }

    /// <summary>Authored local life modes. Never grants an action capability or changes EventInterest.</summary>
    [CreateAssetMenu(menuName = "EmbodiedNPC/Daily Behavior Profile")]
    public sealed class DailyBehaviorProfile : ScriptableObject
    {
        [SerializeField] private DailyBehaviorMode defaultMode = DailyBehaviorMode.Work;
        [SerializeField] private DailyBehaviorMode[] allowedModes = { DailyBehaviorMode.Work, DailyBehaviorMode.Rest };
        [SerializeField] private DailyBehaviorTransition[] transitions = Array.Empty<DailyBehaviorTransition>();
        [SerializeField] private DailyBehaviorActivityBinding[] activities = {
            new(DailyBehaviorMode.Work, DailyBehaviorActivity.HomeIdle),
            new(DailyBehaviorMode.Work, DailyBehaviorActivity.NaturalIdle),
            new(DailyBehaviorMode.Work, DailyBehaviorActivity.ObserveEnvironment),
            new(DailyBehaviorMode.Rest, DailyBehaviorActivity.WindowIdle),
            new(DailyBehaviorMode.Rest, DailyBehaviorActivity.NaturalIdle),
            new(DailyBehaviorMode.Rest, DailyBehaviorActivity.ObserveEnvironment) };
        [SerializeField, Min(0)] private float minimumModeSeconds = 1f;
        [SerializeField, Min(0)] private float transitionCooldown = 1f;
        [SerializeField] private DailyBehaviorGoal[] goals = {
            new(DailyBehaviorMode.Work, "履行店员职责，在现有能力内满足合理需求。", "关注顾客、工作区域和当前服务需求。",
                "home_idle", "natural_idle", "observe_environment", "dialogue", "existing_legal_work_skills"),
            new(DailyBehaviorMode.Rest, "进行个人休闲；可根据职责和当前请求恢复工作。", "休息、观景和普通交谈。",
                "window_idle", "natural_idle", "observe_environment", "dialogue"),
            new(DailyBehaviorMode.Leisure, "在当前环境中休闲并进行普通交谈。", "保持舒适坐姿，适度观察周围。",
                "seated_idle", "natural_idle", "observe_environment", "dialogue") };
        // Binding order is deterministic preference. It is not an action manifest or a weighted planner.
        public DailyBehaviorMode DefaultMode => defaultMode;
        public float MinimumModeSeconds => minimumModeSeconds;
        public float TransitionCooldown => transitionCooldown;
        public bool Allows(DailyBehaviorMode mode) => allowedModes != null && Array.IndexOf(allowedModes, mode) >= 0;
        public IEnumerable<DailyBehaviorActivityBinding> ActivitiesFor(DailyBehaviorMode mode)
        {
            if (!Allows(mode) || activities == null) yield break;
            foreach (var binding in activities)
                if (binding != null && binding.mode == mode) yield return binding;
        }
        public bool AllowsActivity(DailyBehaviorMode mode, DailyBehaviorActivity activity)
        {
            foreach (var binding in ActivitiesFor(mode)) if (binding.activity == activity) return true;
            return false;
        }
        public string[] ConsideredActivitiesFor(DailyBehaviorMode mode)
        {
            var result = new List<string>();
            foreach (var binding in ActivitiesFor(mode)) result.Add(binding.Id);
            return result.ToArray();
        }
        public DailyBehaviorGoal GoalFor(DailyBehaviorMode mode)
        {
            if (!Allows(mode) || goals == null) return null;
            foreach (var goal in goals) if (goal != null && goal.mode == mode) return goal;
            return null;
        }
        public bool AllowsCognitiveTransition(DailyBehaviorMode from, DailyBehaviorMode to) => from != to &&
            AllowsTransition(from, to, to == DailyBehaviorMode.Rest ? Events.AgentEventTypes.WorkBreakStarted :
                to == DailyBehaviorMode.Work ? Events.AgentEventTypes.WorkBreakEnded : string.Empty);
        public DailyBehaviorActivity ActivityFor(DailyBehaviorMode mode)
        {
            foreach (var binding in ActivitiesFor(mode))
                if (!binding.IsOverlay) return binding.activity;
            return DailyBehaviorActivity.SeatedIdle;
        }

        public bool AllowsTransition(DailyBehaviorMode from, DailyBehaviorMode to, string eventType)
        {
            if (!Allows(to)) return false;
            foreach (var edge in transitions)
                if (edge != null && edge.from == from && edge.to == to && edge.systemEventType == eventType) return true;
            return false;
        }

        public bool IsValid => allowedModes != null && allowedModes.Length > 0 && Allows(defaultMode) &&
            transitions != null && activities != null && ActivitiesValid && GoalsValid && float.IsFinite(minimumModeSeconds) && minimumModeSeconds >= 0 &&
            float.IsFinite(transitionCooldown) && transitionCooldown >= 0;
        private bool ActivitiesValid
        {
            get
            {
                foreach (var mode in allowedModes)
                {
                    var seen = new HashSet<DailyBehaviorActivity>(); bool primary = false;
                    foreach (var binding in ActivitiesFor(mode))
                    {
                        if (string.IsNullOrEmpty(binding.Id) || !seen.Add(binding.activity) ||
                            (binding.activity == DailyBehaviorActivity.HomeIdle && mode != DailyBehaviorMode.Work) ||
                            (binding.activity == DailyBehaviorActivity.WindowIdle && mode != DailyBehaviorMode.Rest) ||
                            (binding.activity == DailyBehaviorActivity.SeatedIdle && mode != DailyBehaviorMode.Leisure) ||
                            (binding.activity == DailyBehaviorActivity.NaturalIdle && mode == DailyBehaviorMode.Leisure)) return false;
                        primary |= !binding.IsOverlay;
                    }
                    if (!primary || seen.Count > 6) return false;
                }
                return true;
            }
        }
        private bool GoalsValid
        {
            get
            {
                foreach (var mode in allowedModes)
                {
                    var goal = GoalFor(mode);
                    if (goal == null || string.IsNullOrWhiteSpace(goal.objective) || goal.objective.Length > 160 ||
                        string.IsNullOrWhiteSpace(goal.tendency) || goal.tendency.Length > 160 ||
                        goal.consideredActivities == null || goal.consideredActivities.Length > 6) return false;
                    foreach (string activity in goal.consideredActivities)
                        if (string.IsNullOrWhiteSpace(activity) || activity.Length > 48) return false;
                }
                return true;
            }
        }

        public void Configure(DailyBehaviorMode initial, DailyBehaviorMode[] modes,
            DailyBehaviorTransition[] edges, float minimumSeconds = 1f, float cooldown = 1f)
        {
            defaultMode = initial; allowedModes = modes; transitions = edges;
            minimumModeSeconds = minimumSeconds; transitionCooldown = cooldown;
            var choices = new List<DailyBehaviorActivityBinding>();
            foreach (var mode in modes)
            {
                choices.Add(new(mode, mode == DailyBehaviorMode.Work ? DailyBehaviorActivity.HomeIdle :
                    mode == DailyBehaviorMode.Rest ? DailyBehaviorActivity.WindowIdle : DailyBehaviorActivity.SeatedIdle));
                if (mode != DailyBehaviorMode.Leisure) choices.Add(new(mode, DailyBehaviorActivity.NaturalIdle));
                choices.Add(new(mode, DailyBehaviorActivity.ObserveEnvironment));
            }
            activities = choices.ToArray();
        }
    }
}
