using System.Collections.Generic;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Projects only an NPC's own SkillResult into direct knowledge; it does not broadcast world facts.</summary>
    public sealed class SkillResultDirectObservationBridge : MonoBehaviour
    {
        [SerializeField] private WorldStateService worldState;
        [SerializeField] private DirectObservationReceiver receiver;

        private void Awake()
        {
            if (worldState == null) worldState = FindAnyObjectByType<WorldStateService>();
            if (receiver == null) receiver = GetComponent<DirectObservationReceiver>();
        }

        private void OnEnable()
        {
            if (worldState != null) worldState.SkillResultPublished += HandleSkillResult;
        }

        private void OnDisable()
        {
            if (worldState != null) worldState.SkillResultPublished -= HandleSkillResult;
        }

        private void HandleSkillResult(SkillResult result)
        {
            if (receiver == null || result.ActorEntityId != receiver.ObserverEntityId) return;
            GameplayEvent gameplayEvent = GameplayEvent.FromSkillResult(result, transform.position,
                Time.realtimeSinceStartupAsDouble);
            var known = new Dictionary<string, string>
            {
                ["event_type"] = gameplayEvent.EventType.ToString(),
                ["skill_type"] = result.SkillType,
                ["status"] = result.Status.ToString(),
                ["failure_code"] = result.FailureCode,
            };
            receiver.Receive(gameplayEvent, result.TargetEntityId, known, new[] { "exact_position" });
        }
    }
}
