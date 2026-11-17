using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ambient + character audio. A single global ambience playlist is shuffled
/// without repeats and crossfaded between tracks, independent of room changes.
/// Optional rare one-shot room sounds are scheduled while the player stays in
/// a room. Footsteps remain synchronized with the character's step phase.
/// </summary>
[System.Serializable]
public sealed class AdditionalFootstepSound
{
    [Tooltip("Дополнительный звук, который иногда накладывается поверх обычного шага.")]
    public AudioClip clip;
    [Tooltip("Индивидуальная громкость именно этого звука относительно общего ползунка.")]
    [Range(0f, 2f)] public float volumeMultiplier = 1f;
}

[DisallowMultipleComponent]
public class AmbientAudioController : MonoBehaviour
{
    public static AmbientAudioController Instance { get; private set; }

    [Header("Volumes (0..1)")]
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float windVolume = 0.5f;
    [Range(0f, 1f)] public float breathVolume = 0.3f;
    [Range(0f, 1f)] public float footstepVolume = 0.8f;

    [Header("Additional footstep sounds (random one-shots layered over steps)")]
    [Tooltip("Chance that one extra sound plays on each footstep. 0 = disabled; 0.15 = about one in seven steps.")]
    [Range(0f, 1f)] public float additionalFootstepChance = 0.14f;
    [Tooltip("Overall volume of additional footstep sounds. This is independent from the main footstep volume.")]
    [Range(0f, 1f)] public float additionalFootstepVolume = 0.28f;
    [Tooltip("Large pool of extra Foley sounds: cloth, gear, creaks, gravel accents, etc. A shuffled bag prevents immediate repeats until the pool has played through.")]
    public AdditionalFootstepSound[] additionalFootstepSounds = new AdditionalFootstepSound[0];

    [Header("Clips")]
    public AudioClip windLoop;
    public AudioClip breathLoop;
    public AudioClip[] footstepClips;

    [Header("Global ambience playlist (same playlist in every room)")]
    [Tooltip("All ambience tracks go here. The controller plays them in shuffled order, without repeating a track until the whole list has played.")]
    public AudioClip[] ambiencePlaylist;
    [Range(0f, 1f)] public float ambienceVolume = 0.32f;
    [Min(0.25f)] public float ambienceCrossfadeSeconds = 9f;

    [Header("Rare room sounds (one-shots after staying in a room)")]
    [Tooltip("Rare background sounds for ordinary indoor rooms.")]
    public AudioClip[] indoorRoomEvents;
    [Tooltip("Rare background sounds for corridors.")]
    public AudioClip[] corridorRoomEvents;
    [Tooltip("Rare background sounds for rooftops.")]
    public AudioClip[] rooftopRoomEvents;
    [Tooltip("Rare background sounds for pool rooms.")]
    public AudioClip[] poolRoomEvents;
    [Tooltip("Rare background sounds for stairwells.")]
    public AudioClip[] stairsRoomEvents;
    [Tooltip("Rare background sounds for metro platforms.")]
    public AudioClip[] metroRoomEvents;
    [Tooltip("Rare background sounds for open outdoor rooms.")]
    public AudioClip[] outdoorRoomEvents;
    [Min(1f)] public float roomEventMinimumDelay = 38f;
    [Min(1f)] public float roomEventMaximumDelay = 78f;
    [Range(0f, 1f)] public float roomEventVolume = 0.48f;

    [Header("Indoor wind acoustics")]
    [Tooltip("Wind volume multiplier when the player is inside a protected building.")]
    [Range(0f, 1f)] public float indoorWindVolume = 0.12f;
    [Tooltip("Low-pass cutoff for the wind while sheltered. Lower values sound more muffled.")]
    [Range(100f, 5000f)] public float indoorWindCutoff = 900f;
    [Tooltip("How quickly wind volume and filtering transition when entering/leaving shelter.")]
    [Min(0.01f)] public float indoorFadeSpeed = 3f;

