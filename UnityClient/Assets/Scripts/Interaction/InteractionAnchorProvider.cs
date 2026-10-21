using UnityEngine;

namespace EmbodiedNPC.Interaction
{
    /// <summary>
    /// Scene-authored interaction anchors resolved from the requested entity.
    /// </summary>
    public sealed class InteractionAnchorProvider : MonoBehaviour
    {
        [SerializeField] private Transform pickupApproachPoint;
        [SerializeField] private Transform receivePoint;
        [SerializeField] private Transform grabPoint;
        [SerializeField] private Transform holdPoint;

        public Transform PickupApproachPoint => pickupApproachPoint;
        public Transform ReceivePoint => receivePoint;
        public Transform GrabPoint => grabPoint;
        public Transform HoldPoint => holdPoint;
        public bool HasPickupAnchors => pickupApproachPoint != null && grabPoint != null;
        public bool HasGiveAnchors => receivePoint != null && holdPoint != null;
    }
}
