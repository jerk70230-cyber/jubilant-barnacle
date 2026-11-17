using UnityEngine;

/// <summary>
/// Последовательность лифтовой комнаты: игрок вошёл → входная дверь закрылась
/// → створки лифта закрываются → «поездка» (кабина стоит на месте, звучат
/// старт/езда/конец и музыка) → створки открываются, открывая дверь в
/// следующую комнату.
/// Звуки назначаются в ProceduralRoomManager, секция «Звуки лифта».
/// Не назначенный звук молчит; остальные продолжают работать.
/// </summary>
[DisallowMultipleComponent]
public sealed class ElevatorRoomController : MonoBehaviour
{
    enum Phase { Idle, Waiting, DoorsClosing, Ride, DoorsOpening, Done }

    ProceduralRoomManager _manager;

    [Header("Тайминги")]
    [Tooltip("Пауза после закрытия входной двери до закрытия створок (сек).")]
    [Min(0.2f)] public float waitBeforeDoors = 1.2f;
    [Tooltip("Движение створок (сек).")]
    [Range(0.4f, 2f)] public float doorsMoveTime = 0.9f;
    [Tooltip("Длительность «поездки» (сек).")]
    [Range(3f, 12f)] public float rideDuration = 6.5f;

    Transform _panelL, _panelR;
    Vector3 _closedL, _closedR, _openL, _openR;
    AudioSource _oneshot;
    AudioSource _rideLoop;
    AudioSource _music;
    Phase _phase = Phase.Idle;
    float _timer;

    public void Setup(Transform panelL, Transform panelR,
        Vector3 closedL, Vector3 closedR, Vector3 openL, Vector3 openR,
        Vector3 slideAxis, int seed, ProceduralRoomManager manager)
    {
        _panelL = panelL; _panelR = panelR;
        _closedL = closedL; _closedR = closedR;
        _openL = openL; _openR = openR;
        _manager = manager;
    }

    /// <summary>Запускает последовательность (вызывается при входе игрока в кабину).</summary>
    public void BeginSequence()
    {
        if (_phase != Phase.Idle) return;
        _phase = Phase.Waiting;
        _timer = waitBeforeDoors;

        _oneshot = gameObject.AddComponent<AudioSource>();
        _oneshot.playOnAwake = false;
        _oneshot.spatialBlend = 0f; // игрок внутри кабины — звук как «собственный»
        _rideLoop = gameObject.AddComponent<AudioSource>();
        _rideLoop.playOnAwake = false;
        _rideLoop.loop = true;
        _rideLoop.spatialBlend = 0f;
        _music = gameObject.AddComponent<AudioSource>();
        _music.playOnAwake = false;
        _music.loop = true;
        _music.spatialBlend = 0f;
    }

    void Update()
    {
        if (_phase == Phase.Waiting)
        {
            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                _phase = Phase.DoorsClosing;
                _timer = doorsMoveTime;
                PlayOneShot(ElevatorDoorsCloseClip(), ElevatorDoorsVolume());
            }
        }
        else if (_phase == Phase.DoorsClosing)
        {
            _timer -= Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - _timer / doorsMoveTime));
            SetPanels(t);
            if (_timer <= 0f)
            {
                SetPanels(1f);
                _phase = Phase.Ride;
                _timer = rideDuration;
                PlayOneShot(ElevatorRideStartClip(), ElevatorRideVolume());
                StartLoop(_rideLoop, ElevatorRideLoopClip(), ElevatorRideVolume());
                StartLoop(_music, ElevatorMusicClip(), ElevatorMusicVolume());
            }
        }
        else if (_phase == Phase.Ride)
        {
            _timer -= Time.deltaTime;
            // Кабина стоит на месте — «езда» передаётся только звуком.
            if (_timer <= 0f)
            {
                _phase = Phase.DoorsOpening;
                _timer = doorsMoveTime;
                PlayOneShot(ElevatorRideEndClip(), ElevatorRideVolume());
                StopLoop(_rideLoop);
                StopLoop(_music);
            }
        }
        else if (_phase == Phase.DoorsOpening)
        {
            _timer -= Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - _timer / doorsMoveTime));
            SetPanels(1f - t);
            if (_timer <= 0f)
            {
                SetPanels(0f);
                _phase = Phase.Done;
            }
        }
    }

    void SetPanels(float t) // t: 0 = открыты, 1 = закрыты (локальные координаты комнаты)
    {
        if (_panelL != null) _panelL.localPosition = Vector3.Lerp(_openL, _closedL, t);
        if (_panelR != null) _panelR.localPosition = Vector3.Lerp(_openR, _closedR, t);
    }

    void PlayOneShot(AudioClip clip, float volume)
    {
        if (clip == null || _oneshot == null) return;
        _oneshot.PlayOneShot(clip, volume);
    }

    void StartLoop(AudioSource source, AudioClip clip, float volume)
    {
        if (source == null || clip == null) return;
        source.clip = clip;
        source.volume = volume;
        source.Play();
    }

    void StopLoop(AudioSource source)
    {
        if (source != null) source.Stop();
    }

    // ---- чтение звуков/громкостей из менеджера (безопасно при отсутствии) ----

    AudioClip ElevatorDoorsCloseClip() => _manager != null ? _manager.elevatorDoorsClose : null;
    AudioClip ElevatorDoorsOpenClip() => _manager != null ? _manager.elevatorDoorsOpen : null;
    AudioClip ElevatorRideStartClip() => _manager != null ? _manager.elevatorRideStart : null;
    AudioClip ElevatorRideEndClip() => _manager != null ? _manager.elevatorRideEnd : null;
    AudioClip ElevatorRideLoopClip() => _manager != null ? _manager.elevatorRideLoop : null;
    AudioClip ElevatorMusicClip() => _manager != null ? _manager.elevatorMusic : null;
    float ElevatorDoorsVolume() => _manager != null ? _manager.elevatorDoorsVolume : 0.9f;
    float ElevatorRideVolume() => _manager != null ? _manager.elevatorRideVolume : 0.7f;
    float ElevatorMusicVolume() => _manager != null ? _manager.elevatorMusicVolume : 0.45f;
}
