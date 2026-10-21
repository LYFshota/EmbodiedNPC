using UnityEngine;
using EmbodiedNPC.Settings;

namespace CafeDemo
{
    /// <summary>Scene-owned music playback and one local volume preference.</summary>
    [RequireComponent(typeof(AudioSource))]
    [DisallowMultipleComponent]
    public sealed class CafeBackgroundMusic : MonoBehaviour
    {
        public const string VolumePreferenceKey = "EmbodiedNPC.Audio.BgmVolume";
        public const float DefaultVolume = .5f;
        private AudioSource source;
        private bool savePending;
        private float saveAt;

        public float Volume { get; private set; } = DefaultVolume;

        private void Awake()
        {
            source = GetComponent<AudioSource>();
            // The scene also disables Play On Awake, so the preference is applied before any sound.
            source.playOnAwake = false;
            Volume = ValidVolume(PortableDataPaths.IsPortable ? PortablePreferences.Current.BgmVolume :
                PlayerPrefs.GetFloat(VolumePreferenceKey, DefaultVolume));
            source.volume = Volume;
        }

        private void Start()
        {
            if (source.clip != null && !source.isPlaying) source.Play();
        }

        public void SetVolume(float value)
        {
            value = ValidVolume(value);
            if (Volume == value) return;
            Volume = value;
            source.volume = Volume;
            if (PortableDataPaths.IsPortable) PortablePreferences.Current.SetBgmVolume(Volume);
            else PlayerPrefs.SetFloat(VolumePreferenceKey, Volume);
            savePending = true;
            saveAt = Time.unscaledTime + .4f;
        }

        private static float ValidVolume(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? DefaultVolume : Mathf.Clamp01(value);

        private void Update()
        {
            if (savePending && Time.unscaledTime >= saveAt) FlushPreferences();
        }

        public void FlushPreferences()
        {
            if (!savePending) return;
            if (PortableDataPaths.IsPortable)
            {
                if (!PortablePreferences.Current.Flush()) Debug.LogError("[Portable] " + PortablePreferences.Current.Error);
            }
            else PlayerPrefs.Save();
            savePending = false;
        }

        private void OnDisable() => FlushPreferences();
        private void OnApplicationPause(bool paused) { if (paused) FlushPreferences(); }
        private void OnApplicationQuit() => FlushPreferences();
    }
}
