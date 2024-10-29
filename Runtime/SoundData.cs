using System.Collections.Generic;
using System.Threading;
using System.Linq;

using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

using Cysharp.Threading.Tasks;

namespace ScriptableObjectAudioSystem
{
    /// <summary>
    /// Manages sound data and playback functionality with object pooling and fade effects
    /// </summary>
    [CreateAssetMenu(fileName = "New Sound Data", menuName = "Audio/Sound Data")]
    public class SoundData : ScriptableObject
    {
        #region Fields

        [Header("Pool Settings")]
        [SerializeField] private int initialPoolSize = 1;
        [SerializeField] private int maxPoolSize = 5;

        [Header("Mixer Settings")]
        [SerializeField] private AudioMixerSettings audioMixerSettings;

        [Tooltip("If enabled, only one sound can play at a time from this SoundData")]
        public bool exclusivePlay = false;

        public SoundInfo[] sounds;

        private readonly Dictionary<string, bool> fadeOutInProgress = new();
        private readonly Dictionary<string, CancellationTokenSource> fadeTokens = new();
        private readonly Dictionary<string, CancellationTokenSource> playTokens = new();
        private readonly Dictionary<string, AsyncOperationHandle<AudioClip>> loadHandles = new();
        private readonly Dictionary<string, List<AudioSourceInfo>> activeAudioSources = new();
        private readonly Dictionary<string, Queue<AudioSourceInfo>> audioSourcePool = new();
        private Transform poolContainer;

        #endregion

        #region Data Classes

        /// <summary>
        /// Contains configuration data for a single sound effect
        /// </summary>
        [System.Serializable]
        public class SoundInfo
        {
            public string soundId;
            public AssetReference audioClipReference;
            [Range(0f, 1f)] public float volume = 1f;
            public bool loop = false;
            [Range(-3f, 3f)] public float pitch = 1f;
            [Range(0f, 1f)] public float spatialBlend = 0f;
            public Vector3? position = null;
            [Tooltip("Maximum number of simultaneous plays for non-looping sounds")]
            public int maxSimultaneousPlays = 3;

            [Header("Fade Settings")]
            public bool useFadeEffects = false;
            public float fadeInDuration = 0.1f;
            public float fadeOutDuration = 0.1f;

            /// <summary>
            /// Validates the sound configuration parameters
            /// </summary>
            public bool Validate(out string error)
            {
                if (string.IsNullOrEmpty(soundId))
                {
                    error = "Sound ID cannot be empty";
                    return false;
                }
                if (audioClipReference == null)
                {
                    error = "AudioClip Reference cannot be null";
                    return false;
                }
                if (pitch == 0f)
                {
                    error = "Pitch cannot be 0";
                    pitch = 1f;
                    return false;
                }
                if (maxSimultaneousPlays <= 0)
                {
                    error = "Max simultaneous plays must be greater than 0";
                    maxSimultaneousPlays = 1;
                    return false;
                }
                error = null;
                return true;
            }
        }

        /// <summary>
        /// Tracks the state of an AudioSource instance in the pool
        /// </summary>
        private class AudioSourceInfo
        {
            public AudioSource source;
            public float startTime;
            public bool isPlaying;
            public GameObject gameObject;
            public string currentSoundId;

            public AudioSourceInfo(AudioSource source, GameObject gameObject)
            {
                this.source = source;
                this.gameObject = gameObject;
                Reset();
            }

            public void Reset()
            {
                startTime = 0f;
                isPlaying = false;
                currentSoundId = string.Empty;
                if (source != null)
                {
                    source.Stop();
                    source.clip = null;
                    source.volume = 1f;
                    source.pitch = 1f;
                    source.loop = false;
                    source.spatialBlend = 0f;
                }
            }
        }

        #endregion

        #region Unity Editor Methods

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying)
            {
                return;
            }

            if (sounds == null) return;
            for (int i = 0; i < sounds.Length; i++)
            {
                sounds[i] ??= new SoundInfo();
                if (!sounds[i].Validate(out string error))
                {
                    Debug.LogWarning($"[SoundData] Invalid sound at index {i}: {error}");
                }
            }
        }
