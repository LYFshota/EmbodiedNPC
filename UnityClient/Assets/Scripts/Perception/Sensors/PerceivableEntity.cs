using System.Collections.Generic;
using EmbodiedNPC.Interaction;
using EmbodiedNPC.World;
using EmbodiedNPC.Identity;
using UnityEngine;

namespace EmbodiedNPC.Perception
{
    /// <summary>Generic, opt-in description of fields an entity may reveal to physical sensors.</summary>
    [RequireComponent(typeof(WorldEntity))]
    public sealed class PerceivableEntity : MonoBehaviour
    {
        [SerializeField] private WorldEntity entity;
        [SerializeField] private Transform visualAnchor;
        [SerializeField] private Collider visibilityCollider;
        [SerializeField] private string displayName = "";
        [SerializeField] private string[] aliases = { };
        [SerializeField] private string[] semanticTags = { };
        [SerializeField] private string[] publicCapabilities = { };

        private Transform head;
        private Transform neck;
        private Transform leftEye;
        private Transform rightEye;

        public WorldEntity Entity => entity;
        public string AppearanceDescription { get; private set; }
        public void SetAppearanceDescription(string value) => AppearanceDescription = value;
        public void RebindAnimator(Animator animator)
        {
            if (animator == null || !animator.isHuman) return;
            // Preserve authored anchor semantics; only a direct Humanoid Head binding follows replacement.
            bool followsHead = head != null && visualAnchor == head;
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            if (followsHead) visualAnchor = head;
        }
        public Transform VisualAnchor => visualAnchor == null ? transform : visualAnchor;
        // Humanoid Head is a joint at the base of the skull, not the visible face.
        // Sample the eyes (or a scale-relative upper-face point) when that joint is the
        // authored anchor. All other authored anchors keep their original meaning.
        public Vector3 VisualTargetPosition
        {
            get
            {
                if (head == null || VisualAnchor != head) return VisualAnchor.position;
                if (leftEye != null && rightEye != null) return (leftEye.position + rightEye.position) * 0.5f;
                if (neck == null) return head.position;
                return head.position + (head.position - neck.position) * 2f;
            }
        }
        public Collider VisibilityCollider => visibilityCollider;
        public string DisplayName => TryGetComponent<PlayerIdentityBinding>(out var player) && player.CurrentIdentity != null
            ? player.CurrentIdentity.DisplayName : string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;

        private void Awake()
        {
            if (entity == null) entity = GetComponent<WorldEntity>();
            if (visibilityCollider == null) visibilityCollider = GetComponent<Collider>();
            Animator animator = GetComponentInChildren<Animator>();
            RebindAnimator(animator);
        }

        public void WriteVisibleFields(IDictionary<string, string> fields)
        {
            if (fields == null || entity == null) return;
            fields["entity_kind"] = entity.EntityKind.ToString();
            if (entity.EntityKind == EntityKind.Agent)
                fields["agent_role"] = entity.AgentRole.ToString();
            fields["display_name"] = DisplayName;
            if (aliases != null && aliases.Length > 0)
                fields["aliases"] = string.Join(",", aliases);
            if (semanticTags != null && semanticTags.Length > 0)
                fields["semantic_tags"] = string.Join(",", semanticTags);
            List<string> capabilities = BuildPublicCapabilities();
            if (capabilities.Count > 0)
                fields["capabilities"] = string.Join(",", capabilities);
        }

        private List<string> BuildPublicCapabilities()
        {
            const string pickUp = "pick_up";
            List<string> capabilities = new();
            if (publicCapabilities != null)
            {
                foreach (string capability in publicCapabilities)
                    if (capability != pickUp)
                        capabilities.Add(capability);
            }

            if (TryGetComponent(out InteractableEntity interactable) && interactable.SupportsPickUp)
                capabilities.Add(pickUp);

            return capabilities;
        }
    }
}