    [Header("Footstep feel")]
    public float footstepPitchMin = 0.92f;
    public float footstepPitchMax = 1.08f;

    [Header("Procedural room echo")]
    [Tooltip("How quickly echo/reverb changes after entering another room.")]
    [Min(0.1f)] public float roomAcousticsBlendSpeed = 2.8f;

    private sealed class RoomAudioEffects
    {
        public AudioEchoFilter echo;
        public AudioReverbFilter reverb;
    }

    private readonly List<RoomAudioEffects> _roomAudioEffects = new List<RoomAudioEffects>();
    private float _targetEchoWet;
    private float _targetEchoDelayMs = 120f;
    private float _targetEchoDecay = 0.25f;
    private float _targetReverbDecay = 0.55f;
    private float _targetReverbWet;
    private float _currentEchoWet;
    private float _currentEchoDelayMs = 120f;
    private float _currentEchoDecay = 0.25f;
    private float _currentReverbDecay = 0.55f;
    private float _currentReverbWet;

    private AudioSource _wind;
    private AudioLowPassFilter _windLowPass;
    private AudioSource _ambienceA;
    private AudioSource _ambienceB;
    private AudioSource _ambienceCurrent;
    private AudioSource _ambienceOutgoing;
    private AudioSource _ambienceIncoming;
    private readonly List<AudioClip> _ambienceShuffleBag = new List<AudioClip>();
    private float _ambienceFadeElapsed;
    private float _ambienceFadeDuration;
    private float _ambienceOutgoingStartVolume;
    private bool _ambienceFading;
    private AudioClip _lastAmbienceClip;
    private bool _warnedEmptyAmbience;
    private AudioSource _roomEvents;
    private AudioClip[] _currentRoomEvents;
    private float _roomEventElapsed;
    private float _nextRoomEventTime;
    private float _currentWindMuffle;
    private AudioSource _breath;
    private AudioSource _steps;
    private AudioSource _additionalSteps;
    private readonly List<AdditionalFootstepSound> _additionalFootstepShuffleBag = new List<AdditionalFootstepSound>();
    private AdditionalFootstepSound _lastAdditionalFootstep;
    private bool _warnedMissingSteps;

    void Awake()
    {
        Instance = this;
        _wind = MakeSource("Ambient_Wind", windLoop);
        if (_wind != null)
        {
            _windLowPass = _wind.gameObject.AddComponent<AudioLowPassFilter>();
            _windLowPass.cutoffFrequency = 22000f;
        }
        _breath = MakeSource("Ambient_Breath", breathLoop);
        _steps = MakeSource("Footsteps", null);
        ConfigureFootstepSource(_steps);
        RegisterRoomAudioEffects(_steps);
        _additionalSteps = MakeSource("Additional_Footstep_Sounds", null);
        ConfigureFootstepSource(_additionalSteps);
        RegisterRoomAudioEffects(_additionalSteps);
        _ambienceA = MakeSource("Global_Ambience_A", null);
        _ambienceB = MakeSource("Global_Ambience_B", null);
        _roomEvents = MakeSource("Room_Ambient_Events", null);
        RegisterRoomAudioEffects(_roomEvents);
    }

    void Start()
    {
        StartAmbiencePlaylist();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    AudioSource MakeSource(string name, AudioClip clip)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = clip != null; // loops for wind/breath, one-shots for steps
        src.spatialBlend = 0f;   // ambient is 2D
        src.clip = clip;
        if (clip != null) src.Play();
        return src;
    }

