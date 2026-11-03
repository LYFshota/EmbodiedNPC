using System;
using UnityEngine;

namespace EmbodiedNPC.Routines
{
    public enum DailyAmbientActivity { CounterMove, CounterWipe, SeatedDrink }
    [Serializable]
    public sealed class DailyAmbientBinding
    {
        public DailyBehaviorMode mode;
        public DailyAmbientActivity activity;
        public DailyAmbientBinding(DailyBehaviorMode mode, DailyAmbientActivity activity)
        { this.mode = mode; this.activity = activity; }
    }
    [Serializable]
    public sealed class DailyAmbientWorkPoint
    {
        public Transform point;
        public bool allowWipe;
    }
    // Two actual executors (counter and seated cup), one local selection owner. No world authority.
    public interface IDailyAmbientPresenter
    {
        bool CanPresent(DailyAmbientActivity activity);
        bool BeginAmbient(DailyAmbientActivity activity, long instance, Transform point, float dwellSeconds);
        bool TickAmbient(long instance, float deltaTime);
        bool IsContextValid(long instance);
        bool TryBeginRecovery(long instance);
        void EndAmbient(long instance);
    }
}
