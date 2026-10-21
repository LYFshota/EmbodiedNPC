using System;
using System.Collections.Generic;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Dialogue.Input;
using EmbodiedNPC.Perception;
using EmbodiedNPC.Planning;
using EmbodiedNPC.Planning.MiniGoap;
using EmbodiedNPC.Settings;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    /// <summary>Bounded visual projection only. Runner and GoalOutcome retain all task authority.</summary>
    [DisallowMultipleComponent]
    public sealed class ActionFeedbackPresenter : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;
        [SerializeField] private Camera sceneCamera;
        [SerializeField] private GoalPlanRunner[] runners = Array.Empty<GoalPlanRunner>();
        [SerializeField] private WorldEntity player;
        [SerializeField] private DialogueBubbleManager bubbles;
        [SerializeField] private RectTransform interactionPrompt;
        [SerializeField] private ActionFeedbackView template;
        [SerializeField, Min(1)] private float maximumDistance = 18;
        private sealed class Entry
        {
            public GoalPlanRunner Runner;
            public DialogueBubbleAnchor Anchor;
            public string Name;
            public TaskFeedbackSnapshot Snapshot;
            public ActionFeedbackView View;
        }
        private sealed class Toast
        {
            public string Name, Text;
            public double Created;
            public ActionFeedbackView View;
        }
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly HashSet<TaskFeedbackKey> terminal = new();
        private readonly Queue<TaskFeedbackKey> terminalOrder = new();
        private readonly List<Toast> toasts = new();
        private readonly List<Rect> occupied = new();
        private readonly Dictionary<string, Rect> headRects = new(StringComparer.Ordinal);
        private DialogueBubbleAnchor[] headAnchors = Array.Empty<DialogueBubbleAnchor>();
        public int NotificationsShown { get; private set; }
        public int PendingNotificationCount => toasts.Count;
        public int ActiveTaskCount
        {
            get { int count = 0; foreach (var entry in entries.Values) if (entry.Snapshot != null) count++; return count; }
        }
        public int VisibleLabelCount
        {
            get { int count = 0; foreach (var entry in entries.Values) if (entry.View != null && entry.View.gameObject.activeSelf) count++; return count; }
        }
        public string LastResultText { get; private set; } = "";
        public string PhaseText(string actor) => entries.TryGetValue(actor, out var entry) && entry.Snapshot != null
            ? FormatPhase(entry.Snapshot) : "";

        public void Configure(Canvas root, Camera camera, GoalPlanRunner[] sources, WorldEntity localPlayer,
            DialogueBubbleManager bubbleManager, RectTransform prompt, ActionFeedbackView itemTemplate)
        {
            Unsubscribe();
            canvas = root; sceneCamera = camera; runners = sources; player = localPlayer;
            bubbles = bubbleManager; interactionPrompt = prompt; template = itemTemplate;
            if (isActiveAndEnabled) Subscribe();
        }
        private void OnEnable() => Subscribe();
        private void OnDisable()
        {
            Unsubscribe();
            foreach (var entry in entries.Values) if (entry.View != null) entry.View.gameObject.SetActive(false);
            foreach (var toast in toasts) if (toast.View != null) Destroy(toast.View.gameObject);
            toasts.Clear();
        }
        private void Subscribe()
        {
            headAnchors = FindObjectsByType<DialogueBubbleAnchor>(FindObjectsInactive.Include);
            foreach (var entry in entries.Values)
                if (entry.Runner == null)
                {
                    if (entry.Snapshot != null) RememberTerminal(entry.Snapshot.Key);
                    entry.Snapshot = null;
                    if (entry.View != null) entry.View.gameObject.SetActive(false);
                }
            foreach (var runner in runners)
            {
                if (runner == null) continue;
                runner.TaskStarted -= ObserveStarted; runner.TaskPhaseChanged -= ObservePhase;
                runner.GoalOutcomePublished -= ObserveOutcome;
                runner.TaskStarted += ObserveStarted; runner.TaskPhaseChanged += ObservePhase;
                runner.GoalOutcomePublished += ObserveOutcome;
                var entity = runner.GetComponent<WorldEntity>();
                if (entity == null) continue;
                if (!entries.TryGetValue(entity.EntityId, out var entry))
                {
                    entry = new Entry { Runner = runner, Anchor = runner.GetComponent<DialogueBubbleAnchor>() };
                    entries.Add(entity.EntityId, entry);
                }
                entry.Runner = runner;
                entry.Anchor = runner.GetComponent<DialogueBubbleAnchor>();
                // Hidden presenters consume ended attempts silently; enabling never replays results.
                if (entry.Snapshot != null && (runner.CurrentTaskFeedback == null ||
                    !entry.Snapshot.Key.Equals(runner.CurrentTaskFeedback.Key))) RememberTerminal(entry.Snapshot.Key);
                entry.Snapshot = runner.CurrentTaskFeedback;
                entry.Name = ResolveName(runner);
                if (entry.Snapshot != null) UpdateView(entry);
            }
        }
        private void Unsubscribe()
        {
            foreach (var runner in runners)
            {
                if (runner == null) continue;
                runner.TaskStarted -= ObserveStarted; runner.TaskPhaseChanged -= ObservePhase;
                runner.GoalOutcomePublished -= ObserveOutcome;
            }
        }
        private static string ResolveName(GoalPlanRunner runner)
        {
            var composer = runner.GetComponent<CognitiveContextComposer>();
            if (composer?.CharacterDefinition != null)
                return CharacterProfileResolver.Resolve(composer.CharacterDefinition, CharacterOverrideStore.Shared).display_name;
            var perceived = runner.GetComponent<PerceivableEntity>();
            return perceived == null ? runner.name : perceived.DisplayName;
        }
        internal void ObserveStarted(TaskFeedbackSnapshot snapshot)
        {
            if (snapshot == null || terminal.Contains(snapshot.Key) || !entries.TryGetValue(snapshot.Key.Actor, out var entry)) return;
            if (entry.Snapshot != null && snapshot.Sequence <= entry.Snapshot.Sequence) return;
            entry.Snapshot = snapshot; UpdateView(entry);
        }
        internal void ObservePhase(TaskFeedbackSnapshot snapshot)
        {
            if (snapshot == null || terminal.Contains(snapshot.Key) || !entries.TryGetValue(snapshot.Key.Actor, out var entry) ||
                entry.Snapshot == null || !entry.Snapshot.Key.Equals(snapshot.Key) || snapshot.Sequence <= entry.Snapshot.Sequence) return;
            entry.Snapshot = snapshot; UpdateView(entry);
        }
        internal void ObserveOutcome(GoalOutcome outcome)
        {
            var key = TaskFeedbackKey.From(outcome);
            if (terminal.Contains(key)) return;
            RememberTerminal(key);
            if (!entries.TryGetValue(key.Actor, out var entry) || entry.Snapshot == null || !entry.Snapshot.Key.Equals(key)) return;
            var task = entry.Snapshot;
            entry.Snapshot = null;
            if (entry.View != null) entry.View.gameObject.SetActive(false);
            // The initiating player's identity was captured at accepted Handoff, never inferred from SelectedTarget.
            if (player == null || task.Player != player.EntityId || !isActiveAndEnabled) return;
            string result;
            if (task.ActionId == "clean_counter")
            {
                if (outcome.Status == PlanExecutionStatus.Succeeded)
                {
                    if (!entry.Runner.TryGetCounterCleaningReceipt(key, out var cleanReceipt) || cleanReceipt.Target != task.Target ||
                        cleanReceipt.WorldRevision <= task.WorldRevision || cleanReceipt.WorldRevision > outcome.WorldRevision ||
                        !drinkReceiptIds.Add(cleanReceipt.Id)) return;
                    result = T("柜台已清洁", "Counter cleaned");
                }
                else result = outcome.Status switch { PlanExecutionStatus.Cancelled => T("清洁已取消", "Cleaning cancelled"),
                    PlanExecutionStatus.TimedOut => T("清洁超时", "Cleaning timed out"), _ => T("清洁未完成", "Cleaning incomplete") };
            }
            else if (task.ActionId == "prepare_drink")
            {
                if (outcome.Status == PlanExecutionStatus.Succeeded)
                {
                    if (!entry.Runner.TryGetDrinkReceipt(key, out var receipt) || receipt.Player != player.EntityId ||
                        receipt.DrinkType != task.DrinkType || receipt.WorldRevision <= task.WorldRevision ||
                        receipt.WorldRevision > outcome.WorldRevision || !drinkReceiptIds.Add(receipt.Id)) return;
                    result = EmbodiedNPC.Actions.DrinkTypes.Name(receipt.DrinkType) + " +1";
                }
                else result = outcome.Status switch
                {
                    PlanExecutionStatus.Cancelled => T("制作已取消", "Preparation cancelled"),
                    PlanExecutionStatus.TimedOut => T("制作超时", "Preparation timed out"),
                    _ => T("制作失败", "Preparation failed"),
                };
            }
            else
            {
                if (task.ActionId != "bring_object" || task.Destination != player.EntityId) return;
                result = FormatResult(outcome, task.TargetDefinition == "cafe.cup.coffee");
            }
            EnqueueResult(entry.Name, result);
        }
        // Called only after TaskRuntimeBridge has verified and consumed a fresh committed receipt.
        internal bool ObserveTaskSaved(WorldEntity owner, WorldEntity requestingPlayer)
            => ObserveCommissionResult(owner, requestingPlayer, T("委托已保存", "Commission saved"));
        internal bool ObserveTaskCancelled(WorldEntity owner, WorldEntity requestingPlayer)
            => ObserveCommissionResult(owner, requestingPlayer, T("委托已取消", "Commission cancelled"));
        private bool ObserveCommissionResult(WorldEntity owner, WorldEntity requestingPlayer, string result)
        {
            if (owner == null || player == null || player != requestingPlayer || !isActiveAndEnabled) return false;
            string name = owner.GetComponent<CognitiveContextComposer>()?.CaptureCharacterProfile()?.display_name;
            if (string.IsNullOrWhiteSpace(name)) name = owner.GetComponent<PerceivableEntity>()?.DisplayName ?? owner.name;
            name = SettingsUiLanguage.NpcDisplayName(owner.EntityId, name);
            EnqueueResult(name, result);
            return true;
        }
        private void EnqueueResult(string name, string result)
        {
            LastResultText = result;
            if (toasts.Count >= 4) return; // bounded, no historical notification backlog
            toasts.Add(new Toast { Name = name, Text = result, Created = Time.unscaledTimeAsDouble });
        }
        private void RememberTerminal(TaskFeedbackKey key)
        {
            if (!terminal.Add(key)) return;
            terminalOrder.Enqueue(key);
            while (terminalOrder.Count > 128) terminal.Remove(terminalOrder.Dequeue());
        }
        private readonly HashSet<string> drinkReceiptIds = new(StringComparer.Ordinal);
        private void UpdateView(Entry entry)
        {
            if (template == null) return;
            if (entry.View == null) entry.View = Instantiate(template, transform, false);
            entry.View.name = "Task_" + entry.Snapshot.Key.Actor;
            entry.View.SetText(entry.Name, (entry.Snapshot.ActionId == "clean_counter" ? T("柜台清洁 · ", "Cleaning · ") : entry.Snapshot.ActionId == "prepare_drink" ? T("饮品制作 · ", "Drink · ") :
                T("取物任务 · ", "Delivery · ")) + FormatPhase(entry.Snapshot));
        }
        // Keep this small text readable on narrow displays without changing the shared CanvasScaler.
        private float ViewScale => canvas == null ? 1 : Mathf.Clamp(.8f / Mathf.Max(.1f, canvas.scaleFactor), 1, 2);
        private void LateUpdate()
        {
            if (canvas == null || sceneCamera == null || template == null) return;
            occupied.Clear();
            headRects.Clear();
            foreach (var anchor in headAnchors) if (anchor != null && anchor.isActiveAndEnabled) AddHeadObstacle(anchor);
            if (bubbles != null)
                foreach (Rect rect in bubbles.GetVisibleScreenRects())
                {
                    Vector3 min = transform.InverseTransformPoint(bubbles.transform.TransformPoint(rect.min));
                    Vector3 max = transform.InverseTransformPoint(bubbles.transform.TransformPoint(rect.max));
                    occupied.Add(Rect.MinMaxRect(min.x, min.y, max.x, max.y));
                }
            if (interactionPrompt != null && interactionPrompt.gameObject.activeInHierarchy)
            {
                Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(transform, interactionPrompt);
                occupied.Add(new Rect((Vector2)bounds.min, (Vector2)bounds.size));
            }
            foreach (var entry in entries.Values) LayoutLabel(entry);
            LayoutToasts();
        }
        private void AddHeadObstacle(DialogueBubbleAnchor anchor)
        {
            Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = anchor.HeadPosition + new Vector3((i & 1) == 0 ? -.3f : .3f,
                    (i & 2) == 0 ? -.35f : .4f, (i & 4) == 0 ? -.3f : .3f);
                Vector3 screen = sceneCamera.WorldToScreenPoint(point);
                if (screen.z <= sceneCamera.nearClipPlane) return;
                RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)transform, screen,
                    canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out var local);
                min = Vector2.Min(min, local); max = Vector2.Max(max, local);
            }
            Rect rect = Rect.MinMaxRect(min.x - 10, min.y - 10, max.x + 10, max.y + 10);
            // Like dialogue bubbles, world characters do not occlude this screen-space UI.
            headRects[anchor.SpeakerEntityId] = rect;
        }
        private void LayoutLabel(Entry entry)
        {
            if (entry.View == null) return;
            bool show = entry.Snapshot != null && entry.Runner != null && entry.Runner.isActiveAndEnabled && entry.Anchor != null;
            Vector3 screen = show ? sceneCamera.WorldToScreenPoint(entry.Anchor.AnchorPoint.position) : Vector3.zero;
            show &= screen.z > sceneCamera.nearClipPlane && screen.x >= 0 && screen.x <= Screen.width &&
                screen.y >= 0 && screen.y <= Screen.height &&
                Vector3.Distance(sceneCamera.transform.position, entry.Anchor == null ? Vector3.zero : entry.Anchor.HeadPosition) <= maximumDistance;
            if (show)
            {
                var layer = (RectTransform)transform;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, screen,
                    canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out Vector2 point);
                entry.View.transform.localScale = Vector3.one * ViewScale;
                Vector2 size = entry.View.Rect.sizeDelta * ViewScale;
                show = false;
                // Side placements first; use above/below when the screen edge blocks both sides.
                // Bubbles, prompts, screen bounds and distance still take priority.
                for (int i = 0; i < 8; i++)
                {
                    bool right = i % 2 == 0;
                    float x = headRects.TryGetValue(entry.Snapshot.Key.Actor, out Rect head)
                        ? right ? head.xMax + size.x / 2 + 12 : head.xMin - size.x / 2 - 12
                        : point.x + (right ? 1 : -1) * (size.x / 2 + 90);
                    Vector2 position = new(x, point.y - 90 - i / 2 * (size.y + 8));
                    if (i >= 4)
                    {
                        bool above = i % 2 == 0;
                        float y = headRects.TryGetValue(entry.Snapshot.Key.Actor, out var bounds)
                            ? above ? bounds.yMax + size.y / 2 + 12 : bounds.yMin - size.y / 2 - 12
                            : point.y + (above ? 1 : -1) * (size.y / 2 + 40);
                        position = new Vector2(Mathf.Clamp(point.x, layer.rect.xMin + size.x / 2 + 12,
                            layer.rect.xMax - size.x / 2 - 12), y + (above ? 1 : -1) * ((i-4)/2) * (size.y+8));
                    }
                    Rect rect = new(position - size / 2, size);
                    if (rect.xMin < layer.rect.xMin + 12 || rect.xMax > layer.rect.xMax - 12 ||
                        rect.yMin < layer.rect.yMin + 12 || rect.yMax > layer.rect.yMax - 12) continue;
                    bool conflict = false;
                    foreach (Rect obstacle in occupied) if (rect.Overlaps(obstacle)) { conflict = true; break; }
                    if (conflict) continue;
                    entry.View.Rect.anchoredPosition = position; occupied.Add(rect); show = true; break;
                }
            }
            entry.View.gameObject.SetActive(show);
        }
        private void LayoutToasts()
        {
            double now = Time.unscaledTimeAsDouble;
            int visible = 0;
            var layer = (RectTransform)transform;
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                var toast = toasts[i];
                double age = now - toast.Created;
                if (age >= 2.8)
                {
                    if (toast.View != null) Destroy(toast.View.gameObject);
                    toasts.RemoveAt(i); continue;
                }
                if (visible >= 2)
                {
                    if (toast.View != null) toast.View.gameObject.SetActive(false);
                    continue;
                }
                if (toast.View == null)
                {
                    toast.View = Instantiate(template, transform, false);
                    toast.View.name = "ActionResult"; toast.View.SetText(toast.Name, toast.Text);
                    NotificationsShown++;
                }
                toast.View.gameObject.SetActive(true);
                toast.View.transform.localScale = Vector3.one * ViewScale;
                Vector2 size = toast.View.Rect.sizeDelta * ViewScale;
                toast.View.Rect.anchoredPosition = new Vector2(layer.rect.xMax - size.x / 2 - 24,
                    layer.rect.yMax - size.y / 2 - 90 - visible * (size.y + 8));
                toast.View.SetAlpha(Mathf.Clamp01((float)((2.8 - age) / .45)));
                visible++;
            }
        }
        private static string T(string chinese, string english) => SettingsUiLanguage.T(chinese, english);
        public static string FormatPhase(TaskFeedbackSnapshot value)
        {
            var progress = value.Progress;
            if (value.ActionId == "prepare_drink")
            {
                string name = EmbodiedNPC.Actions.DrinkTypes.Name(value.DrinkType);
                if (progress.Kind == PlanProgressKind.Accepted) return $"正在准备{name}…";
                if (progress.Action == PlannerActionType.WalkTo) return T("前往吧台制作区…", "Going to drink station…");
                if (progress.Action == PlannerActionType.PrepareDrink) return $"正在制作{name}…";
            }
            if (value.ActionId == "clean_counter")
                return progress.Action == PlannerActionType.CleanCounter ? T("正在擦拭柜台…", "Wiping counter…") : T("前往柜台…", "Going to counter…");
            bool cup = value.TargetDefinition == "cafe.cup.coffee";
            if (progress.Kind == PlanProgressKind.Rediscovering) return T("正在确认物品位置…", "Locating item…");
            if (progress.Kind == PlanProgressKind.Replanning) return T("正在重新规划…", "Replanning…");
            if (progress.Kind == PlanProgressKind.Accepted) return T(cup ? "正在处理取杯任务…" : "正在处理取物任务…", "Delivery accepted…");
            return progress.Action switch
            {
                PlannerActionType.WalkTo when progress.Bindings.TryGetValue("destination", out string id) && id == value.Target => T("前往物品…", "Going to item…"),
                PlannerActionType.WalkTo when progress.Bindings.TryGetValue("destination", out string id) && id == value.Destination => T(cup ? "正在递送杯子…" : "正在递送物品…", "Delivering item…"),
                PlannerActionType.WalkTo => T("前往交互点…", "Going to interaction…"),
                PlannerActionType.PickUp => T(cup ? "正在取杯…" : "正在拾取…", "Picking up…"),
                PlannerActionType.Give => T("正在交付…", "Handing over…"),
                PlannerActionType.PutDown => T("正在放下物品…", "Putting item down…"),
                PlannerActionType.Open => T("正在开门…", "Opening door…"),
                _ => T("正在执行任务…", "Executing task…"),
            };
        }
        public static string FormatResult(GoalOutcome value, bool cup = false) => value.Status switch
        {
            PlanExecutionStatus.Succeeded => T(cup ? "杯子已交付" : "物品已交付", "Item delivered"),
            PlanExecutionStatus.Cancelled => T("任务已取消", "Task cancelled"),
            PlanExecutionStatus.TimedOut => T("任务超时", "Task timed out"),
            _ when value.FailureCode == "TARGET_UNREACHABLE" || value.FailureCode == "PATH_INVALID" ||
                value.FailureCode == "PATH_PARTIAL" => T("无法到达目标", "Cannot reach target"),
            _ => T("任务未完成", "Task incomplete"),
        };
    }
}