    void Update()
    {
        if (_wind != null)
        {
            Vector3 listenerPosition = GetListenerPosition();
            float targetMuffle = IndoorShelterZone.GetAcousticMuffleAt(listenerPosition + Vector3.up * 0.35f);
            float blendSpeed = Mathf.Max(0.01f, indoorFadeSpeed);
            float blend = 1f - Mathf.Exp(-blendSpeed * Time.unscaledDeltaTime);
            _currentWindMuffle = Mathf.Lerp(_currentWindMuffle, targetMuffle, blend);

            float volumeMultiplier = Mathf.Lerp(1f, Mathf.Clamp01(indoorWindVolume), _currentWindMuffle);
            _wind.volume = windVolume * masterVolume * volumeMultiplier;

            if (_windLowPass != null)
                _windLowPass.cutoffFrequency = Mathf.Lerp(22000f, Mathf.Clamp(indoorWindCutoff, 100f, 22000f), _currentWindMuffle);
        }

        if (_breath != null) _breath.volume = breathVolume * masterVolume;
        UpdateAmbiencePlaylist();
        UpdateRoomEvents();
        UpdateRoomAcoustics();
    }

    /// <summary>
    /// Called when the player enters a committed room. This only resets the
    /// rare room-event timer/set; the global ambience playlist is not changed.
    /// </summary>
    public void OnRoomEntered(ProceduralRoomPlan plan)
    {
        SetRoomEventSet(plan);
    }

    /// <summary>
    /// Receives acoustic values calculated from the committed procedural room.
    /// The global ambience playlist is intentionally not affected by room changes.
    /// </summary>
    public void SetRoomAcoustics(float echoWet, float echoDelayMs, float echoDecayRatio,
        float reverbDecaySeconds, float reverbWet)
    {
        _targetEchoWet = Mathf.Clamp01(echoWet);
        _targetEchoDelayMs = Mathf.Clamp(echoDelayMs, 10f, 1500f);
        _targetEchoDecay = Mathf.Clamp(echoDecayRatio, 0.05f, 0.9f);
        _targetReverbDecay = Mathf.Clamp(reverbDecaySeconds, 0.1f, 5f);
        _targetReverbWet = Mathf.Clamp01(reverbWet);
    }

    void RegisterRoomAudioEffects(AudioSource source)
    {
        if (source == null) return;
        AudioEchoFilter echo = source.GetComponent<AudioEchoFilter>();
        if (echo == null) echo = source.gameObject.AddComponent<AudioEchoFilter>();
        AudioReverbFilter reverb = source.GetComponent<AudioReverbFilter>();
        if (reverb == null) reverb = source.gameObject.AddComponent<AudioReverbFilter>();
        echo.enabled = false;
        reverb.enabled = false;
        _roomAudioEffects.Add(new RoomAudioEffects { echo = echo, reverb = reverb });
    }

    void UpdateRoomAcoustics()
    {
        float blend = 1f - Mathf.Exp(-Mathf.Max(0.1f, roomAcousticsBlendSpeed) * Time.unscaledDeltaTime);
        _currentEchoWet = Mathf.Lerp(_currentEchoWet, _targetEchoWet, blend);
        _currentEchoDelayMs = Mathf.Lerp(_currentEchoDelayMs, _targetEchoDelayMs, blend);
        _currentEchoDecay = Mathf.Lerp(_currentEchoDecay, _targetEchoDecay, blend);
        _currentReverbDecay = Mathf.Lerp(_currentReverbDecay, _targetReverbDecay, blend);
        _currentReverbWet = Mathf.Lerp(_currentReverbWet, _targetReverbWet, blend);

        for (int i = 0; i < _roomAudioEffects.Count; i++)
        {
            RoomAudioEffects effects = _roomAudioEffects[i];
            if (effects == null) continue;
            ApplyRoomEffects(effects, _currentEchoWet, _currentEchoDelayMs, _currentEchoDecay,
                _currentReverbDecay, _currentReverbWet);
        }
    }

    internal static void ApplyRoomEffects(AudioSource source, float echoWet, float echoDelayMs,
        float echoDecayRatio, float reverbDecaySeconds, float reverbWet)
    {
        if (source == null) return;
        AudioEchoFilter echo = source.GetComponent<AudioEchoFilter>();
        if (echo == null) echo = source.gameObject.AddComponent<AudioEchoFilter>();
        AudioReverbFilter reverb = source.GetComponent<AudioReverbFilter>();
        if (reverb == null) reverb = source.gameObject.AddComponent<AudioReverbFilter>();
        ApplyRoomEffects(new RoomAudioEffects { echo = echo, reverb = reverb }, echoWet,
            echoDelayMs, echoDecayRatio, reverbDecaySeconds, reverbWet);
    }