#endif

        #endregion

        #region Object Pool Management

        /// <summary>
        /// Ensures the pool container exists in the scene
        /// </summary>
        private void EnsurePoolContainer()
        {
            if (poolContainer != null) return;
            var containerObj = new GameObject("SoundPool_Container");
            Object.DontDestroyOnLoad(containerObj);
            poolContainer = containerObj.transform;
        }

        /// <summary>
        /// Initializes the audio source pool for a specific sound ID
        /// </summary>
        private void InitializePool(string soundId)
        {
            if (!audioSourcePool.ContainsKey(soundId))
            {
                audioSourcePool[soundId] = new Queue<AudioSourceInfo>();
                EnsurePoolContainer();

                var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
                if (soundInfo == null) return;

                for (int i = 0; i < initialPoolSize; i++)
                {
                    CreatePoolObject(soundId);
                }
            }
        }

        /// <summary>
        /// Creates a new pooled audio source object if pool size limit allows
        /// </summary>
        private bool CreatePoolObject(string soundId)
        {
            if (!audioSourcePool.ContainsKey(soundId))
            {
                audioSourcePool[soundId] = new Queue<AudioSourceInfo>();
            }

            // Check current pool size including both pooled and active sources
            int totalSources = audioSourcePool[soundId].Count;
            if (activeAudioSources.TryGetValue(soundId, out var activeSources))
            {
                totalSources += activeSources.Count;
            }

            // Don't create if we're at or over the max pool size
            if (totalSources >= maxPoolSize)
            {
                return false;
            }

            var audioObject = new GameObject($"PooledSound_{soundId}");
            audioObject.transform.SetParent(poolContainer);
            var audioSource = audioObject.AddComponent<AudioSource>();
            var sourceInfo = new AudioSourceInfo(audioSource, audioObject);
            audioObject.SetActive(false);
            audioSourcePool[soundId].Enqueue(sourceInfo);
            return true;
        }


        /// <summary>
        /// Retrieves an available audio source from the pool if within size limits
        /// </summary>
        private AudioSourceInfo GetAudioSourceFromPool(string soundId)
        {
            InitializePool(soundId);

            AudioSourceInfo sourceInfo = null;
            var pool = audioSourcePool[soundId];

            // Clean up any null or destroyed objects from the pool
            while (pool.Count > 0)
            {
                var pooledInfo = pool.Peek();
                if (pooledInfo != null && pooledInfo.gameObject != null)
                {
                    sourceInfo = pool.Dequeue();
                    break;
                }
                else
                {
                    pool.Dequeue();
                }
            }

            // Calculate total sources (pooled + active)
            int totalSources = pool.Count;
            if (activeAudioSources.TryGetValue(soundId, out var activeSources))
            {
                totalSources += activeSources.Count;
            }

            // If no available source and we're under the limit, create new one
            if (sourceInfo == null && totalSources < maxPoolSize)
            {
                if (CreatePoolObject(soundId))
                {
                    sourceInfo = pool.Count > 0 ? pool.Dequeue() : null;
                }
            }

            if (sourceInfo != null)
            {
                sourceInfo.Reset();
                sourceInfo.gameObject.SetActive(true);
                sourceInfo.currentSoundId = soundId;
                sourceInfo.gameObject.transform.position = Vector3.zero;
            }
            else
            {
                Debug.LogWarning($"[SoundData] Unable to get audio source from pool for {soundId}. Max pool size ({maxPoolSize}) reached.");
            }

            return sourceInfo;
        }

        /// <summary>
        /// Returns an audio source to the pool
        /// </summary>
        private void ReturnToPool(AudioSourceInfo sourceInfo)
        {
            if (sourceInfo == null || string.IsNullOrEmpty(sourceInfo.currentSoundId)) return;

            string soundId = sourceInfo.currentSoundId;

            sourceInfo.Reset();
            sourceInfo.gameObject.SetActive(false);
            sourceInfo.gameObject.transform.SetParent(poolContainer);
            sourceInfo.gameObject.transform.position = Vector3.zero;

            if (!audioSourcePool.ContainsKey(soundId))
            {
                audioSourcePool[soundId] = new Queue<AudioSourceInfo>();
            }

            var pool = audioSourcePool[soundId];

            if (pool.Count < maxPoolSize && !pool.Any(x => x.gameObject == sourceInfo.gameObject))
            {
                pool.Enqueue(sourceInfo);
            }
            else if (pool.Count >= maxPoolSize)
            {
                Object.Destroy(sourceInfo.gameObject);
            }
        }

        #endregion

        #region Fade Effect Management

        /// <summary>
        /// Handles volume fade effects for an audio source
        /// </summary>
        private async UniTask FadeVolume(AudioSource source, float startVolume, float targetVolume,
                float duration, CancellationToken token)
        {
            if (source == null)
            {
                Debug.LogWarning("AudioSource is null at the start of fade operation");
                return;
            }

            float elapsedTime = 0f;
            float initialVolume = startVolume;

            while (elapsedTime < duration)
            {
                token.ThrowIfCancellationRequested();

                if (source == null)
                {
                    Debug.LogWarning("AudioSource has been destroyed during fade operation");
                    return;
                }

                elapsedTime += Time.deltaTime;
                float t = elapsedTime / duration;
                source.volume = Mathf.Lerp(initialVolume, targetVolume, t);
                await UniTask.Yield(token);
            }

            if (source != null)
            {
                source.volume = targetVolume;
            }
        }

        /// <summary>
        /// Manages fade effects for audio sources with cleanup handling
        /// </summary>
        private async UniTask HandleFadeEffect(AudioSource source, string soundId, bool isFadeIn,
            float duration, float targetVolume, CancellationToken cancellationToken)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                if (source == null)
                {
                    Debug.LogWarning($"HandleFadeEffect: AudioSource is null for sound {soundId}");
                    return;
                }

                if (isFadeIn)
                {
                    // Cleanup previous fade tokens before starting new fade in
                    if (fadeTokens.TryGetValue(soundId, out var existingCts))
                    {
                        existingCts.Cancel();
                        // Move disposal to finally block
                    }
                    fadeOutInProgress[soundId] = false;
                    fadeTokens[soundId] = cts;
                }
                else
                {
                    fadeOutInProgress[soundId] = true;
                    if (!fadeTokens.ContainsKey(soundId))
                    {
                        fadeTokens[soundId] = cts;
                    }
                }

                float startVolume = source.volume;
                await FadeVolume(source, startVolume, targetVolume, duration, cts.Token);

                // Handle successful fade out completion
                if (!isFadeIn && fadeOutInProgress.TryGetValue(soundId, out bool isFading) && isFading)
                {
                    source.Stop();
                    if (activeAudioSources.TryGetValue(soundId, out var sources))
                    {
                        var sourceInfo = sources.FirstOrDefault(s => s.source == source);
                        if (sourceInfo != null)
                        {
                            sources.Remove(sourceInfo);
                            ReturnToPool(sourceInfo);
                        }
                    }
                }
            }
            catch (System.OperationCanceledException)
            {
                // Handle fade cancellation
                if (!isFadeIn && fadeOutInProgress.TryGetValue(soundId, out bool isFading) && isFading)
                {
                    source.Stop();
                    if (activeAudioSources.TryGetValue(soundId, out var sources))
                    {
                        var sourceInfo = sources.FirstOrDefault(s => s.source == source);
                        if (sourceInfo != null)
                        {
                            sources.Remove(sourceInfo);
                            ReturnToPool(sourceInfo);
                        }
                    }
                }
            }
            finally
            {
                // Cleanup only if this is the current token for the sound
                if (fadeTokens.TryGetValue(soundId, out var currentCts) && currentCts == cts)
                {
                    fadeTokens.Remove(soundId);
                    if (!isFadeIn || (fadeOutInProgress.TryGetValue(soundId, out bool isFading) && isFading))
                    {
                        fadeOutInProgress.Remove(soundId);
                    }
                }

                // Always dispose of the CancellationTokenSource
                cts.Dispose();

                // If there was an existing CTS that we replaced, dispose it here
                if (isFadeIn && fadeTokens.TryGetValue(soundId, out var existingCts) && existingCts != cts)
                {
                    existingCts.Dispose();
                }
            }
        }

        /// <summary>
        /// Cancels all active fade effects for a sound
        /// </summary>
        private void CancelAllFadeEffects(string soundId)
        {
            if (fadeTokens.TryGetValue(soundId, out var existingCts))
            {
                // Remove from dictionary first before canceling
                fadeTokens.Remove(soundId);

                try
                {
                    if (!existingCts.IsCancellationRequested && !existingCts.Token.IsCancellationRequested)
                    {
                        existingCts.Cancel();
                    }
                }
                catch (System.ObjectDisposedException)
                {
                    // Ignore if already disposed
                }
                finally
                {
                    try
                    {
                        existingCts.Dispose();
                    }
                    catch (System.ObjectDisposedException)
                    {
                        // Ignore if already disposed
                    }
                }
            }
            fadeOutInProgress[soundId] = false;
        }


        #endregion

        #region Audio Playback Management

        /// <summary>
        /// Loads an audio clip asynchronously
        /// </summary>
        private async UniTask<AudioClip> LoadAudioClip(string soundId, SoundInfo soundInfo, CancellationToken token)
        {
            // 이전 핸들 처리를 더 안전하게 수행
            if (loadHandles.TryGetValue(soundId, out var existingHandle))
            {
                try
                {
                    if (existingHandle.IsValid())
                    {
                        // 이미 완료된 경우에만 결과 반환
                        if (existingHandle.Status == AsyncOperationStatus.Succeeded)
                        {
                            return existingHandle.Result;
                        }

                        // 진행 중인 경우 완료 대기
                        if (existingHandle.Status == AsyncOperationStatus.None)
                        {
                            await existingHandle.WithCancellation(token);
                            if (existingHandle.Status == AsyncOperationStatus.Succeeded)
                            {
                                return existingHandle.Result;
                            }
                        }
                    }

                    // 실패하거나 유효하지 않은 핸들은 해제
                    Addressables.Release(existingHandle);
                    loadHandles.Remove(soundId);
                }
                catch (System.Exception)
                {
                    // 예외 발생 시 핸들 정리
                    if (existingHandle.IsValid())
                    {
                        Addressables.Release(existingHandle);
                    }
                    loadHandles.Remove(soundId);
                }
            }

            // 새로운 로딩 작업 시작
            var operation = soundInfo.audioClipReference.LoadAssetAsync<AudioClip>();
            loadHandles[soundId] = operation;

            try
            {
                await operation.WithCancellation(token);
                return operation.Status == AsyncOperationStatus.Succeeded ? operation.Result : null;
            }
            catch (System.Exception)
            {
                if (operation.IsValid())
                {
                    Addressables.Release(operation);
                }
                loadHandles.Remove(soundId);
                throw;
            }
        }

        /// <summary>
        /// Plays an audio clip with the specified settings
        /// </summary>
        private async UniTask PlayClip(string soundId, SoundInfo soundInfo, AudioClip clip,
            CancellationToken token)
        {
            var sourceInfo = GetAudioSourceFromPool(soundId);
            if (sourceInfo == null) return;

            var source = sourceInfo.source;
            if (soundInfo.position.HasValue)
            {
                sourceInfo.gameObject.transform.position = soundInfo.position.Value;
            }

            source.outputAudioMixerGroup = audioMixerSettings.MixerGroupReferences;

            source.clip = clip;
            source.volume = soundInfo.useFadeEffects ? 0f : soundInfo.volume;
            source.loop = soundInfo.loop;
            source.pitch = soundInfo.pitch;
            source.spatialBlend = soundInfo.spatialBlend;

            sourceInfo.startTime = Time.time;
            sourceInfo.isPlaying = true;

            activeAudioSources.TryAdd(soundId, new List<AudioSourceInfo>());
            activeAudioSources[soundId].Add(sourceInfo);

            source.Play();

            if (soundInfo.useFadeEffects && soundInfo.fadeInDuration > 0)
            {
                await HandleFadeEffect(source, soundId, true, soundInfo.fadeInDuration,
                    soundInfo.volume, token);
            }

            if (!soundInfo.loop)
            {
                try
                {
                    await UniTask.Delay((int)(clip.length * 1000), cancellationToken: token);
                    sourceInfo.isPlaying = false;
                    await CleanupSound(soundId, sourceInfo);
                }
                catch (System.OperationCanceledException)
                {
                    await CleanupSound(soundId, sourceInfo);
                }
            }
        }

        /// <summary>
        /// Manages active audio sources for a sound
        /// </summary>
        private async UniTask ManageActiveSources(string soundId, SoundInfo soundInfo,
            CancellationToken cancellationToken)
        {
            if (!activeAudioSources.TryGetValue(soundId, out var sources))
            {
                sources = new List<AudioSourceInfo>();
                activeAudioSources[soundId] = sources;
            }

            if (!soundInfo.loop)
            {
                var playingSources = sources.Where(s => s.source != null && s.source.isPlaying).ToList();
                sources.RemoveAll(s =>
                    s.source == null ||
                    (!s.source.isPlaying && Time.time - s.startTime > s.source.clip.length));

                if (playingSources.Count >= soundInfo.maxSimultaneousPlays)
                {
                    var oldestSource = playingSources.OrderBy(s => s.startTime).FirstOrDefault();
                    if (oldestSource != null)
                    {
                        if (soundInfo.useFadeEffects)
                        {
                            await HandleFadeEffect(oldestSource.source, soundId, false,
                                soundInfo.fadeOutDuration, 0f, cancellationToken);
                        }
                        else
                        {
                            oldestSource.source.Stop();
                        }
                        oldestSource.isPlaying = false;
                        sources.Remove(oldestSource);
                        ReturnToPool(oldestSource);
                    }
                }
            }
            else
            {
                if (!fadeOutInProgress.TryGetValue(soundId, out bool isFading) || !isFading ||
                    !soundInfo.useFadeEffects)
                {
                    foreach (var source in sources.ToList())
                    {
                        if (source.source != null)
                        {
                            if (soundInfo.useFadeEffects)
                            {
                                await HandleFadeEffect(source.source, soundId, false,
                                    soundInfo.fadeOutDuration, 0f, cancellationToken);
                            }
                            else
                            {
                                source.source.Stop();
                            }
                            ReturnToPool(source);
                        }
                    }
                    sources.Clear();
                }
            }
        }

        #endregion

        #region Public Playback Methods

        /// <summary>
        /// Plays a sound with the specified ID
        /// </summary>
        public void Play(string soundId)
        {
            var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
            string error = "";
            if (soundInfo == null || !soundInfo.Validate(out error))
            {
                Debug.LogError($"[SoundData] Invalid sound '{soundId}': {error}");
                return;
            }

            // Check for exclusive play mode
            if (exclusivePlay)
            {
                var playingSounds = sounds.Where(s => IsPlaying(s.soundId));
                foreach (var playing in playingSounds)
                {
                    Stop(playing.soundId);
                }
            }

            if (soundInfo.loop && IsPlaying(soundId) &&
                (!fadeOutInProgress.TryGetValue(soundId, out bool fading) || !fading))
            {
                Debug.Log($"[SoundData] Looping sound '{soundId}' already playing");
                return;
            }
            else
            {
                CancelAllFadeEffects(soundId);
            }

            var cts = new CancellationTokenSource();
            playTokens[soundId] = cts;

            try
            {
                PlayInternal(soundId, cts.Token).Forget();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SoundData] Error playing '{soundId}': {e}");
                if (playTokens.TryGetValue(soundId, out var tokenSource))
                {
                    tokenSource.Dispose();
                    playTokens.Remove(soundId);
                }
            }
        }

        /// <summary>
        /// Plays a sound at a specific position
        /// </summary>
        public void PlayAtPosition(string soundId, Vector3 position)
        {
            var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
            if (soundInfo == null)
            {
                Debug.LogError($"[SoundData] Sound '{soundId}' not found");
                return;
            }
            soundInfo.position = position;
            Play(soundId);
        }

        /// <summary>
        /// Plays a random sound from the available sounds
        /// </summary>
        public void PlayRandom()
        {
            if (sounds?.Length > 0)
            {
                var availableSounds = sounds;
                if (exclusivePlay)
                {
                    availableSounds = sounds.Where(s => !IsPlaying(s.soundId)).ToArray();
                }

                if (availableSounds.Length > 0)
                {
                    Play(availableSounds[Random.Range(0, availableSounds.Length)].soundId);
                }
            }
        }

        /// <summary>
        /// Plays a random sound at a specific position
        /// </summary>
        public void PlayRandomAtPosition(Vector3 position) =>
            PlayAtPosition(sounds?.Length > 0 ? sounds[Random.Range(0, sounds.Length)].soundId : null, position);

        #endregion

        #region Private Playback Methods

        /// <summary>
        /// Checks if a sound is currently playing
        /// </summary>
        private bool IsPlaying(string soundId) =>
                activeAudioSources.TryGetValue(soundId, out var sources) &&
                sources.Any(s => s.source != null && s.source.isPlaying);

        /// <summary>
        /// Internal method for playing a sound with all necessary setup and error handling
        /// </summary>
        private async UniTaskVoid PlayInternal(string soundId, CancellationToken cancellationToken)
        {
            try
            {
                var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
                if (soundInfo == null) return;

                if (soundInfo.useFadeEffects)
                {
                    if (activeAudioSources.TryGetValue(soundId, out var sources))
                    {
                        var fadingSource = sources.FirstOrDefault(s =>
                            s.source != null &&
                            s.source.isPlaying &&
                            s.source.volume < soundInfo.volume);

                        if (fadingSource != null)
                        {
                            float currentVolume = fadingSource.source.volume;
                            float remainingFadeTime = soundInfo.fadeInDuration *
                                (soundInfo.volume - currentVolume) / soundInfo.volume;

                            try
                            {
                                await FadeVolume(
                                    fadingSource.source,
                                    currentVolume,
                                    soundInfo.volume,
                                    remainingFadeTime,
                                    cancellationToken
                                );
                                return;
                            }
                            catch (System.OperationCanceledException)
                            {
                                // Proceed with new playback if fade-in is cancelled
                            }
                        }
                    }
                }

                await ManageActiveSources(soundId, soundInfo, cancellationToken);
                var clip = await LoadAudioClip(soundId, soundInfo, cancellationToken);

                if (clip != null)
                {
                    await PlayClip(soundId, soundInfo, clip, cancellationToken);
                }
            }
            catch (System.OperationCanceledException)
            {
                if (!fadeOutInProgress.TryGetValue(soundId, out bool isFading) || !isFading)
                {
                    await CleanupSound(soundId);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SoundData] Error playing '{soundId}': {e}");
                await CleanupSound(soundId);
            }
        }

        #endregion

        #region Cleanup Methods

        /// <summary>
        /// Cleans up resources for a specific sound
        /// </summary>
        private async UniTask CleanupSound(string soundId, AudioSourceInfo specificSource = null)
        {
            if (specificSource != null)
            {
                if (activeAudioSources.TryGetValue(soundId, out var sources))
                {
                    var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
                    if (soundInfo?.useFadeEffects == true && soundInfo.fadeOutDuration > 0
                        && specificSource.source.isPlaying)
                    {
                        await HandleFadeEffect(specificSource.source, soundId, false,
                            soundInfo.fadeOutDuration, 0f, CancellationToken.None);
                    }

                    sources.Remove(specificSource);
                    ReturnToPool(specificSource);

                    if (sources.Count == 0)
                    {
                        await CleanupResources(soundId);
                    }
                }
            }
            else
            {
                if (activeAudioSources.TryGetValue(soundId, out var sources))
                {
                    var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
                    var tasks = new List<UniTask>();

                    foreach (var source in sources.ToList())
                    {
                        if (source.source != null && source.source.isPlaying &&
                            soundInfo?.useFadeEffects == true && soundInfo.fadeOutDuration > 0)
                        {
                            tasks.Add(HandleFadeEffect(source.source, soundId, false,
                                soundInfo.fadeOutDuration, 0f, CancellationToken.None));
                        }
                        ReturnToPool(source);
                    }

                    if (tasks.Count > 0)
                    {
                        await UniTask.WhenAll(tasks);
                    }
                    sources.Clear();
                }
                await CleanupResources(soundId);
            }
        }

        /// <summary>
        /// Cleans up resources associated with a sound ID
        /// </summary>
        private async UniTask CleanupResources(string soundId)
        {
            if (fadeTokens.TryGetValue(soundId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                fadeTokens.Remove(soundId);
            }

            if (activeAudioSources.TryGetValue(soundId, out var sources))
            {
                foreach (var source in sources)
                {
                    ReturnToPool(source);
                }
                sources.Clear();
                activeAudioSources.Remove(soundId);
            }

            if (loadHandles.TryGetValue(soundId, out var handle) && handle.IsValid())
            {
                var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
                if (soundInfo != null && !soundInfo.loop)
                {
                    await UniTask.SwitchToMainThread();
                    Addressables.Release(handle);
                    loadHandles.Remove(soundId);
                }
            }

            fadeOutInProgress.Remove(soundId);
        }

        #endregion

        #region Stop Methods

        /// <summary>
        /// Stops playing a specific sound
        /// </summary>
        public void Stop(string soundId)
        {
            var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
            if (soundInfo?.useFadeEffects == true)
            {
                StopWithFade(soundId).Forget();
            }
            else
            {
                CleanupSound(soundId).Forget();
            }
        }

        /// <summary>
        /// Stops all currently playing sounds
        /// </summary>
        public void StopAll()
        {
            foreach (var soundId in activeAudioSources.Keys.ToList())
            {
                Stop(soundId);
            }
        }

        /// <summary>
        /// Stops a sound with fade effect
        /// </summary>
        private async UniTaskVoid StopWithFade(string soundId)
        {
            if (activeAudioSources.TryGetValue(soundId, out var sources))
            {
                var soundInfo = sounds.FirstOrDefault(s => s.soundId == soundId);
                if (soundInfo == null) return;

                var cts = new CancellationTokenSource();

                try
                {
                    fadeTokens[soundId] = cts;
                    fadeOutInProgress[soundId] = true;

                    var tasks = sources
                        .Where(s => s.source != null && s.source.isPlaying)
                        .Select(s => HandleFadeEffect(
                            s.source,
                            soundId,
                            false,
                            soundInfo.fadeOutDuration,
                            0f,
                            cts.Token));

                    await UniTask.WhenAll(tasks);
                }
                catch (System.OperationCanceledException)
                {
                    foreach (var source in sources)
                    {
                        if (source?.source != null)
                        {
                            source.source.Stop();
                        }
                    }
                }
                finally
                {
                    cts.Dispose();
                }
            }
        }

        #endregion

        #region Resource Management

        /// <summary>
        /// Releases all resources and cleans up the audio system
        /// </summary>
        public void ReleaseAll()
        {
            foreach (var cts in fadeTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            fadeTokens.Clear();

            foreach (var cts in playTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            playTokens.Clear();

            foreach (var sources in activeAudioSources.Values)
            {
                foreach (var source in sources)
                {
                    if (source?.gameObject != null)
                    {
                        ReturnToPool(source);
                    }
                }
            }

            activeAudioSources.Clear();

            foreach (var pool in audioSourcePool.Values)
            {
                while (pool.Count > 0)
                {
                    var sourceInfo = pool.Dequeue();
                    if (sourceInfo?.gameObject != null)
                    {
                        Object.Destroy(sourceInfo.gameObject);
                    }
                }
            }

            audioSourcePool.Clear();

            foreach (var handle in loadHandles.Values.Where(h => h.IsValid()))
            {
                Addressables.Release(handle);
            }

            loadHandles.Clear();

            fadeOutInProgress.Clear();

            if (poolContainer != null)
            {
                Object.Destroy(poolContainer.gameObject);
                poolContainer = null;
            }
        }

        /// <summary>
        /// Called when the ScriptableObject is disabled
        /// </summary>
        private void OnDisable() => ReleaseAll();

        #endregion
    }
}