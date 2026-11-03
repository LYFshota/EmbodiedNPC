using System;
using System.Collections.Generic;
using EmbodiedNPC.Actions;
using UnityEngine;

namespace EmbodiedNPC.World
{
    /// <summary>Logical service receipt. Not an item, held state, stock deduction or player inventory.</summary>
    public readonly struct SnackServiceReceipt
    {
        internal SnackServiceReceipt(string id,string actor,string proposal,string trace,string player,string snack,string quantity,int revision,
            PersistentIdentityKey actorIdentity,PersistentIdentityKey playerIdentity)
        { Id=id; Actor=actor; Proposal=proposal; Trace=trace; Player=player; SnackType=snack; Quantity=quantity; WorldRevision=revision;
            ActorIdentity=actorIdentity; PlayerIdentity=playerIdentity; }
        public string Id { get; }
        public string Actor { get; }
        public string Proposal { get; }
        public string Trace { get; }
        public string Player { get; }
        public string SnackType { get; }
        public string Quantity { get; }
        public int WorldRevision { get; }
        public PersistentIdentityKey ActorIdentity { get; }
        public PersistentIdentityKey PlayerIdentity { get; }
    }
    public sealed partial class WorldStateService
    {
        private readonly Dictionary<(string actor,string proposal,string trace),SnackServiceReceipt> snackReceipts = new();
        public bool TryGetSnackReceipt(string actor,string proposal,string trace,out SnackServiceReceipt receipt) =>
            snackReceipts.TryGetValue((actor,proposal,trace),out receipt);
        internal SkillResult TryCommitSnack(string proposal,string trace,EntityRegistry registry,WorldEntity actor,WorldEntity station,
            WorldEntity player,string snack,string quantity)
        {
            string skill="serve_snack:"+proposal;
            if (!isActiveAndEnabled || string.IsNullOrEmpty(proposal) || string.IsNullOrEmpty(trace) || !SnackCatalog.IsQuantity(quantity) ||
                !SnackServiceCapability.ValidateRuntime(registry,actor,station,player,snack))
                return RecordAttempt(skill,trace,"ServeSnack",actor,station,player,SkillStatus.Failed,"SNACK_COMMIT_INVALID");
            if (TryGetSnackReceipt(actor.EntityId,proposal,trace,out var existing))
            {
                bool matches=existing.Player==player.EntityId && existing.SnackType==snack && existing.Quantity==quantity;
                return RecordAttempt(skill,trace,"ServeSnack",actor,station,player,matches?SkillStatus.Succeeded:SkillStatus.Failed,
                    matches?"SNACK_ALREADY_COMMITTED":"SNACK_ORDER_CONFLICT");
            }
            if (!TaskEffectAllowed(actor.EntityId,proposal,trace,"serve_snack",station.EntityId,player.EntityId,null,0,snack,quantity))
                return RecordAttempt(skill,trace,"ServeSnack",actor,station,player,SkillStatus.Failed,"TASK_GUARD_EXPIRED");
            var cap=actor.GetComponent<SnackServiceCapability>();
            if (Vector3.Distance(actor.transform.position,cap.Approach(snack).position)>.3f)
                return RecordAttempt(skill,trace,"ServeSnack",actor,station,player,SkillStatus.Failed,"ACTION_OUT_OF_RANGE");
            actor.TryGetPersistentIdentityKey(out var actorIdentity); player.TryGetPersistentIdentityKey(out var playerIdentity);
            var receipt=new SnackServiceReceipt("snack_"+Guid.NewGuid().ToString("N"),actor.EntityId,proposal,trace,player.EntityId,
                snack,quantity,worldRevision+1,actorIdentity,playerIdentity);
            snackReceipts.Add((actor.EntityId,proposal,trace),receipt);
            return Commit(skill,trace,"ServeSnack",actor,station,player,SkillStatus.Succeeded,"");
        }
    }
}