    static void ApplyRoomEffects(RoomAudioEffects effects, float echoWet, float echoDelayMs,
        float echoDecayRatio, float reverbDecaySeconds, float reverbWet)
    {
        if (effects.echo != null)
        {
            effects.echo.delay = Mathf.Clamp(echoDelayMs, 10f, 1500f);
            effects.echo.decayRatio = Mathf.Clamp01(echoDecayRatio);
            effects.echo.dryMix = 1f;
            effects.echo.wetMix = Mathf.Clamp01(echoWet);
            effects.echo.enabled = echoWet > 0.005f;
        }

        if (effects.reverb != null)
        {
            float wet = Mathf.Clamp01(reverbWet);
            effects.reverb.reverbPreset = AudioReverbPreset.User;
            effects.reverb.room = Mathf.Lerp(-10000f, -1000f, wet);
            effects.reverb.roomHF = Mathf.Lerp(-10000f, -1800f, wet);
            effects.reverb.decayTime = Mathf.Clamp(reverbDecaySeconds, 0.1f, 5f);
            effects.reverb.decayHFRatio = 0.72f;
            effects.reverb.reflectionsLevel = Mathf.Lerp(-10000f, -2600f, wet);
            effects.reverb.reflectionsDelay = Mathf.Lerp(0.01f, 0.12f, wet);
            effects.reverb.reverbLevel = Mathf.Lerp(-10000f, -1700f, wet);
            effects.reverb.reverbDelay = Mathf.Lerp(0.01f, 0.08f, wet);
            effects.reverb.diffusion = 82f;
            effects.reverb.density = 88f;
            effects.reverb.hfReference = 5000f;
            effects.reverb.lfReference = 250f;
            effects.reverb.enabled = wet > 0.005f;
        }
    }

    void StartAmbiencePlaylist()
    {
        AudioClip first = TakeNextAmbienceClip();
        if (first == null)
        {
            if (!_warnedEmptyAmbience)
            {
                Debug.LogWarning("[ColdCar] Global ambience playlist is empty. Assign the imported ambience tracks to AmbientAudioController.ambiencePlaylist.", this);
                _warnedEmptyAmbience = true;
            }
            return;
        }

        _ambienceCurrent = _ambienceA;
        ConfigureAmbienceSource(_ambienceCurrent, first);
        _ambienceCurrent.Play();

        // Fade the first ambience in instead of starting it at full volume.
        _ambienceOutgoing = null;
        _ambienceIncoming = _ambienceCurrent;
        _ambienceOutgoingStartVolume = 0f;
        _ambienceFadeElapsed = 0f;
        float requestedFade = Mathf.Max(0.25f, ambienceCrossfadeSeconds);
        _ambienceFadeDuration = CountUsableAmbienceClips() > 1
            ? Mathf.Min(requestedFade, Mathf.Max(0.25f, first.length * 0.45f))
            : requestedFade;
        _ambienceFading = true;
    }

    void ConfigureAmbienceSource(AudioSource source, AudioClip clip)
    {
        if (source == null || clip == null) return;
        source.Stop();
        source.clip = clip;
        source.loop = CountUsableAmbienceClips() == 1;
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        source.dopplerLevel = 0f;
        source.pitch = 1f;
        source.volume = 0f;
        if (clip.loadState == AudioDataLoadState.Unloaded)
            clip.LoadAudioData();
    }

