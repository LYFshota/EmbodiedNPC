using EmbodiedNPC.World;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldEntity))]
    public sealed class DialogueBubbleAnchor : MonoBehaviour
    {
        [SerializeField] private WorldEntity owner;
        [SerializeField] private Transform anchorPoint;
        [SerializeField] private DialogueSpeakerKind speakerKind;
        private Transform head;

        public string SpeakerEntityId => owner == null ? string.Empty : owner.EntityId;
        public Transform AnchorPoint => anchorPoint == null ? transform : anchorPoint;
        public DialogueSpeakerKind SpeakerKind => speakerKind;
        public Vector3 HeadPosition => head != null ? head.position : AnchorPoint.position - Vector3.up * 0.35f;

        private void Awake()
        {
            if (owner == null) owner = GetComponent<WorldEntity>();
            if (anchorPoint == null) anchorPoint = transform;
            Animator animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman) head = animator.GetBoneTransform(HumanBodyBones.Head);
        }

        private void OnEnable() => DialogueBubbleManager.Active?.RegisterAnchor(this);
        private void OnDisable() => DialogueBubbleManager.Active?.UnregisterAnchor(this);

        public void Configure(WorldEntity entity, Transform point, DialogueSpeakerKind kind)
        {
            owner = entity;
            anchorPoint = point;
            speakerKind = kind;
            if (isActiveAndEnabled) DialogueBubbleManager.Active?.RegisterAnchor(this);
        }
    }
}
