using UnityEngine;

/// <summary>
/// A hinged portal into a room whose layout is already planned but whose meshes
/// are created only after the player opens this door.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class OneWayProceduralDoor : MonoBehaviour
{
    public bool IsLocked { get; private set; }
    public bool IsOpen { get; private set; }
    public ProceduralRoomPlan DestinationPlan { get; private set; }
    public Vector3 OpeningCenterWorld { get; private set; }
    public Vector3 OutwardNormal { get; private set; }
    public Transform LeafPivot { get; private set; }
    /// <summary>Внутренняя дверь уровня: просто открывается, без генерации следующей комнаты.</summary>
    public bool InteriorDoor { get; set; }

    ProceduralRoomManager _manager;
    Quaternion _closedRotation = Quaternion.identity;
    Quaternion _openRotation = Quaternion.Euler(0f, -90f, 0f);
    bool _animate;
    bool _targetOpen;
    const float OpenAnimationSpeed = 8.5f;
    const float CloseAnimationSpeed = 24f;
    Collider _leafCollider;
    Collider _passageBlocker;

    AudioSource _doorAudio;
    AudioClip[] _openClips = new AudioClip[0];
    AudioClip[] _closeClips = new AudioClip[0];
    AudioClip _legacyOpenClip;
    AudioClip _legacyCloseClip;
    readonly System.Collections.Generic.List<AudioClip> _openShuffleBag = new System.Collections.Generic.List<AudioClip>();
    readonly System.Collections.Generic.List<AudioClip> _closeShuffleBag = new System.Collections.Generic.List<AudioClip>();
    int _openBagIndex;
    int _closeBagIndex;
    AudioClip _lastOpenClip;
    AudioClip _lastCloseClip;
    float _doorVolumeMin = 0.66f;
    float _doorVolumeMax = 0.78f;
    float _pitchMin = 0.96f;
    float _pitchMax = 1.04f;
    float _echoWet;
    float _echoDelayMs = 120f;
    float _echoDecay = 0.22f;
    float _reverbDecay = 0.35f;
    float _reverbWet;

    public void Initialize(
        ProceduralRoomManager manager,
        ProceduralRoomPlan destinationPlan,
        Transform leafPivot,
        Vector3 openingCenterWorld,
        Vector3 outwardNormal)
    {
        _manager = manager;
        DestinationPlan = destinationPlan;
        LeafPivot = leafPivot;
        OpeningCenterWorld = openingCenterWorld;
        OutwardNormal = outwardNormal.normalized;
        _closedRotation = leafPivot != null ? leafPivot.localRotation : Quaternion.identity;
        _openRotation = _closedRotation * Quaternion.Euler(0f, -90f, 0f);
        if (leafPivot != null) _leafCollider = leafPivot.GetComponentInChildren<Collider>();
    }

    public void ConfigureAudio(AudioClip[] openClips, AudioClip[] closeClips,
        AudioClip legacyOpenClip, AudioClip legacyCloseClip,
        float volumeMin, float volumeMax, float pitchMin, float pitchMax,
        float spatialBlend, float maxDistance,
        float echoWet, float echoDelayMs, float echoDecayRatio,
        float reverbDecaySeconds, float reverbWet)
    {
        _openClips = SanitizeClipPool(openClips);
        _closeClips = SanitizeClipPool(closeClips);
        _legacyOpenClip = legacyOpenClip;
        _legacyCloseClip = legacyCloseClip;
        _doorVolumeMin = Mathf.Clamp01(Mathf.Min(volumeMin, volumeMax));
        _doorVolumeMax = Mathf.Clamp01(Mathf.Max(volumeMin, volumeMax));
        _pitchMin = Mathf.Min(pitchMin, pitchMax);
        _pitchMax = Mathf.Max(pitchMin, pitchMax);
        _echoWet = Mathf.Clamp01(echoWet);
        _echoDelayMs = Mathf.Clamp(echoDelayMs, 10f, 1500f);
        _echoDecay = Mathf.Clamp01(echoDecayRatio);
        _reverbDecay = Mathf.Clamp(reverbDecaySeconds, 0.1f, 5f);
        _reverbWet = Mathf.Clamp01(reverbWet);

        // При перенастройке сбрасываем циклы перемешивания, чтобы новые списки
        // применились сразу и каждый пул работал независимо.
        _openShuffleBag.Clear();
        _closeShuffleBag.Clear();
        _openBagIndex = 0;
        _closeBagIndex = 0;
        _lastOpenClip = null;
        _lastCloseClip = null;

        AudioSource audioSource = EnsureAudioSource();
        if (audioSource == null)
        {
            Debug.LogError($"[{nameof(OneWayProceduralDoor)}] Could not create an AudioSource for door '{name}'. Door animation will still work; door sounds are disabled.", this);
            return;
        }

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = Mathf.Clamp01(spatialBlend);
        audioSource.dopplerLevel = 0f;
        audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        audioSource.minDistance = 1.8f;
        audioSource.maxDistance = Mathf.Max(2f, maxDistance);
        ApplyDoorRoomEffects();
    }

    // Совместимость с вызовами старого API: одиночные клипы становятся
    // одноэлементными пулами, а громкость остаётся фиксированной.
    public void ConfigureAudio(AudioClip openClip, AudioClip closeClip, float volume,
        float pitchMin, float pitchMax, float spatialBlend, float maxDistance,
        float echoWet, float echoDelayMs, float echoDecayRatio,
        float reverbDecaySeconds, float reverbWet)
    {
        ConfigureAudio(null, null, openClip, closeClip, volume, volume,
            pitchMin, pitchMax, spatialBlend, maxDistance,
            echoWet, echoDelayMs, echoDecayRatio, reverbDecaySeconds, reverbWet);
    }

    static AudioClip[] SanitizeClipPool(AudioClip[] source)
    {
        if (source == null || source.Length == 0) return new AudioClip[0];
        var unique = new System.Collections.Generic.List<AudioClip>(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            AudioClip clip = source[i];
            if (clip == null || unique.Contains(clip)) continue;
            unique.Add(clip);
        }
        return unique.ToArray();
    }

    AudioSource EnsureAudioSource()
    {
        // Use Unity's overloaded null check (not just CLR null) so destroyed
        // component wrappers are not accidentally reused.
        if (_doorAudio != null) return _doorAudio;

        AudioSource existing = GetComponent<AudioSource>();
        if (existing != null)
        {
            _doorAudio = existing;
            return _doorAudio;
        }

        _doorAudio = gameObject.AddComponent<AudioSource>();
        if (_doorAudio != null) return _doorAudio;

        // Last-resort fallback: keep audio on a dedicated child object. This
        // also avoids coupling the source to the generated door assembly.
        GameObject audioObject = new GameObject("Door Audio Source");
        audioObject.transform.SetParent(transform, false);
        _doorAudio = audioObject.AddComponent<AudioSource>();
        return _doorAudio;
    }

    void ApplyDoorRoomEffects()
    {
        if (_doorAudio == null) return;
        AudioEchoFilter echo = _doorAudio.GetComponent<AudioEchoFilter>();
        if (echo == null) echo = _doorAudio.gameObject.AddComponent<AudioEchoFilter>();
        echo.delay = _echoDelayMs;
        echo.decayRatio = _echoDecay;
        echo.dryMix = 1f;
        echo.wetMix = _echoWet;
        echo.enabled = _echoWet > 0.005f;

        AudioReverbFilter reverb = _doorAudio.GetComponent<AudioReverbFilter>();
        if (reverb == null) reverb = _doorAudio.gameObject.AddComponent<AudioReverbFilter>();
        reverb.reverbPreset = AudioReverbPreset.User;
        reverb.room = Mathf.Lerp(-10000f, -1000f, _reverbWet);
        reverb.roomHF = Mathf.Lerp(-10000f, -1800f, _reverbWet);
        reverb.decayTime = _reverbDecay;
        reverb.decayHFRatio = 0.72f;
        reverb.reflectionsLevel = Mathf.Lerp(-10000f, -2600f, _reverbWet);
        reverb.reflectionsDelay = Mathf.Lerp(0.01f, 0.12f, _reverbWet);
        reverb.reverbLevel = Mathf.Lerp(-10000f, -1700f, _reverbWet);
        reverb.reverbDelay = Mathf.Lerp(0.01f, 0.08f, _reverbWet);
        reverb.diffusion = 82f;
        reverb.density = 88f;
        reverb.hfReference = 5000f;
        reverb.lfReference = 250f;
        reverb.enabled = _reverbWet > 0.005f;
    }

    void PlayDoorSound(bool opening)
    {
        if (_doorAudio == null) return;

        AudioClip clip = null;
        // Процедурные двери создаются на ходу, поэтому выбор клипа делегируется
        // менеджеру: shuffle-bag общий для всей экспедиции, а не сбрасывается
        // при создании каждой новой двери.
        if (_manager != null)
            clip = opening ? _manager.GetNextDoorOpenClip() : _manager.GetNextDoorCloseClip();

        // Резерв для двери без менеджера (например, отдельно созданной в сцене).
        if (clip == null)
        {
            clip = opening
                ? GetNextPoolClip(_openClips, _openShuffleBag, ref _openBagIndex, ref _lastOpenClip, _legacyOpenClip)
                : GetNextPoolClip(_closeClips, _closeShuffleBag, ref _closeBagIndex, ref _lastCloseClip, _legacyCloseClip);
        }
        if (clip == null) return;

        _doorAudio.pitch = Random.Range(_pitchMin, _pitchMax);
        float volume = Random.Range(_doorVolumeMin, _doorVolumeMax);
        if (volume > 0.001f)
            _doorAudio.PlayOneShot(clip, volume);
    }

    AudioClip GetNextPoolClip(AudioClip[] pool,
        System.Collections.Generic.List<AudioClip> shuffleBag,
        ref int bagIndex, ref AudioClip lastClip, AudioClip legacyFallback)
    {
        bool hasPool = pool != null && pool.Length > 0;
        if (!hasPool)
        {
            // Старые сцены с одиночным клипом продолжают работать.
            return legacyFallback;
        }

        if (shuffleBag.Count == 0 || bagIndex >= shuffleBag.Count)
        {
            shuffleBag.Clear();
            shuffleBag.AddRange(pool);
            // Fisher-Yates shuffle.
            for (int i = shuffleBag.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                AudioClip temp = shuffleBag[i];
                shuffleBag[i] = shuffleBag[j];
                shuffleBag[j] = temp;
            }

            // Не повторять последний звук предыдущего круга первым звуком нового.
            if (shuffleBag.Count > 1 && shuffleBag[0] == lastClip)
            {
                int swapIndex = Random.Range(1, shuffleBag.Count);
                AudioClip temp = shuffleBag[0];
                shuffleBag[0] = shuffleBag[swapIndex];
                shuffleBag[swapIndex] = temp;
            }
            bagIndex = 0;
        }

        if (bagIndex >= shuffleBag.Count) return null;
        AudioClip selected = shuffleBag[bagIndex++];
        lastClip = selected;
        return selected;
    }

    void Update()
    {
        if (LeafPivot == null) return;

        if (_animate)
        {
            Quaternion target = _targetOpen ? _openRotation : _closedRotation;
            float speed = _targetOpen ? OpenAnimationSpeed : CloseAnimationSpeed;
            LeafPivot.localRotation = Quaternion.Slerp(
                LeafPivot.localRotation, target, 1f - Mathf.Exp(-speed * Time.deltaTime));

            bool reached = Quaternion.Angle(LeafPivot.localRotation, target) < 0.6f;
            if (reached)
            {
                LeafPivot.localRotation = target;
                _animate = false;
            }
        }

        // The moving leaf is decorative while the doorway is traversable. Its
        // collider is disabled on opening, so neither its swing nor its fast
        // close can physically shove the player backwards. A separate static
        // blocker is enabled only after the manager confirms the whole capsule
        // has cleared the opening.
        if (_targetOpen && _leafCollider != null && _leafCollider.enabled)
            _leafCollider.enabled = false;
    }

    public void TryInteract()
    {
        if (IsLocked || _animate || IsOpen) return;
        // Внутренняя дверь не порождает комнату — просто распахивается.
        if (InteriorDoor) { Open(); return; }
        if (_manager == null) return;
        _manager.TryOpenDoor(this);
    }

    /// <summary>Assigns the invisible fixed blocker for the one-way doorway.</summary>
    public void SetPassageBlocker(Collider blocker)
    {
        _passageBlocker = blocker;
        if (_passageBlocker != null) _passageBlocker.enabled = false;
    }

    public void Open()
    {
        if (IsLocked || IsOpen) return;
        IsOpen = true;
        _targetOpen = true;
        _animate = true;
        // Door leaf has no physical collision while it swings out of the way.
        if (_leafCollider != null) _leafCollider.enabled = false;
        if (_passageBlocker != null) _passageBlocker.enabled = false;
        PlayDoorSound(true);
    }

    /// <summary>Moves the door into the destination and locks the passage only
    /// after the manager has verified that the entire player capsule is clear.</summary>
    public void CommitAsOneWayEntrance(Transform newRoomRoot)
    {
        bool wasOpen = IsOpen;
        if (wasOpen) PlayDoorSound(false);
        if (newRoomRoot != null) transform.SetParent(newRoomRoot, true);

        IsLocked = true;
        IsOpen = false;
        _targetOpen = false;
        _animate = true;

        // Lock the doorway immediately with a stationary collider. The player
        // is already at least one capsule radius + clearance beyond the plane,
        // so enabling this cannot overlap or push them. The leaf itself remains
        // non-solid while closing quickly behind the player.
        if (_leafCollider != null) _leafCollider.enabled = false;
        if (_passageBlocker != null) _passageBlocker.enabled = true;
    }
}