    void UpdateAmbiencePlaylist()
    {
        if (_ambienceFading)
        {
            UpdateAmbienceCrossfade();
            return;
        }

        if (_ambienceCurrent == null || _ambienceCurrent.clip == null)
            return;

        // A single assigned track simply loops. With multiple tracks each
        // plays once, and the next begins before the current one ends.
        if (_ambienceCurrent.loop) return;

        if (!_ambienceCurrent.isPlaying)
        {
            BeginNextAmbienceCrossfade();
            return;
        }

        float requestedFade = Mathf.Max(0.25f, ambienceCrossfadeSeconds);
        float safeFade = Mathf.Min(requestedFade, Mathf.Max(0.25f, _ambienceCurrent.clip.length * 0.45f));
        float remaining = _ambienceCurrent.clip.length - _ambienceCurrent.time;
        if (remaining <= safeFade)
            BeginNextAmbienceCrossfade();
    }

    void BeginNextAmbienceCrossfade()
    {
        AudioClip nextClip = TakeNextAmbienceClip();
        if (nextClip == null) return;

        AudioSource incoming = _ambienceCurrent == _ambienceA ? _ambienceB : _ambienceA;
        if (incoming == null) return;

        ConfigureAmbienceSource(incoming, nextClip);
        incoming.Play();

        _ambienceOutgoing = _ambienceCurrent != null && _ambienceCurrent.isPlaying ? _ambienceCurrent : null;
        _ambienceIncoming = incoming;
        _ambienceOutgoingStartVolume = _ambienceOutgoing != null ? _ambienceOutgoing.volume : 0f;
        _ambienceFadeElapsed = 0f;

        float requestedFade = Mathf.Max(0.25f, ambienceCrossfadeSeconds);
        float incomingLimit = Mathf.Max(0.25f, nextClip.length * 0.45f);
        float outgoingRemaining = _ambienceOutgoing != null && _ambienceOutgoing.clip != null
            ? Mathf.Max(0.25f, _ambienceOutgoing.clip.length - _ambienceOutgoing.time)
            : requestedFade;
        _ambienceFadeDuration = Mathf.Min(requestedFade, Mathf.Min(incomingLimit, outgoingRemaining));
        _ambienceFading = true;
    }

    void UpdateAmbienceCrossfade()
    {
        _ambienceFadeElapsed += Time.unscaledDeltaTime;
        float duration = Mathf.Max(0.05f, _ambienceFadeDuration);
        float t = Mathf.Clamp01(_ambienceFadeElapsed / duration);
        float angle = t * Mathf.PI * 0.5f;
        float targetVolume = Mathf.Clamp01(ambienceVolume * masterVolume);

        // Equal-power crossfade: the perceived loudness stays more even
        // while the two ambience tracks overlap.
        if (_ambienceOutgoing != null)
            _ambienceOutgoing.volume = _ambienceOutgoingStartVolume * Mathf.Cos(angle);
        if (_ambienceIncoming != null)
            _ambienceIncoming.volume = targetVolume * Mathf.Sin(angle);

        if (t >= 1f)
            FinishAmbienceCrossfade();
    }

    void FinishAmbienceCrossfade()
    {
        if (_ambienceOutgoing != null)
        {
            _ambienceOutgoing.Stop();
            _ambienceOutgoing.clip = null;
            _ambienceOutgoing.volume = 0f;
        }

        _ambienceCurrent = _ambienceIncoming;
        if (_ambienceCurrent != null)
            _ambienceCurrent.volume = Mathf.Clamp01(ambienceVolume * masterVolume);

        _ambienceOutgoing = null;
        _ambienceIncoming = null;
        _ambienceFadeElapsed = 0f;
        _ambienceFading = false;
    }

    AudioClip TakeNextAmbienceClip()
    {
        if (_ambienceShuffleBag.Count == 0)
            RefillAmbienceShuffleBag();
        if (_ambienceShuffleBag.Count == 0) return null;

        AudioClip picked = _ambienceShuffleBag[0];
        _ambienceShuffleBag.RemoveAt(0);
        _lastAmbienceClip = picked;
        return picked;
    }

