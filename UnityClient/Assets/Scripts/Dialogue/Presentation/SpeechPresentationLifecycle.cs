using System;
using UnityEngine;

namespace EmbodiedNPC.Dialogue.Presentation
{
    public readonly struct SpeechPresentation
    {
        public SpeechPresentation(long id, string utterance) { Id = id; Utterance = utterance; }
        public long Id { get; }
        public string Utterance { get; }
    }

    /// <summary>Per-speaker presentation interval, independent of bubble retention and cognition.</summary>
    [DisallowMultipleComponent]
    public sealed class SpeechPresentationLifecycle : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float minimumSeconds = 1.2f;
        [SerializeField, Min(0.1f)] private float maximumSeconds = 6f;
        [SerializeField, Min(1f)] private float charactersPerSecond = 12f;
        private long nextId;
        private double deadline;
        private bool textTimed;
        public event Action<SpeechPresentation> SpeechStarted;
        public event Action<SpeechPresentation> SpeechEnded;
        public bool IsSpeaking { get; private set; }
        public SpeechPresentation Current { get; private set; }

        public float TextDuration(string text) => Mathf.Clamp(
            minimumSeconds + (text?.Length ?? 0) / Mathf.Max(1f, charactersPerSecond),
            Mathf.Max(0.1f, minimumSeconds), Mathf.Max(minimumSeconds, maximumSeconds));
        public long BeginPresentedText(string text, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return 0;
            long id = BeginPlayback(text);
            if (id == 0) return 0;
            textTimed = true; deadline = now + TextDuration(text);
            return id;
        }
        // Future TTS supplies real playback start/finish instead of the text timer.
        public long BeginPlayback(string utterance)
        {
            if (!isActiveAndEnabled || string.IsNullOrWhiteSpace(utterance)) return 0;
            Cancel();
            Current = new SpeechPresentation(++nextId, utterance);
            IsSpeaking = true; textTimed = false;
            Publish(SpeechStarted, Current);
            return Current.Id;
        }
        public bool EndPlayback(long id)
        {
            if (!IsSpeaking || id != Current.Id) return false;
            SpeechPresentation ended = Current;
            IsSpeaking = false; textTimed = false; Current = default;
            Publish(SpeechEnded, ended);
            return true;
        }
        public void Cancel() { if (IsSpeaking) EndPlayback(Current.Id); }
        public void Tick(double now) { if (IsSpeaking && textTimed && now >= deadline) EndPlayback(Current.Id); }
        private void Update() => Tick(Time.unscaledTimeAsDouble);
        private void OnDisable() => Cancel();
        private void OnDestroy() => Cancel();
        private void Publish(Action<SpeechPresentation> observers, SpeechPresentation value)
        {
            if (observers == null) return;
            foreach (Delegate observer in observers.GetInvocationList())
                try { ((Action<SpeechPresentation>)observer)(value); }
                catch (Exception exception) { Debug.LogWarning($"[Speech] observer failed: {exception.GetType().Name}", this); }
        }
    }
}
