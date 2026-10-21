using EmbodiedNPC.Perception;
using EmbodiedNPC.World;
using EmbodiedNPC.Dialogue;
using EmbodiedNPC.Cognition;
using EmbodiedNPC.Settings;
using EmbodiedNPC.Events;
using System.Text;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CafeDemo
{
    /// <summary>Session-only rendering of sensor settings and one NPC's existing perceived state.</summary>
    public sealed class CafePerceptionDebugVisualizer : MonoBehaviour
    {
        [SerializeField] private EntityRegistry entityRegistry;
        [SerializeField] private Camera sceneCamera;

        private WorldEntity selectedEntity;
        private PerceivedStateStore perception;
        private VisionSensor vision;
        private ProximitySensor proximity;
        private HearingSensor hearing;
        // The scene tool owns these temporary projections, never player/persona persistence.
        public struct VisualizationState
        {
            public bool Vision, Proximity, EntityIds, PerceivedEntities, Hearing;
        }
        private readonly Dictionary<string, VisualizationState> selections = new(StringComparer.Ordinal);
        public VisualizationState SelectionState { get; private set; }
        private bool toolActive = true;
        private LineRenderer visionLine;
        private LineRenderer proximityLine;
        private Material lineMaterial;
        private GUIStyle labelStyle;
        private bool showVision;
        private bool showProximity;
        private bool showEntityIds;
        private bool showPerceivedEntities;
        private CafeDemoDeveloperPanel settings;
        private DialogueController dialogue;
        private AudioStimulusRouter audioRouter;
        private CognitiveRunDriver[] speechSources;
        private LineRenderer hearingLine;
        private LineRenderer hearingReferenceLine;
        private bool showHearing, hasSpeech;
        private Vector3 speechOrigin;
        private string speechSource, speechTarget;
        private string speechOccurrence;

        public bool IsVisionVisible => visionLine != null && visionLine.enabled;
        public bool IsProximityVisible => proximityLine != null && proximityLine.enabled;
        public bool ShowEntityIds => showEntityIds;
        public bool ShowPerceivedEntities => showPerceivedEntities;
        public bool IsHearingVisible => hearingLine != null && hearingLine.enabled;
        public bool IsHearingReferenceVisible => hearingReferenceLine != null && hearingReferenceLine.enabled;
        public bool CanShowVision => vision != null && vision.isActiveAndEnabled;
        public bool CanShowProximity => proximity != null && proximity.isActiveAndEnabled;
        public bool CanShowHearing => hearing != null && hearing.isActiveAndEnabled && audioRouter != null &&
            audioRouter.isActiveAndEnabled && audioRouter.SpeechProfile != null;
        // All rendering paths consult the same modal/developer gate, including IMGUI.
        public bool CanRenderOverlays => isActiveAndEnabled && toolActive && selectedEntity != null &&
            selectedEntity.isActiveAndEnabled && (settings == null || (!settings.BlocksWorldDebugOverlays &&
            (settings.PlayerNavigation == null || settings.PlayerNavigation.IsDeveloper)));

        private void Awake()
        {
            if (entityRegistry == null) entityRegistry = FindAnyObjectByType<EntityRegistry>();
            if (sceneCamera == null) sceneCamera = Camera.main;
            settings = FindAnyObjectByType<CafeDemoDeveloperPanel>();
            dialogue = FindAnyObjectByType<DialogueController>();
            audioRouter = FindAnyObjectByType<AudioStimulusRouter>();
            speechSources = FindObjectsByType<CognitiveRunDriver>(FindObjectsSortMode.None);
        }
        private void OnEnable()
        {
            if (dialogue != null) dialogue.DialogueSubmissionAccepted += OnAccepted;
            if (speechSources != null) foreach (var source in speechSources) source.UtterancePresented += OnReply;
        }
        private void OnDisable()
        {
            if (dialogue != null) dialogue.DialogueSubmissionAccepted -= OnAccepted;
            if (speechSources != null) foreach (var source in speechSources) if (source != null) source.UtterancePresented -= OnReply;
            HideLines();
        }
        private void OnAccepted(UserUtterance input)
        {
            if (input == null) return;
            string occurrence = null;
            if (entityRegistry != null && entityRegistry.TryGetEntity(input.targetAgentId, out var target))
            {
                var turn = target.GetComponent<CognitiveDialogueSubmissionHandler>()?.LastAcceptedTurn;
                if (turn != null && turn.Text == input.text && turn.SpeakerEntityId == input.speakerEntityId &&
                    turn.ConversationId == input.conversationId) occurrence = "speech_input_" + turn.SourceEventId;
            }
            ObserveSpeech(input.speakerEntityId, input.targetAgentId, occurrence);
        }
        private void OnReply(PresentedCognitiveUtterance reply)
        {
            if (reply?.IsDialogueEvidence == true)
                ObserveSpeech(reply.OwnerAgentEntityId, reply.PlayerEntityId, "speech_reply_" + reply.RunId + "_" + reply.SourceEventId);
        }
        private void ObserveSpeech(string source, string target, string occurrence)
        {
            if (entityRegistry == null || !entityRegistry.TryGetEntity(source, out var speaker)) return;
            speechOccurrence = occurrence;
            // Same source position as CafeSpeechHearingBridge; this does not route a new stimulus.
            speechSource = source; speechTarget = target; speechOrigin = speaker.transform.position + Vector3.up * 1.5f; hasSpeech = true;
        }
        public void SetHearingVisibility(bool visible) => SetVisibility(showVision, showProximity, showEntityIds, showPerceivedEntities, visible);
        public bool HasSelectedSpeechReceipt
        {
            get
            {
                if (speechOccurrence == null || perception?.Aggregator == null) return false;
                foreach (var observation in perception.Aggregator.Observations)
                    if (observation.Channel == ObservationChannel.Audio &&
                        observation.KnownFields.TryGetValue("occurrence_id", out var occurrence) && occurrence == speechOccurrence &&
                        observation.KnownFields.TryGetValue("event_type", out var type) && type == AgentEventTypes.HeardSpeech) return true;
                return false;
            }
        }
        public string SensorSummary
        {
            get
            {
                string Status(Behaviour sensor, string absent) => sensor == null ? absent :
                    SettingsUiLanguage.T("已禁用", "Disabled");
                string visual = CanShowVision ? $"{vision.EnterDistance:0.##} m · {vision.EnterHalfFovDegrees * 2:0.#}°" :
                    Status(vision, SettingsUiLanguage.T("未配置视觉传感器", "No vision sensor configured"));
                string nearby = CanShowProximity ? $"{proximity.Range:0.##} m" :
                    Status(proximity, SettingsUiLanguage.T("未配置近距传感器", "No proximity sensor configured"));
                return SettingsUiLanguage.T("视野（传感器实际方向）：", "Vision (actual sensor direction): ") + visual + "\n" +
                    SettingsUiLanguage.T("近距：", "Proximity: ") + nearby + "\n" + HearingSummary;
            }
        }
        public string HearingSummary
        {
            get
            {
                var profile = audioRouter == null ? null : audioRouter.SpeechProfile;
                if (hearing == null) return SettingsUiLanguage.T("听觉：未配置听觉传感器。", "Hearing: no hearing sensor configured.");
                if (!hearing.isActiveAndEnabled) return SettingsUiLanguage.T("听觉：传感器已禁用。", "Hearing: sensor disabled.");
                if (profile == null || !audioRouter.isActiveAndEnabled) return SettingsUiLanguage.T("听觉：语音 Profile 或路由不可用。", "Hearing: speech profile or router unavailable.");
                string legend = SettingsUiLanguage.T("蓝圈：耳部语音标称参考范围；紫圈：最近声源范围。", "Blue: ear-centered nominal speech reference; magenta: latest source range.");
                string boundary = SettingsUiLanguage.T("范围内不等于收到；仍需强度、遮挡及路由判定。", "Within range does not prove receipt; intensity, occlusion and routing still apply.");
                if (!hasSpeech) return $"{legend}\n{profile.HearingRange:0.##} m · " + SettingsUiLanguage.T("等待发言。", "Waiting for speech.") + "\n" + boundary;
                var text = new StringBuilder(legend + "\n" + SettingsUiLanguage.T("最近声源：", "Latest source: ") + speechSource + " · " + profile.HearingRange + " m\n");
                text.Append(SettingsUiLanguage.T("距离内的潜在旁听者：", "Potential bystanders within range: "));
                foreach (var sensor in FindObjectsByType<HearingSensor>(FindObjectsSortMode.None))
                {
                    var entity = sensor.GetComponent<WorldEntity>();
                    if (entity == null || !sensor.isActiveAndEnabled || !entity.isActiveAndEnabled || !entity.IsAgent(AgentRole.Npc) ||
                        entity.EntityId == speechSource || entity.EntityId == speechTarget || Vector3.Distance(sensor.EarPosition, speechOrigin) >= profile.HearingRange) continue;
                    text.Append(SettingsUiLanguage.NpcDisplayName(entity.EntityId, entity.GetComponent<PerceivableEntity>()?.DisplayName) ?? entity.EntityId).Append(" · ");
                }
                text.AppendLine().AppendLine(HasSelectedSpeechReceipt ? SettingsUiLanguage.T("本 NPC：有匹配的 Audio Observation 接收证据。", "Selected NPC: matching Audio Observation receipt.") :
                    SettingsUiLanguage.T("本 NPC：尚无匹配的接收证据。", "Selected NPC: no matching receipt evidence."));
                return text.Append(boundary).ToString();
            }
        }

        public void SetSelection(WorldEntity entity, PerceivedStateStore state, VisionSensor visionSensor,
            ProximitySensor proximitySensor)
        {
            selectedEntity = entity;
            perception = state;
            vision = visionSensor;
            proximity = proximitySensor;
            hearing = entity == null ? null : entity.GetComponent<HearingSensor>();
            var stateKey = entity == null ? null : entity.EntityId;
            var value = stateKey != null && selections.TryGetValue(stateKey, out var saved) ? saved : default;
            ApplyState(value);
            RefreshLineVisibility();
        }

        public void SetVisibility(bool visionCone, bool proximityRange, bool entityIds, bool perceivedEntities)
            => SetVisibility(visionCone, proximityRange, entityIds, perceivedEntities, showHearing);

        public void SetVisibility(bool visionCone, bool proximityRange, bool entityIds, bool perceivedEntities, bool hearingRange)
        {
            var value = new VisualizationState { Vision = visionCone, Proximity = proximityRange,
                EntityIds = entityIds, PerceivedEntities = perceivedEntities, Hearing = hearingRange };
            if (selectedEntity != null && !string.IsNullOrEmpty(selectedEntity.EntityId)) selections[selectedEntity.EntityId] = value;
            ApplyState(value);
            RefreshLineVisibility();
        }

        private void ApplyState(VisualizationState value)
        {
            SelectionState = value;
            showVision = value.Vision; showProximity = value.Proximity; showEntityIds = value.EntityIds;
            showPerceivedEntities = value.PerceivedEntities; showHearing = value.Hearing;
        }
        public void SetToolActive(bool active) { toolActive = active; if (!active) HideLines(); }
        private void HideLines()
        {
            foreach (var line in new[] {visionLine, proximityLine, hearingLine, hearingReferenceLine})
                if (line != null) line.enabled = false;
        }

        private void RefreshLineVisibility()
        {
            if (showVision && vision != null && visionLine == null) visionLine = CreateLine("VisionCone", Color.cyan);
            if (showProximity && proximity != null && proximityLine == null)
                proximityLine = CreateLine("ProximityRange", new Color(1f, .75f, .2f));
            bool covered = !CanRenderOverlays;
            if (visionLine != null) visionLine.enabled = !covered && showVision && CanShowVision;
            if (proximityLine != null)
                proximityLine.enabled = !covered && showProximity && CanShowProximity;
        }

        private LineRenderer CreateLine(string name, Color color)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;
            lineMaterial ??= new Material(shader) { name = "CafeDebugLines" };
            var lineObject = new GameObject(name);
            lineObject.transform.SetParent(transform, false);
            var line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = lineMaterial;
            line.startColor = line.endColor = color;
            line.startWidth = line.endWidth = .035f;
            line.useWorldSpace = true;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private void LateUpdate()
        {
            RefreshLineVisibility();
            var profile = audioRouter == null ? null : audioRouter.SpeechProfile;
            bool hearingVisible = CanRenderOverlays && showHearing && CanShowHearing;
            if (hearingVisible && hearingReferenceLine == null) hearingReferenceLine = CreateLine("HearingReference", new Color(.3f, .55f, 1f));
            if (hearingVisible && hasSpeech && hearingLine == null) hearingLine = CreateLine("PotentialHearing", Color.magenta);
            if (hearingReferenceLine != null)
            {
                hearingReferenceLine.enabled = hearingVisible;
                if (hearingVisible) DrawCircle(hearingReferenceLine, hearing.EarPosition, profile.HearingRange);
            }
            if (hearingLine != null)
            {
                hearingLine.enabled = hearingVisible && hasSpeech;
                if (hearingLine.enabled)
                {
                    DrawCircle(hearingLine, speechOrigin, profile.HearingRange);
                }
            }
            if (visionLine != null && visionLine.enabled) DrawVisionCone();
            if (proximityLine != null && proximityLine.enabled) DrawProximityRange();
        }

        private static void DrawCircle(LineRenderer line, Vector3 center, float range)
        {
            line.loop = true; line.positionCount = 48;
            for (int i = 0; i < 48; i++)
            {
                float angle = i * Mathf.PI * 2 / 48;
                line.SetPosition(i, center + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * range);
            }
        }

        private void DrawVisionCone()
        {
            const int segments = 24;
            Vector3 origin = vision.transform.position + Vector3.up * vision.EyeHeight;
            visionLine.positionCount = segments + 3;
            visionLine.SetPosition(0, origin);
            for (int index = 0; index <= segments; index++)
            {
                float angle = Mathf.Lerp(-vision.EnterHalfFovDegrees, vision.EnterHalfFovDegrees,
                    index / (float)segments);
                Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * vision.transform.forward;
                visionLine.SetPosition(index + 1, origin + direction * vision.EnterDistance);
            }
            visionLine.SetPosition(segments + 2, origin);
        }

        private void DrawProximityRange()
        {
            const int segments = 48;
            Vector3 center = proximity.transform.position + Vector3.up * .05f;
            proximityLine.positionCount = segments;
            proximityLine.loop = true;
            for (int index = 0; index < segments; index++)
            {
                float angle = index * Mathf.PI * 2f / segments;
                proximityLine.SetPosition(index, center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) *
                    proximity.Range);
            }
        }

        private void OnGUI()
        {
            if (!CanRenderOverlays || Event.current.type != EventType.Repaint || sceneCamera == null ||
                (!showEntityIds && !showPerceivedEntities)) return;
            labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            if (showEntityIds) DrawLabel(selectedEntity, Color.yellow);
            PerceptionAggregator state = perception?.Aggregator;
            if (state == null || entityRegistry == null || !entityRegistry.IsReady) return;
            foreach (PerceivedEntityRecord record in state.Entities.Values)
            {
                if (record.KnownFields.TryGetValue("visibility_state", out string visibility) &&
                    visibility == "forgotten") continue;
                if (!entityRegistry.TryGetEntity(record.EntityId, out WorldEntity perceived)) continue;
                if (showPerceivedEntities) DrawMarker(perceived, new Color(.2f, 1f, .45f, .9f));
                if (showEntityIds) DrawLabel(perceived, Color.cyan);
            }
        }

        private bool TryScreenPoint(WorldEntity target, out Vector2 point)
        {
            var perceived = target.GetComponent<PerceivableEntity>();
            Vector3 projected = sceneCamera.WorldToScreenPoint(perceived == null ? target.transform.position : perceived.VisualTargetPosition);
            point = new Vector2(projected.x, Screen.height - projected.y);
            return projected.z > 0f && point.x >= 0f && point.x <= Screen.width &&
                   point.y >= 0f && point.y <= Screen.height;
        }

        private void DrawLabel(WorldEntity target, Color color)
        {
            if (!TryScreenPoint(target, out Vector2 point)) return;
            Color previous = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(point.x + 14f, point.y - 12f, 245f, 28f), target.EntityId, labelStyle);
            GUI.color = previous;
        }

        private void DrawMarker(WorldEntity target, Color color)
        {
            if (!TryScreenPoint(target, out Vector2 point)) return;
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(point.x - 11f, point.y - 11f, 22f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(point.x - 11f, point.y + 9f, 22f, 2f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(point.x - 11f, point.y - 11f, 2f, 22f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(point.x + 9f, point.y - 11f, 2f, 22f), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private void OnDestroy()
        {
            foreach (var line in new[] {visionLine, proximityLine, hearingLine, hearingReferenceLine})
                if (line != null) Release(line.gameObject);
            if (lineMaterial != null) Release(lineMaterial);
        }
        private static void Release(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