    void RefillAmbienceShuffleBag()
    {
        _ambienceShuffleBag.Clear();
        if (ambiencePlaylist == null) return;

        // Remove nulls and duplicate references so each actual clip appears
        // only once in a shuffle cycle.
        for (int i = 0; i < ambiencePlaylist.Length; i++)
        {
            AudioClip clip = ambiencePlaylist[i];
            if (clip != null && !_ambienceShuffleBag.Contains(clip))
                _ambienceShuffleBag.Add(clip);
        }

        // Fisher-Yates shuffle.
        for (int i = _ambienceShuffleBag.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            AudioClip temp = _ambienceShuffleBag[i];
            _ambienceShuffleBag[i] = _ambienceShuffleBag[j];
            _ambienceShuffleBag[j] = temp;
        }

        // Prevent a repeat when a new shuffle cycle begins.
        if (_ambienceShuffleBag.Count > 1 && _ambienceShuffleBag[0] == _lastAmbienceClip)
        {
            int swapIndex = Random.Range(1, _ambienceShuffleBag.Count);
            AudioClip temp = _ambienceShuffleBag[0];
            _ambienceShuffleBag[0] = _ambienceShuffleBag[swapIndex];
            _ambienceShuffleBag[swapIndex] = temp;
        }
    }

    int CountUsableAmbienceClips()
    {
        if (ambiencePlaylist == null) return 0;
        int count = 0;
        for (int i = 0; i < ambiencePlaylist.Length; i++)
        {
            AudioClip clip = ambiencePlaylist[i];
            if (clip != null && !IsDuplicateAmbienceClipBefore(i, clip)) count++;
        }
        return count;
    }

    bool IsDuplicateAmbienceClipBefore(int index, AudioClip clip)
    {
        for (int i = 0; i < index; i++)
            if (ambiencePlaylist[i] == clip) return true;
        return false;
    }

    void SetRoomEventSet(ProceduralRoomPlan plan)
    {
        if (plan == null) _currentRoomEvents = indoorRoomEvents;
        else if (plan.IsCorridor) _currentRoomEvents = HasUsableClip(corridorRoomEvents) ? corridorRoomEvents : indoorRoomEvents;
        else
        {
            switch (plan.Special)
            {
                case RoomSpecial.Rooftop: _currentRoomEvents = HasUsableClip(rooftopRoomEvents) ? rooftopRoomEvents : outdoorRoomEvents; break;
                case RoomSpecial.Pool: _currentRoomEvents = HasUsableClip(poolRoomEvents) ? poolRoomEvents : indoorRoomEvents; break;
                case RoomSpecial.Stairs: _currentRoomEvents = HasUsableClip(stairsRoomEvents) ? stairsRoomEvents : indoorRoomEvents; break;
                case RoomSpecial.Metro: _currentRoomEvents = HasUsableClip(metroRoomEvents) ? metroRoomEvents : indoorRoomEvents; break;
                case RoomSpecial.Outdoor: _currentRoomEvents = HasUsableClip(outdoorRoomEvents) ? outdoorRoomEvents : indoorRoomEvents; break;
                case RoomSpecial.Forest: _currentRoomEvents = HasUsableClip(outdoorRoomEvents) ? outdoorRoomEvents : indoorRoomEvents; break;
                default: _currentRoomEvents = indoorRoomEvents; break;
            }
        }

        _roomEventElapsed = 0f;
        float min = Mathf.Max(1f, Mathf.Min(roomEventMinimumDelay, roomEventMaximumDelay));
        float max = Mathf.Max(min, Mathf.Max(roomEventMinimumDelay, roomEventMaximumDelay));
        _nextRoomEventTime = Random.Range(min, max);
    }

    void UpdateRoomEvents()
    {
        if (_currentRoomEvents == null || !HasUsableClip(_currentRoomEvents) || _roomEvents == null)
            return;

        _roomEventElapsed += Time.unscaledDeltaTime;
        if (_roomEventElapsed < _nextRoomEventTime) return;

        AudioClip clip = PickRandomUsableClip(_currentRoomEvents);
        if (clip != null && roomEventVolume > 0f)
            _roomEvents.PlayOneShot(clip, Mathf.Clamp01(roomEventVolume * masterVolume));

        _roomEventElapsed = 0f;
        float min = Mathf.Max(1f, Mathf.Min(roomEventMinimumDelay, roomEventMaximumDelay));
        float max = Mathf.Max(min, Mathf.Max(roomEventMinimumDelay, roomEventMaximumDelay));
        _nextRoomEventTime = Random.Range(min, max);
    }

