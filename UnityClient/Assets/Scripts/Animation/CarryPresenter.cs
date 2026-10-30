using System;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Animation
{
    /// <summary>Read-only projection of authoritative item ownership onto a holding layer.</summary>
    [DisallowMultipleComponent]
    public sealed class CarryPresenter : MonoBehaviour
    {
        public const string LayerName = "CarryUpperBody";
        [SerializeField] private WorldEntity owner;
        [SerializeField] private EntityRegistry registry;
        [SerializeField] private Animator animator;
        [SerializeField, Min(.01f)] private float blendIn = .2f;
        [SerializeField, Min(.01f)] private float blendOut = .2f;
        private RuntimeAnimatorController inspectedController;
        private int layer = -1;
        private bool warnedMultiple;
        private MotionChannelCoordinator motionChannels;
        public bool IsCarrying { get; private set; }
        public string CarriedItemId { get; private set; } = string.Empty;
        public float TargetWeight => IsCarrying ? 1f : 0f;
        public float CurrentWeight { get; private set; }

        public void RebindAnimator(Animator character) => Configure(owner, registry, character);

        public void Configure(WorldEntity actor, EntityRegistry entities, Animator character)
        {
            ResetPresentation(); owner = actor; registry = entities; animator = character;
            motionChannels = owner == null ? null : owner.GetComponent<MotionChannelCoordinator>(); InspectLayer();
        }
        private void Awake()
        { motionChannels = owner == null ? null : owner.GetComponent<MotionChannelCoordinator>(); InspectLayer(); }
        private void InspectLayer()
        {
            inspectedController = animator == null ? null : animator.runtimeAnimatorController;
            layer = inspectedController != null ? animator.GetLayerIndex(LayerName) : -1;
        }
        public void Tick(float deltaTime)
        {
            if (!isActiveAndEnabled || !float.IsFinite(deltaTime) || deltaTime < 0f) return;
            WorldEntity selected = null;
            int count = 0;
            if (owner != null && !string.IsNullOrEmpty(owner.EntityId) && registry != null && registry.IsReady)
            {
                foreach (WorldEntity entity in registry.RegisteredEntities)
                {
                    if (entity == null) continue;
                    var held = entity.GetComponent<HeldItemState>();
                    if (held == null || !held.IsHeld || !string.Equals(held.HolderEntityId, owner.EntityId, StringComparison.Ordinal)) continue;
                    count++;
                    if (selected == null || string.CompareOrdinal(entity.EntityId, selected.EntityId) < 0) selected = entity;
                }
            }
            if (count > 1 && !warnedMultiple)
                Debug.LogWarning($"[Carry] Multiple held items for {owner.EntityId}; presenting {selected.EntityId} only.", this);
            warnedMultiple = count > 1;
            IsCarrying = selected != null;
            if (motionChannels != null && motionChannels.IsCarryPresentationActive != IsCarrying)
                motionChannels.SetCarryPresentationActive(IsCarrying);
            CarriedItemId = selected != null ? selected.EntityId : string.Empty;
            if (animator == null) { CurrentWeight = 0f; return; }
            if (inspectedController != animator.runtimeAnimatorController) InspectLayer();
            if (!animator.isActiveAndEnabled || !animator.isInitialized || layer < 0 || layer >= animator.layerCount)
            { CurrentWeight = 0f; return; }
            CurrentWeight = Mathf.MoveTowards(CurrentWeight, TargetWeight,
                deltaTime / Mathf.Max(.01f, IsCarrying ? blendIn : blendOut));
            animator.SetLayerWeight(layer, CurrentWeight);
        }
        public void ResetPresentation()
        {
            if (animator != null && animator.isInitialized && animator.runtimeAnimatorController != null &&
                layer >= 0 && layer < animator.layerCount) animator.SetLayerWeight(layer, 0f);
            IsCarrying = false; CarriedItemId = string.Empty; CurrentWeight = 0f; warnedMultiple = false;
            motionChannels?.SetCarryPresentationActive(false);
        }
        private void Update() => Tick(Time.deltaTime);
        private void OnDisable() => ResetPresentation();
    }
}
