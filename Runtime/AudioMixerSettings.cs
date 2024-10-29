using UnityEngine;
using UnityEngine.Audio;

using System.Collections.Generic;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ScriptableObjectAudioSystem
{
    /// <summary>
    /// ScriptableObject that manages Audio Mixer settings
    /// </summary>
    [CreateAssetMenu(fileName = "New Audio Mixer Settings", menuName = "Audio/Mixer Settings")]
    public class AudioMixerSettings : ScriptableObject
    {
        #region Fields

        [SerializeField] private AudioMixer audioMixer;
        [SerializeField] private AudioMixerGroup mixerGroupReferences;
        [Range(0f, 1f)] public float defaultVolume = 1f;

        // PlayerPrefs key constant
        private const string VOLUME_PREFS_KEY = "AudioVolume_";

        private float lastVolumes = 1f;

        public AudioMixerGroup MixerGroupReferences { get => mixerGroupReferences; private set => mixerGroupReferences = value; }

        #endregion

        #region Unity Methods

        protected virtual void OnEnable()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        protected virtual void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
        }


#if UNITY_EDITOR
        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    InitializeRuntimeData();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    CleanupRuntimeData();
                    break;
            }
        }
#endif

        /// <summary>
        /// Method to initialize runtime data when entering play mode
        /// </summary>
        protected virtual void InitializeRuntimeData()
        {
            LoadSettings();
        }

        /// <summary>
        /// Method to clean up runtime data when exiting play mode
        /// </summary>
        protected virtual void CleanupRuntimeData()
        {
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets the volume
        /// </summary>
        public void SetGroupVolume(float volume)
        {
            if (audioMixer == null) return;

            float decibelValue = volume > 0 ? 20f * Mathf.Log10(volume) : -80f;
            string paramName = $"{mixerGroupReferences.name}";

            if (!audioMixer.SetFloat(paramName, decibelValue))
            {
                Debug.LogWarning($"Failed to set volume for {paramName}");
            }

            lastVolumes = volume;

            // Save volume settings
            SaveSettings();
        }

        /// <summary>
        /// Gets the volume
        /// </summary>
        public float GetGroupVolume()
        {
            if (audioMixer == null) return 1f;

            string paramName = $"{mixerGroupReferences.name}";

            // Get current value from mixer
            if (audioMixer.GetFloat(paramName, out float decibelValue))
            {
                float volume = decibelValue <= -80f ? 0f : Mathf.Pow(10f, decibelValue / 20f);
                lastVolumes = volume;
                return volume;
            }

            return defaultVolume;
        }

        /// <summary>
        /// Resets the volume to default value
        /// </summary>
        public void ResetVolumes()
        {
            if (mixerGroupReferences == null) return;

            SetGroupVolume(defaultVolume);
            SaveSettings();
        }

        /// <summary>
        /// Mutes the volume
        /// </summary>
        public void MuteGroup()
        {
            string paramName = $"{mixerGroupReferences.name}";
            audioMixer.SetFloat(paramName, -80f);
            SaveSettings();
        }

        #endregion

        #region Save/Load Methods

        /// <summary>
        /// Saves current audio settings
        /// </summary>
        public void SaveSettings()
        {
            if (mixerGroupReferences == null) return;

            string key = VOLUME_PREFS_KEY + mixerGroupReferences.name;
            PlayerPrefs.SetFloat(key, lastVolumes);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Loads saved audio settings
        /// </summary>
        public void LoadSettings()
        {
            if (mixerGroupReferences == null) return;

            string key = VOLUME_PREFS_KEY + mixerGroupReferences.name;
            float savedVolume = PlayerPrefs.GetFloat(key, defaultVolume);
            Debug.Log(savedVolume);
            SetGroupVolume(savedVolume);
        }

        /// <summary>
        /// Deletes all saved audio settings
        /// </summary>
        public void ClearSettings()
        {
            if (mixerGroupReferences == null) return;

            string key = VOLUME_PREFS_KEY + mixerGroupReferences.name;
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }

        #endregion

        #region Unity Editor Methods

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying) return;

            // Validate Mixer
            if (audioMixer == null)
            {
                Debug.LogWarning("[AudioMixerSettings] AudioMixer reference is missing");
                return;
            }
        }
#endif

        #endregion
    }
}