    private static Vector3 GetListenerPosition()
    {
        PlayerCharacterController player = PlayerCharacterController.Instance;
        if (player != null && player.character != null)
            return player.character.position;

        Camera camera = Camera.main;
        return camera != null ? camera.transform.position : Vector3.zero;
    }

    /// <summary>Called by PlayerCharacterController every time a foot lands.</summary>
    public void PlayFootstep()
    {
        PlayFootstepForSurface(null);
    }

    /// <summary>
    /// Footstep with an optional surface class: when the class has its own clip
    /// set, it overrides the default footsteps and applies its volume multiplier.
    /// </summary>
    public void PlayFootstepForSurface(SnowTilemapData.SurfaceClass surface)
    {
        AudioClip[] set = null;
        float mul = 1f;
        if (surface != null && surface.footstepClips != null && HasUsableClip(surface.footstepClips))
        {
            set = surface.footstepClips;
            mul = surface.volumeMultiplier;
        }
        PlayFootstepForClips(set, mul);
    }

    /// <summary>
    /// Шаг с произвольным набором клипов — поверхность процедурных комнат.
    /// Пустой/неполный набор никогда не заглушает шаги: фоллбэк на общий набор.
    /// </summary>
    public void PlayFootstepForClips(AudioClip[] clips, float volumeMultiplier = 1f)
    {
        if (_steps == null) _steps = FindOrCreateFootstepSource();
        if (_steps == null) return;

        AudioClip[] set = clips != null && HasUsableClip(clips) ? clips : null;
        float mul = set != null ? volumeMultiplier : 1f;

        // A missing/partially-filled surface set must never silence footsteps:
        // fall back to the controller's general footstep collection.
        AudioClip clip = PickRandomUsableClip(set);
        if (clip == null) clip = PickRandomUsableClip(footstepClips);
        if (clip != null)
        {
            float volume = Mathf.Clamp01(footstepVolume * masterVolume * mul);
            volume *= Random.Range(0.9f, 1.05f); // шаги не звучат метрономом
            if (volume > 0.001f)
            {
                ConfigureFootstepSource(_steps);
                if (clip.loadState == AudioDataLoadState.Unloaded)
                    clip.LoadAudioData();

                _steps.pitch = Random.Range(Mathf.Min(footstepPitchMin, footstepPitchMax),
                                            Mathf.Max(footstepPitchMin, footstepPitchMax));
                _steps.PlayOneShot(clip, volume);
            }
        }
        else if (!_warnedMissingSteps)
        {
            Debug.LogWarning("[ColdCar] No usable footstep AudioClips assigned. Assign clips to the surface class or AmbientAudioController.footstepClips.", this);
            _warnedMissingSteps = true;
        }

        // Extra Foley is a separate layered source, so it can overlap the
        // ordinary footstep instead of replacing or muting it.
        TryPlayAdditionalFootstep();
    }

    void TryPlayAdditionalFootstep()
    {
        if (_additionalSteps == null || additionalFootstepSounds == null || additionalFootstepSounds.Length == 0)
            return;
        if (additionalFootstepChance <= 0f || additionalFootstepVolume <= 0f || Random.value >= additionalFootstepChance)
            return;

        AdditionalFootstepSound entry = TakeNextAdditionalFootstep();
        if (entry == null || entry.clip == null || entry.volumeMultiplier <= 0f)
            return;

        if (entry.clip.loadState == AudioDataLoadState.Unloaded)
            entry.clip.LoadAudioData();

        float volume = Mathf.Clamp01(additionalFootstepVolume * masterVolume * entry.volumeMultiplier);
        volume *= Random.Range(0.92f, 1.05f);
        if (volume <= 0.001f) return;

        ConfigureFootstepSource(_additionalSteps);
        _additionalSteps.pitch = Random.Range(Mathf.Min(footstepPitchMin, footstepPitchMax),
                                              Mathf.Max(footstepPitchMin, footstepPitchMax));
        _additionalSteps.PlayOneShot(entry.clip, volume);
    }

    AdditionalFootstepSound TakeNextAdditionalFootstep()
    {
        if (_additionalFootstepShuffleBag.Count == 0)
            RefillAdditionalFootstepBag();
        if (_additionalFootstepShuffleBag.Count == 0) return null;

        AdditionalFootstepSound picked = _additionalFootstepShuffleBag[0];
        _additionalFootstepShuffleBag.RemoveAt(0);
        _lastAdditionalFootstep = picked;
        return picked;
    }

    void RefillAdditionalFootstepBag()
    {
        _additionalFootstepShuffleBag.Clear();
        if (additionalFootstepSounds == null) return;

        for (int i = 0; i < additionalFootstepSounds.Length; i++)
        {
            AdditionalFootstepSound entry = additionalFootstepSounds[i];
            if (entry != null && entry.clip != null && entry.volumeMultiplier > 0f &&
                !_additionalFootstepShuffleBag.Contains(entry))
                _additionalFootstepShuffleBag.Add(entry);
        }

        for (int i = _additionalFootstepShuffleBag.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            AdditionalFootstepSound temp = _additionalFootstepShuffleBag[i];
            _additionalFootstepShuffleBag[i] = _additionalFootstepShuffleBag[j];
            _additionalFootstepShuffleBag[j] = temp;
        }

        // Prevent the last accent of one cycle from starting the next cycle.
        if (_additionalFootstepShuffleBag.Count > 1 &&
            _additionalFootstepShuffleBag[0] == _lastAdditionalFootstep)
        {
            int swapIndex = Random.Range(1, _additionalFootstepShuffleBag.Count);
            AdditionalFootstepSound temp = _additionalFootstepShuffleBag[0];
            _additionalFootstepShuffleBag[0] = _additionalFootstepShuffleBag[swapIndex];
            _additionalFootstepShuffleBag[swapIndex] = temp;
        }
    }

    private static bool HasUsableClip(AudioClip[] clips)
    {
        if (clips == null) return false;
        for (int i = 0; i < clips.Length; i++)
            if (clips[i] != null) return true;
        return false;
    }

    private static AudioClip PickRandomUsableClip(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0) return null;

        int validCount = 0;
        for (int i = 0; i < clips.Length; i++)
            if (clips[i] != null) validCount++;
        if (validCount == 0) return null;

        int selected = Random.Range(0, validCount);
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i] == null) continue;
            if (selected-- == 0) return clips[i];
        }
        return null;
    }

    private AudioSource FindOrCreateFootstepSource()
    {
        Transform child = transform.Find("Footsteps");
        if (child != null)
        {
            var existing = child.GetComponent<AudioSource>();
            if (existing != null)
            {
                bool registered = false;
                for (int i = 0; i < _roomAudioEffects.Count; i++)
                    if (_roomAudioEffects[i] != null && _roomAudioEffects[i].echo != null && _roomAudioEffects[i].echo.gameObject == existing.gameObject)
                        registered = true;
                if (!registered) RegisterRoomAudioEffects(existing);
                return existing;
            }
            AudioSource created = child.gameObject.AddComponent<AudioSource>();
            RegisterRoomAudioEffects(created);
            return created;
        }
        AudioSource createdSource = MakeSource("Footsteps", null);
        RegisterRoomAudioEffects(createdSource);
        return createdSource;
    }

    private static void ConfigureFootstepSource(AudioSource source)
    {
        if (source == null) return;
        source.enabled = true;
        source.playOnAwake = false;
        source.loop = false;
        source.mute = false;
        source.volume = 1f;
        source.spatialBlend = 0f; // player footsteps are intentionally non-spatial
        source.dopplerLevel = 0f;
    }
}
