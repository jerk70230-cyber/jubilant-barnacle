using System;
using System.Collections.Generic;
using UnityEngine;

public enum ProceduralRoomSide
{
    North, // +Z
    East,  // +X
    South, // -Z
    West   // -X
}

[Serializable]
public sealed class ProceduralDoorSocket
{
    public ProceduralRoomSide Side;
    public float Along;

    public ProceduralDoorSocket(ProceduralRoomSide side, float along)
    {
        Side = side;
        Along = along;
    }
}

/// <summary>
/// Lightweight room description. It contains dimensions, texture choices and
/// door sockets only; it does not allocate Unity objects or meshes.
/// Each unopened door already owns one of these plans.
/// </summary>
/// <summary>
/// Поверхность процедурных комнат: набор текстур + звуки шагов для них.
/// Текстура пола под ногами игрока матчится со списком textures.
/// </summary>
[Serializable]
public sealed class RoomFootstepSurface
{
    [Tooltip("Название для удобства (на работу не влияет).")]
    public string name = "Поверхность";
    [Tooltip("Текстуры этой поверхности: если текстура пола ПОД НОГАМИ совпадает с одной из них, играются звуки этого набора.")]
    public Texture2D[] textures;
    [Tooltip("Звуки шагов этой поверхности при ХОДЬБЕ (случайный выбор).")]
    public AudioClip[] footstepClips;
    [Tooltip("Звуки при БЕГЕ по этой поверхности (если пусто — используются обычные).")]
    public AudioClip[] footstepRunClips;
    [Tooltip("Редкое «шарканье» для естественности (шанс задаётся в контроллере).")]
    public AudioClip[] scuffClips;
    [Tooltip("Громкость шагов этой поверхности.")]
    [Range(0f, 1f)] public float volumeMultiplier = 1f;
}

/// <summary>Особые типы комнат: меняют пол/потолок/стены и добавляют контент.</summary>
public enum RoomSpecial
{
    None,
    Rooftop,   // крыша: без потолка, низкий парапет по периметру, ящики-вентустановки
    Pool,      // зал с бассейном случайного размера
    Stairs,    // лестница-ступени на платформу внутри зала
    Metro,     // платформа метро: яма с рельсами вдоль одной стены
    Outdoor,   // пустынное пространство либо луг с травой: без потолка, вал земляной
    Forest,    // большая лесная локация: неровная земля, деревья, кусты и грибы
    Elevator   // тесная кабина лифта: двери закрываются, «поездка» со звуками, створки открываются в новую комнату
}


public enum ProceduralAcousticMaterial
{
    Auto,
    Concrete,
    Brick,
    Stone,
    Tile,
    Metal,
    Glass,
    Wood,
    Plaster,
    Carpet,
    Fabric,
    Grass,
    Sand,
    Soil,
    Snow,
    Rubber
}

[Serializable]
public sealed class RoomAcousticTextureOverride
{
    [Tooltip("Точная текстура из Floor/Wall/Ceiling Textures.")]
    public Texture2D texture;
    [Tooltip("Материал для расчёта отражений. Auto использует имя текстуры.")]
    public ProceduralAcousticMaterial material = ProceduralAcousticMaterial.Auto;
}

[Serializable]
public sealed class ProceduralRoomPlan
{
    public RoomSpecial Special;
    public int Seed;
    public float Width;
    public float Depth;
    public float WallHeight;
    public bool HasIncomingSide;
    public ProceduralRoomSide IncomingSide;
    public Texture2D FloorTexture;
    public Texture2D CeilingTexture;
    public Texture2D WallTexture;
    public bool IsCorridor;
    public bool HasPartitions;
    public bool HasClosetRoom;
    public float ClosetDepth;
    public readonly List<ProceduralDoorSocket> Exits = new List<ProceduralDoorSocket>();
}

public sealed class ProceduralRoomInstance
{
    public GameObject Root;
    public ProceduralRoomPlan Plan;
    public Vector3 Center;
    /// <summary>Прямоугольник бассейна (x0, z0, x1, z1) — заполняется BuildPoolFloors.</summary>
    public Vector4 PoolRect;
    /// <summary>Temporary door-sized portal objects used while entering the next room.</summary>
    public GameObject PortalWindow;
    public RenderTexture PortalRT;
    public GameObject PortalCamera;
    public Material PortalMaterial;
    public Camera PortalMainCamera;
    public int PortalOriginalCullingMask;
    public bool PortalCullingMaskSaved;
    public readonly List<Renderer> Renderers = new List<Renderer>();
    public readonly Dictionary<GameObject, int> OriginalRendererLayers = new Dictionary<GameObject, int>();
    public readonly Dictionary<Renderer, bool> OriginalRendererEnabled = new Dictionary<Renderer, bool>();
    public readonly List<GameObject> PortalVisualProxies = new List<GameObject>();
    public readonly List<BoxCollider> WallColliders = new List<BoxCollider>();
    public readonly List<Mesh> RuntimeMeshes = new List<Mesh>();
    // TerrainData is a Unity native resource and must be released with its room.
    public readonly List<TerrainData> RuntimeTerrainData = new List<TerrainData>();
    public BoxCollider ForestEntranceLanding;
    public readonly List<OneWayProceduralDoor> Doors = new List<OneWayProceduralDoor>();
}

/// <summary>
/// Endless one-way room chain. Room layouts are planned when the containing
/// room is built, while destination geometry and colliders are instantiated
/// only when the matching door is opened. Only current + pending rooms coexist.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class ProceduralRoomManager : MonoBehaviour
{
    [Header("Scene references")]
    public Transform worldRoot;
    public Transform wallOcclusionRoot;
    public Transform playerRoot;
    public ProceduralFirstPersonController playerController;
    public Camera playerCamera;
    [Tooltip("Lit shader used only by generated procedural floors, walls and doors.")]
    public Shader proceduralSurfaceShader;
    [Tooltip("Генератор лесной локации: списки префабов, плотность растительности, неровность земли и оптимизация.")]
    public ForestRoomGenerator forestGenerator;

    [Header("Room textures")]
    [Tooltip("Текстуры ПОЛА. Первая в списке — основная (доски): она используется на полу всегда, пока не включён Floor Variety. Остальные — запас на будущее.")]
    public Texture2D[] floorTextures;
    [Tooltip("Текстуры ПОТОЛКА. Если пусто — используется текстура стены (снег на потолок больше не попадает).")]
    public Texture2D[] ceilingTextures;
    public Texture2D[] wallTextures;
    [Tooltip("Вкл = пол выбирается случайно из всех Floor Textures. Выкл (рекомендую сейчас) = пол всегда первая текстура (доски).")]
    public bool floorVariety = false;
    [Tooltip("Масштаб тайлинга текстур: сколько метров занимает один тайл на полу/стенах/ступенях. Меньше = текстура мельче и чаще, больше = крупнее. Крути, если текстуры выглядят растянутыми или мелкими.")]
    [Range(0.5f, 5f)] public float textureMetersPerTile = 2f;

    [Header("Textures of special rooms (пусто = цветной запасный вариант)")]
    [Tooltip("КРЫША: рубероид/тёмный гравий/бетон — пол и парапет.")]
    public Texture2D[] roofTextures;
    [Tooltip("БАССЕЙН: кафельная плитка — стенки чаши, бортик.")]
    public Texture2D[] poolTextures;
    [Tooltip("МЕТРО: бетон станции/плитка — платформа, стены ямы, колонны.")]
    public Texture2D[] metroTextures;
    [Tooltip("ЛУГ: трава (плоский пол).")]
    public Texture2D[] meadowTextures;
    [Tooltip("ПУСТЫНЯ: песок (плоский пол).")]
    public Texture2D[] desertTextures;

    static Texture2D FirstOf(Texture2D[] arr) => arr != null && arr.Length > 0 ? arr[0] : null;

    [Header("Procedural dimensions")]
    [Min(5f)] public float minimumRoomSize = 7f;
    [Min(5f)] public float maximumRoomSize = 13f;
    [Min(2f)] public float wallHeight = 3.2f;
    [Min(0.08f)] public float wallThickness = 0.22f;
    [Range(0.8f, 1.8f)] public float doorWidth = 1.2f;
    [Range(1.8f, 2.6f)] public float doorHeight = 2.15f;
    [Range(1, 3)] public int minimumDoorsPerRoom = 1;
    [Tooltip("Максимум дверей в комнате: 1–3.")]
    [Range(1, 3)] public int maximumDoorsPerRoom = 3;
    public int randomSeed;

    [Header("Room echo / reverb")]
    [Tooltip("Включает процедурное эхо для шагов, дверей и локальных звуков внутри комнат. На крыше и открытых пространствах отключается полностью.")]
    public bool enableProceduralRoomEcho = true;
    [Tooltip("Общая интенсивность эха. 1 = расчётные значения без усиления/ослабления.")]
    [Range(0f, 2f)] public float roomEchoIntensity = 1f;
    [Tooltip("Приблизительный объём комнаты, начиная с которого эхо заметно усиливается.")]
    [Min(20f)] public float roomEchoSmallVolume = 130f;
    [Tooltip("Объём большой комнаты, после которого размер почти не усиливает эхо.")]
    [Min(50f)] public float roomEchoLargeVolume = 560f;
    [Tooltip("Максимальная доля повторного сигнала. Держи ниже 0.45, чтобы не получить неприятное дребезжащее эхо.")]
    [Range(0f, 0.6f)] public float roomEchoMaximumWet = 0.38f;
    [Tooltip("Необязательные точные переопределения акустики для текстур. Если пусто, материал определяется по имени файла.")]
    public RoomAcousticTextureOverride[] roomAcousticTextureOverrides = new RoomAcousticTextureOverride[0];

    [Header("Door sounds — random pools")]
    [Tooltip("Пул звуков ОТКРЫТИЯ. На каждое открытие берётся случайный клип. Клип не повторяется, пока не прозвучат остальные из этого списка.")]
    public AudioClip[] doorOpenClips = new AudioClip[0];
    [Tooltip("Пул звуков ЗАКРЫТИЯ. Независим от списка открытия; также без повторов до полного прохода пула.")]
    public AudioClip[] doorCloseClips = new AudioClip[0];
    [Tooltip("Минимальная громкость случайного воспроизведения.")]
    [Range(0f, 1f)] public float doorVolumeMin = 0.66f;
    [Tooltip("Максимальная громкость случайного воспроизведения.")]
    [Range(0f, 1f)] public float doorVolumeMax = 0.78f;
    [Tooltip("Случайный диапазон высоты тона для каждого воспроизведения.")]
    [Range(0.8f, 1.2f)] public float doorPitchMin = 0.96f;
    [Range(0.8f, 1.2f)] public float doorPitchMax = 1.04f;
    [Range(0f, 1f)] public float doorSpatialBlend = 0.85f;
    [Min(2f)] public float doorSoundMaxDistance = 16f;

    // Старые одиночные поля оставлены скрытыми для сохранения сериализованных
    // ссылок из предыдущей версии сцены. Используются как резерв, если пул пуст.
    [HideInInspector] public AudioClip doorOpenClip;
    [HideInInspector] public AudioClip doorCloseClip;
    [HideInInspector, Range(0f, 1f)] public float doorSoundVolume = 0.72f;

    [Header("Звуки лифта (кабина лифта — назначь клипы сюда)")]
    [Tooltip("Створки лифта закрываются.")]
    public AudioClip elevatorDoorsClose;
    [Tooltip("Створки лифта открываются.")]
    public AudioClip elevatorDoorsOpen;
    [Tooltip("Старт движения лифта.")]
    public AudioClip elevatorRideStart;
    [Tooltip("Остановка лифта.")]
    public AudioClip elevatorRideEnd;
    [Tooltip("Езда лифта (зацикленный гул/шум).")]
    public AudioClip elevatorRideLoop;
    [Tooltip("Музыка в лифте (зацикленная).")]
    public AudioClip elevatorMusic;
    [Range(0f, 1f)] public float elevatorDoorsVolume = 0.9f;
    [Range(0f, 1f)] public float elevatorRideVolume = 0.7f;
    [Range(0f, 1f)] public float elevatorMusicVolume = 0.45f;

    [Header("Звуки шагов (текстура под ногами → звуки)")]
    [Tooltip("Список поверхностей: например «Доски» (floor.png), «Кафель» (kafel), «Рубероид» (ruberoid), «Трава» (grass), «Песок» (sand). Если под ногами ни одна не совпала — играется обычный набор из AmbientAudioController.")]
    public List<RoomFootstepSurface> footstepSurfaces = new List<RoomFootstepSurface>();

    [Header("Save room")]
    public Vector3 saveRoomPlayerSpawn = new Vector3(0f, 0.04f, -1.35f);
    public bool buildOnPlay = true;
    [Tooltip("Строить стартовую сейв-комнату прямо в редакторе (не в Play), чтобы её было видно в Game-виде и можно было декорировать. Мир пересобирается заново при входе в Play.")]
    public bool buildInEditMode = true;

    ProceduralRoomInstance _activeRoom;
    ProceduralRoomInstance _pendingRoom;
    OneWayProceduralDoor _pendingDoor;
    Vector3 _pendingLastPlayerPosition;
    bool _pendingPlayerSampleValid;
    // We remember the actual forward plane crossing, but do not unload the old
    // room until the complete CharacterController capsule has cleared the door.
    bool _pendingDoorPlaneCrossed;
    bool _pendingRoomRevealedToMainCamera;
    float _pendingGroundedClearTime;
    bool _pendingSupportWarningLogged;
    static readonly Vector3[] GroundSupportProbeOffsets =
    {
        Vector3.zero, Vector3.right, Vector3.left, Vector3.forward, Vector3.back
    };
    static readonly RaycastHit[] GroundSupportProbeHits = new RaycastHit[64];
    System.Random _random;
    int _nextRoomNumber;

    // Shared shuffle bags live on the manager rather than individual doors:
    // procedural doors are spawned/destroyed frequently, but the sound pools
    // must avoid immediate repeats across the entire expedition.
    readonly List<AudioClip> _doorOpenShuffleBag = new List<AudioClip>();
    readonly List<AudioClip> _doorCloseShuffleBag = new List<AudioClip>();
    int _doorOpenBagIndex;
    int _doorCloseBagIndex;
    AudioClip _lastDoorOpenClip;
    AudioClip _lastDoorCloseClip;

    readonly List<Material> _ownedMaterials = new List<Material>();
    readonly Dictionary<Texture2D, Material> _floorMaterialCache = new Dictionary<Texture2D, Material>();
    readonly Dictionary<Texture2D, Material> _wallMaterialCache = new Dictionary<Texture2D, Material>();
    Material _doorMaterial;
    Material _doorTrimMaterial;
    readonly Dictionary<Texture2D, Material> _ceilingMaterialCache = new Dictionary<Texture2D, Material>();
    Light _generatedDaySun;

    public int ActiveRoomDoorCount => _activeRoom != null ? _activeRoom.Doors.Count : 0;
    public int ActiveRoomSeed => _activeRoom != null && _activeRoom.Plan != null ? _activeRoom.Plan.Seed : 0;
    public bool HasPendingTransition => _pendingDoor != null;

    void Awake()
    {
        // Случайный сид — только в Play: в редакторе он должен оставаться
        // каким задан в инспекторе (детерминированный мир для декорирования).
        if (Application.isPlaying && randomSeed == 0) randomSeed = UnityEngine.Random.Range(1, int.MaxValue);
        _random = new System.Random(randomSeed);
        EnsureRoots();
    }

    void Start()
    {
        if (!buildOnPlay) return;
        ResolveReferences();
        // Комната, построенная в редакторе, попала в сцену — сносим её и
        // строим свежую, чтобы не задваивать мир.
        CleanupGeneratedRooms();
        _activeRoom = BuildSaveRoom();
        if (playerRoot != null)
        {
            playerRoot.position = saveRoomPlayerSpawn;
            playerRoot.rotation = Quaternion.identity;
        }
        ApplyRoomAtmosphere(_activeRoom.Plan);
    }

    /// <summary>Активна ли сейчас «дневная» комната (крыша/открытое пространство) — для скайбокс-логики.</summary>
    public static bool OutdoorRoomActive { get; private set; }

    Material _daySkybox;

    /// <summary>
    /// Каждая комната — отдельный «уровень» со своей атмосферой: крыша и
    /// открытые пространства — ДЕНЬ (небо, солнце, светлый туман), остальные —
    /// ночь. Вызывается при входе игрока в комнату.
    /// </summary>
    void CalculateRoomEcho(ProceduralRoomPlan plan, out float echoWet, out float echoDelayMs,
        out float echoDecayRatio, out float reverbDecaySeconds, out float reverbWet)
    {
        echoWet = 0f;
        echoDelayMs = 120f;
        echoDecayRatio = 0.22f;
        reverbDecaySeconds = 0.35f;
        reverbWet = 0f;

        if (!enableProceduralRoomEcho || plan == null || roomEchoIntensity <= 0.001f)
            return;

        // Rooftops and open outdoor rooms have no enclosing walls/ceiling:
        // do not add artificial slapback or reverb, even if they have parapets.
        if (plan.Special == RoomSpecial.Rooftop || plan.Special == RoomSpecial.Outdoor || plan.Special == RoomSpecial.Forest)
            return;

        float volume = Mathf.Max(1f, plan.Width * plan.Depth * plan.WallHeight);
        float size01 = Mathf.InverseLerp(Mathf.Max(20f, roomEchoSmallVolume),
            Mathf.Max(roomEchoSmallVolume + 1f, roomEchoLargeVolume), volume);
        float maxDimension = Mathf.Max(plan.Width, plan.Depth);
        float minDimension = Mathf.Max(0.5f, Mathf.Min(plan.Width, plan.Depth));
        float aspect = maxDimension / minDimension;
        float corridor01 = plan.IsCorridor ? Mathf.InverseLerp(2f, 3.8f, aspect) : 0f;

        float sizeFactor = Mathf.Lerp(0.18f, 0.95f, size01);
        if (plan.IsCorridor)
            sizeFactor = Mathf.Max(sizeFactor, Mathf.Lerp(0.52f, 0.90f, corridor01));
        if (plan.Special == RoomSpecial.Stairs)
            sizeFactor = Mathf.Clamp01(sizeFactor + 0.08f); // tall stairwells retain more sound
        if (plan.HasPartitions)
            sizeFactor *= 0.92f;

        float wallReflection = GetTextureReflectivity(plan.WallTexture, 0.68f);
        float floorReflection = GetTextureReflectivity(plan.FloorTexture, 0.62f);
        float ceilingReflection = GetTextureReflectivity(plan.CeilingTexture, 0.62f);
        // Wall material dominates; floor and ceiling still affect the room tail.
        float surfaceReflection = wallReflection * 0.68f + floorReflection * 0.20f + ceilingReflection * 0.12f;
        float acousticStrength = Mathf.Clamp01(surfaceReflection * sizeFactor * roomEchoIntensity);

        echoWet = Mathf.Clamp(acousticStrength * 0.43f, 0f, Mathf.Clamp(roomEchoMaximumWet, 0f, 0.6f));
        if (echoWet < 0.018f) echoWet = 0f;

        // Corridors are elongated, so their first reflection arrives later.
        echoDelayMs = Mathf.Clamp(48f + maxDimension * 10f + corridor01 * 55f, 65f, 340f);
        echoDecayRatio = Mathf.Lerp(0.12f, 0.58f, acousticStrength);
        reverbDecaySeconds = Mathf.Lerp(0.22f, 2.25f, acousticStrength);
        reverbWet = Mathf.Clamp01(echoWet * 0.82f);
    }

    float GetTextureReflectivity(Texture2D texture, float fallback)
    {
        if (texture == null) return fallback;

        if (roomAcousticTextureOverrides != null)
        {
            for (int i = 0; i < roomAcousticTextureOverrides.Length; i++)
            {
                RoomAcousticTextureOverride entry = roomAcousticTextureOverrides[i];
                if (entry != null && entry.texture == texture && entry.material != ProceduralAcousticMaterial.Auto)
                    return ReflectivityForMaterial(entry.material);
            }
        }

        string n = texture.name.ToLowerInvariant();
        if (n.Contains("grass") || n.Contains("meadow") || n.Contains("foliage")) return 0.04f;
        if (n.Contains("sand") || n.Contains("desert")) return 0.07f;
        if (n.Contains("soil") || n.Contains("earth") || n.Contains("dirt")) return 0.10f;
        if (n.Contains("snow")) return 0.20f;
        if (n.Contains("carpet") || n.Contains("fabric") || n.Contains("cloth") || n.Contains("curtain")) return 0.12f;
        if (n.Contains("tile") || n.Contains("kafel") || n.Contains("ceramic") || n.Contains("marble")) return 0.97f;
        if (n.Contains("concrete") || n.Contains("cement") || n.Contains("brick") || n.Contains("stone") || n.Contains("rock")) return 0.92f;
        if (n.Contains("metal") || n.Contains("steel") || n.Contains("iron")) return 0.96f;
        if (n.Contains("glass")) return 0.98f;
        if (n.Contains("wood") || n.Contains("plank") || n == "floor") return 0.70f;
        if (n.Contains("ruberoid") || n.Contains("asphalt") || n.Contains("roof")) return 0.42f;
        if (n == "wall" || n.Contains("plaster")) return 0.68f;
        return fallback;
    }

    static float ReflectivityForMaterial(ProceduralAcousticMaterial material)
    {
        switch (material)
        {
            case ProceduralAcousticMaterial.Concrete: return 0.93f;
            case ProceduralAcousticMaterial.Brick: return 0.86f;
            case ProceduralAcousticMaterial.Stone: return 0.94f;
            case ProceduralAcousticMaterial.Tile: return 0.98f;
            case ProceduralAcousticMaterial.Metal: return 0.96f;
            case ProceduralAcousticMaterial.Glass: return 0.98f;
            case ProceduralAcousticMaterial.Wood: return 0.70f;
            case ProceduralAcousticMaterial.Plaster: return 0.66f;
            case ProceduralAcousticMaterial.Carpet: return 0.16f;
            case ProceduralAcousticMaterial.Fabric: return 0.12f;
            case ProceduralAcousticMaterial.Grass: return 0.04f;
            case ProceduralAcousticMaterial.Sand: return 0.07f;
            case ProceduralAcousticMaterial.Soil: return 0.10f;
            case ProceduralAcousticMaterial.Snow: return 0.20f;
            case ProceduralAcousticMaterial.Rubber: return 0.22f;
            default: return 0.68f;
        }
    }

    /// <summary>Следующий звук открытия из общего перемешиваемого пула.</summary>
    public AudioClip GetNextDoorOpenClip()
    {
        return GetNextDoorClip(doorOpenClips, doorOpenClip,
            _doorOpenShuffleBag, ref _doorOpenBagIndex, ref _lastDoorOpenClip);
    }

    /// <summary>Следующий звук закрытия из независимого общего пула.</summary>
    public AudioClip GetNextDoorCloseClip()
    {
        return GetNextDoorClip(doorCloseClips, doorCloseClip,
            _doorCloseShuffleBag, ref _doorCloseBagIndex, ref _lastDoorCloseClip);
    }

    static AudioClip GetNextDoorClip(AudioClip[] clips, AudioClip fallback,
        List<AudioClip> bag, ref int bagIndex, ref AudioClip lastClip)
    {
        if (clips == null || clips.Length == 0)
            return fallback;

        if (bag.Count == 0 || bagIndex >= bag.Count)
        {
            bag.Clear();
            for (int i = 0; i < clips.Length; i++)
            {
                AudioClip clip = clips[i];
                if (clip != null && !bag.Contains(clip)) bag.Add(clip);
            }
            if (bag.Count == 0) return fallback;

            // Fisher-Yates: все варианты прозвучат до повторения.
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                AudioClip temp = bag[i];
                bag[i] = bag[j];
                bag[j] = temp;
            }

            // На стыке циклов тоже не повторяем один и тот же вариант подряд.
            if (bag.Count > 1 && bag[0] == lastClip)
            {
                int swapIndex = UnityEngine.Random.Range(1, bag.Count);
                AudioClip temp = bag[0];
                bag[0] = bag[swapIndex];
                bag[swapIndex] = temp;
            }
            bagIndex = 0;
        }

        if (bagIndex >= bag.Count) return fallback;
        AudioClip selected = bag[bagIndex++];
        lastClip = selected;
        return selected;
    }

    static bool HasValidDoorClips(AudioClip[] clips)
    {
        if (clips == null) return false;
        for (int i = 0; i < clips.Length; i++)
            if (clips[i] != null) return true;
        return false;
    }

    void ConfigureDoorAudio(OneWayProceduralDoor door, ProceduralRoomPlan plan)
    {
        if (door == null) return;
        CalculateRoomEcho(plan, out float echoWet, out float echoDelayMs,
            out float echoDecayRatio, out float reverbDecay, out float reverbWet);
        bool hasOpenPool = HasValidDoorClips(doorOpenClips);
        bool hasClosePool = HasValidDoorClips(doorCloseClips);
        float volumeMin = doorVolumeMin;
        float volumeMax = doorVolumeMax;
        // Если сцена ещё использует старые одиночные AudioClip, сохраняем
        // её прежнюю настройку громкости до заполнения новых пулов.
        if (!hasOpenPool && !hasClosePool)
        {
            volumeMin = doorSoundVolume;
            volumeMax = doorSoundVolume;
        }
        door.ConfigureAudio(doorOpenClips, doorCloseClips, doorOpenClip, doorCloseClip,
            volumeMin, volumeMax, doorPitchMin, doorPitchMax,
            doorSpatialBlend, doorSoundMaxDistance,
            echoWet, echoDelayMs, echoDecayRatio, reverbDecay, reverbWet);
    }

    void ApplyRoomAtmosphere(ProceduralRoomPlan plan)
    {
        // The global ambience playlist continues across room transitions.
        // Only the rare one-shot room-event pool is updated here.
        AmbientAudioController audio = AmbientAudioController.Instance;
        if (audio == null) audio = FindFirstObjectByType<AmbientAudioController>();
        if (audio != null)
        {
            // The global music playlist does not change with rooms. Only local
            // room-event sounds and the acoustic response are updated here.
            audio.OnRoomEntered(plan);
            CalculateRoomEcho(plan, out float echoWet, out float echoDelayMs,
                out float echoDecayRatio, out float reverbDecay, out float reverbWet);
            audio.SetRoomAcoustics(echoWet, echoDelayMs, echoDecayRatio, reverbDecay, reverbWet);
        }

        bool isForest = plan != null && plan.Special == RoomSpecial.Forest;
        bool day = plan != null && (plan.Special == RoomSpecial.Rooftop || plan.Special == RoomSpecial.Outdoor || isForest);
        OutdoorRoomActive = day;

        if (!day)
        {
            // A fallback sun is created only for scenes without a directional light.
            // Disable it when leaving an outdoor room so it cannot brighten interiors.
            if (_generatedDaySun != null)
            {
                _generatedDaySun.enabled = false;
                if (RenderSettings.sun == _generatedDaySun) RenderSettings.sun = null;
            }
            // Ночь: восстановить ночную атмосферу из визуальных настроек.
            var vs = FindFirstObjectByType<ProceduralFPSVisualSettings>();
            if (vs != null)
            {
                vs.ApplyWorldLighting();
                vs.ApplySceneFog();
            }
            else if (RenderSettings.skybox != null)
            {
                RenderSettings.skybox = null;
            }
            return;
        }

        // День: небо, яркое окружение, солнце, светлый лёгкий туман.
        if (_daySkybox == null)
        {
            Shader skyboxShader = Shader.Find("Skybox/Procedural");
            if (skyboxShader != null)
            {
                _daySkybox = new Material(skyboxShader) { name = "ProceduralDaySkybox_Runtime" };
                _daySkybox.SetFloat("_AtmosphereThickness", 0.55f);
                _daySkybox.SetFloat("_Exposure", 1.0f);
                _daySkybox.SetColor("_SkyTint", new Color(0.55f, 0.66f, 0.82f));
                _daySkybox.SetColor("_GroundColor", new Color(0.34f, 0.34f, 0.36f));
            }
            else
            {
                Debug.LogWarning("[ProceduralRooms] Skybox/Procedural shader is missing; the forest will use the camera background instead.", this);
            }
        }
        if (_daySkybox != null) RenderSettings.skybox = _daySkybox;
        // Лес: небо бледнее и ниже экспозицией — пасмурный туманный день.
        if (_daySkybox != null && isForest)
        {
            _daySkybox.SetFloat("_Exposure", 0.82f);
            _daySkybox.SetColor("_SkyTint", new Color(0.72f, 0.76f, 0.83f));
        }
        else if (_daySkybox != null)
        {
            _daySkybox.SetFloat("_Exposure", 1.0f);
            _daySkybox.SetColor("_SkyTint", new Color(0.55f, 0.66f, 0.82f));
        }
        // КРИТИЧНО: скайбокс рисуется только при clearFlags = Skybox. Без этого
        // днём небо чёрное и белый туман леса выглядит как «хуйня на чёрном».
        Camera atmosphereCamera = playerCamera != null ? playerCamera : Camera.main;
        if (atmosphereCamera != null)
            atmosphereCamera.clearFlags = day ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        // Лес — пасмурный туманный день: небо бледнее, свет мягче.
        RenderSettings.ambientLight = isForest
            ? new Color(0.42f, 0.46f, 0.5f)
            : new Color(0.5f, 0.55f, 0.62f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        // Лес: БЕЛЫЙ плотный туман (туманный лес), остальные открытые — лёгкая дымка.
        RenderSettings.fogDensity = isForest ? 0.032f : 0.006f;
        RenderSettings.fogColor = isForest
            ? new Color(0.78f, 0.8f, 0.84f)
            : new Color(0.68f, 0.74f, 0.82f);

        Light sun = null;
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light == null || light.type != LightType.Directional) continue;
            if (sun == null) sun = light;
            light.color = isForest ? new Color(0.92f, 0.93f, 0.96f) : new Color(1f, 0.96f, 0.87f);
            light.intensity = isForest ? 0.55f : 0.85f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.78f;
        }

        // The scene setup normally provides a directional light. Keep a safe
        // fallback so the forest remains lit in scenes assembled by hand too.
        if (sun == null && worldRoot != null)
        {
            if (_generatedDaySun == null)
            {
                GameObject sunObject = new GameObject("__GeneratedDaySun");
                sunObject.transform.SetParent(worldRoot, false);
                sunObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
                _generatedDaySun = sunObject.AddComponent<Light>();
                _generatedDaySun.type = LightType.Directional;
            }
            sun = _generatedDaySun;
            sun.enabled = true;
            sun.color = new Color(1f, 0.96f, 0.87f);
            sun.intensity = 0.95f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.78f;
        }
        if (sun != null) RenderSettings.sun = sun;
    }

    void LateUpdate()
    {
        if (!Application.isPlaying || _pendingRoom == null)
            return;

        Camera mainCam = playerCamera != null ? playerCamera : Camera.main;
        if (mainCam == null) return;

        // Reveal the destination BEFORE the camera reaches the aperture. The camera
        // and CharacterController share horizontal position; waiting until the near
        // clip plane crosses can leave one rendered frame with no portal in front of
        // the camera. The source room remains loaded until the full capsule is safe.
        if (!_pendingRoomRevealedToMainCamera && IsCameraAtDestinationDoorPlane(mainCam))
            RevealPendingRoomToMainCamera();

        if (_pendingRoomRevealedToMainCamera || _pendingRoom.PortalCamera == null)
            return;

        Camera portalCam = _pendingRoom.PortalCamera.GetComponent<Camera>();
        if (portalCam == null) return;

        SetPortalCameraPose(portalCam, mainCam, _pendingDoor);
        portalCam.orthographic = mainCam.orthographic;
        portalCam.orthographicSize = mainCam.orthographicSize;
        portalCam.fieldOfView = mainCam.fieldOfView;
        portalCam.aspect = mainCam.aspect;
        portalCam.nearClipPlane = mainCam.nearClipPlane;
        portalCam.farClipPlane = mainCam.farClipPlane;
        // Do NOT copy the screen camera's projection matrix: a camera rendering
        // into a RenderTexture needs Unity's own RT projection/Y orientation.
        portalCam.ResetProjectionMatrix();
        portalCam.ResetCullingMatrix();

        // Project the aperture through the portal camera. This keeps the sampled
        // image perspective-correct and prevents vertical/horizontal inversion.
        UpdatePortalProjectiveMatrix(_pendingRoom, portalCam);

        // The portal camera is manually rendered so its output is ready before
        // the main camera draws the doorway surface.
        if (portalCam.targetTexture != null && portalCam.gameObject.activeInHierarchy)
            portalCam.Render();

        // Диагностика «пустой рамки»: если окно-квад пропало/неактивно, пока
        // комната скрыта на портальном слое — это причина пустого проёма.
        var window = _pendingRoom != null ? _pendingRoom.PortalWindow : null;
        if (window == null || !window.activeInHierarchy)
            Debug.LogWarning("[Portal] PortalWindow отсутствует/неактивен, хотя комната скрыта на слое 30 — проём будет пустым!", _pendingRoom.Root);
        else if (Time.frameCount % 180 == 0)
            Debug.Log($"[Portal] Window live: layer={window.layer}, pos={window.transform.position:0.00}, " +
                $"rendererEnabled={window.GetComponent<Renderer>().enabled}, frame={Time.frameCount}.", window);
    }

    static void SetPortalCameraPose(Camera portalCam, Camera mainCam, OneWayProceduralDoor door)
    {
        if (portalCam == null || mainCam == null) return;

        // View-through-window portal: the virtual camera keeps the player's real
        // pose. The destination room is adjacent to the source opening in world
        // space, so the correct sightline is the same world-space camera pose.
        // Projective sampling below fixes the previous upside-down/offset output.
        portalCam.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
    }

    static void UpdatePortalProjectiveMatrix(ProceduralRoomInstance room, Camera portalCam)
    {
        if (room == null || room.PortalMaterial == null || portalCam == null) return;
        Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(portalCam.projectionMatrix, true);
        room.PortalMaterial.SetMatrix("_PortalVP", gpuProjection * portalCam.worldToCameraMatrix);
    }

    bool IsCameraAtDestinationDoorPlane(Camera cameraToTest)
    {
        if (cameraToTest == null || _pendingDoor == null) return false;
        Vector3 normal = _pendingDoor.OutwardNormal.normalized;
        float signedDistance = Vector3.Dot(cameraToTest.transform.position - _pendingDoor.OpeningCenterWorld, normal);
        if (playerRoot != null)
            signedDistance = Mathf.Max(signedDistance,
                Vector3.Dot(playerRoot.position - _pendingDoor.OpeningCenterWorld, normal));
        // Reveal only shortly before the near clip reaches the aperture. Earlier
        // reveal removes the door-sized RT while the camera is still in the source
        // room; later reveal lets the portal quad be clipped and exposes a void.
        float revealBeforePlane = Mathf.Max(0.35f, cameraToTest.nearClipPlane + 0.05f);
        return signedDistance >= -revealBeforePlane;
    }

    void RevealPendingRoomToMainCamera()
    {
        if (_pendingRoom == null || _pendingRoomRevealedToMainCamera) return;
        // Restores the original layers/culling mask and removes only the temporary
        // RT portal. Room colliders and both room instances remain alive.
        FinishPortalEntry(_pendingRoom);
        _pendingRoomRevealedToMainCamera = true;
        float signedDistance = _pendingDoor != null && playerCamera != null
            ? Vector3.Dot(playerCamera.transform.position - _pendingDoor.OpeningCenterWorld, _pendingDoor.OutwardNormal.normalized)
            : 0f;
        Debug.Log($"[ProceduralRooms] Destination visuals revealed to main camera early (door-plane distance={signedDistance:0.00}m); source room stays loaded until safe grounded commit.", this);
    }

    void Update()
    {
        // The destination room is initially shown through the door-sized RT portal.
        // As the player's camera approaches the aperture, its real renderers are
        // restored before the portal leaves view; the previous room remains alive
        // until the whole capsule has crossed and is physically supported.
        if (Application.isPlaying && _pendingRoom != null && _pendingDoor != null && playerRoot != null)
        {
            Vector3 currentPlayerPosition = playerRoot.position;
            if (_pendingPlayerSampleValid && DidPlayerCrossDoorPlane(
                _pendingLastPlayerPosition, currentPlayerPosition, _pendingDoor))
            {
                _pendingDoorPlaneCrossed = true;
                RevealPendingRoomToMainCamera();
                Debug.Log("[ProceduralRooms] Door plane crossed; waiting for capsule clearance and destination floor contact.", this);
            }

            _pendingLastPlayerPosition = currentPlayerPosition;
            _pendingPlayerSampleValid = true;

            if (_pendingDoorPlaneCrossed)
            {
                bool capsuleClear = IsPlayerFullyClearOfDoorway(_pendingDoor) && IsPlayerDeepInsideDestination(_pendingDoor);
                bool supportedByDestination = IsPlayerSupportedByRoom(_pendingRoom);
                // CharacterController.isGrounded describes the result of its LAST
                // Move call. Depending on Update order it can flicker for one frame
                // while a player settles on uneven terrain. The room-owned multi-point
                // support probe below is the stable criterion; require it continuously
                // for 0.35 s before committing the transition.
                if (capsuleClear && supportedByDestination)
                {
                    _pendingGroundedClearTime += Time.deltaTime;
                    if (_pendingGroundedClearTime >= 0.35f)
                    {
                        CommitPendingTransition();
                        return;
                    }
                }
                else
                {
                    _pendingGroundedClearTime = 0f;
                    if (capsuleClear && !supportedByDestination && !_pendingSupportWarningLogged)
                    {
                        _pendingSupportWarningLogged = true;
                        Debug.LogWarning("[ProceduralRooms] Player capsule cleared the doorway but no stable destination-room floor support was detected. Keeping the previous room loaded.", this);
                    }
                }
            }
        }

        // Редакторский предпросмотр: строим сейв-комнату прямо в edit-mode,
        // чтобы Game-вид показывал мир и его можно было декорировать.
        if (Application.isPlaying || !buildInEditMode) return;
        // В C#-обёртке ссылки на Unity-объекты не становятся null после удаления
        // объекта руками — проверяем жив ли корень комнаты.
        if (_activeRoom != null && _activeRoom.Root != null) return;
        ResolveReferences();
        if (worldRoot == null || playerRoot == null) return;
        CleanupGeneratedRooms();
        _activeRoom = BuildSaveRoom();
        playerRoot.position = saveRoomPlayerSpawn;
        playerRoot.rotation = Quaternion.identity;
        Debug.Log("[ProceduralRooms] Сейв-комната построена в редакторе (сохранится со сценой).", this);
    }

    /// <summary>
    /// Удаляет всё, что нагенерил этот менеджер: корни комнат (Room_*) и
    /// бокс-коллайдеры стен на __ProceduralWallOcclusion. Вызывается перед
    /// каждой сборкой, чтобы комната не задваивалась между edit и Play.
    /// wallOcclusionRoot считается собственностью генератора — ручные
    /// коллайдеры туда добавлять не стоит.
    /// </summary>
    void CleanupGeneratedRooms()
    {
        if (worldRoot != null)
        {
            for (int i = worldRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = worldRoot.GetChild(i);
                if (!child.name.StartsWith("Room_", System.StringComparison.Ordinal)) continue;
                if (Application.isPlaying) Destroy(child.gameObject);
                else DestroyImmediate(child.gameObject);
            }
        }
        if (wallOcclusionRoot != null)
        {
            BoxCollider[] boxes = wallOcclusionRoot.GetComponents<BoxCollider>();
            foreach (BoxCollider box in boxes)
            {
                if (Application.isPlaying) Destroy(box);
                else DestroyImmediate(box);
            }
        }
    }

    void EnsureRoots()
    {
        if (worldRoot == null) worldRoot = transform;
        worldRoot.position = Vector3.zero;
        worldRoot.rotation = Quaternion.identity;
        worldRoot.localScale = Vector3.one;

        if (wallOcclusionRoot == null)
        {
            Transform found = worldRoot.Find("__ProceduralWallOcclusion");
            if (found != null) wallOcclusionRoot = found;
            else
            {
                GameObject wallRoot = new GameObject("__ProceduralWallOcclusion");
                wallRoot.transform.SetParent(worldRoot, false);
                wallOcclusionRoot = wallRoot.transform;
            }
        }
        wallOcclusionRoot.localPosition = Vector3.zero;
        wallOcclusionRoot.localRotation = Quaternion.identity;
        wallOcclusionRoot.localScale = Vector3.one;
    }

    void ResolveReferences()
    {
        if (forestGenerator == null) forestGenerator = GetComponent<ForestRoomGenerator>();
        if (forestGenerator == null) forestGenerator = gameObject.AddComponent<ForestRoomGenerator>();
        if (playerController == null) playerController = FindFirstObjectByType<ProceduralFirstPersonController>();
        if (playerRoot == null && playerController != null) playerRoot = playerController.transform;
        if (playerCamera == null && playerController != null) playerCamera = playerController.playerCamera;
        if (playerCamera == null) playerCamera = Camera.main;
        if (playerController != null) playerController.roomManager = this;

        DisableLegacyDriversThatConflictWithFpsPlayer();
    }

    // The legacy isometric bootstrap intentionally skips FPS scenes, but components
    // that were already serialized into the scene can still remain enabled. Disable
    // only components that directly control the FPS root or its first-person camera.
    void DisableLegacyDriversThatConflictWithFpsPlayer()
    {
        if (playerController == null || playerRoot == null) return;

        PlayerCharacterController[] legacyPlayers =
            FindObjectsByType<PlayerCharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        for (int i = 0; i < legacyPlayers.Length; i++)
        {
            PlayerCharacterController legacy = legacyPlayers[i];
            if (legacy == null || !legacy.isActiveAndEnabled || legacy.character == null) continue;

            Transform target = legacy.character;
            bool controlsFpsHierarchy =
                target == playerRoot ||
                target.IsChildOf(playerRoot) ||
                playerRoot.IsChildOf(target);

            if (!controlsFpsHierarchy) continue;

            legacy.enabled = false;
            Debug.LogWarning(
                $"[FPS COMPAT] Disabled legacy PlayerCharacterController '{legacy.name}': " +
                $"its character target '{target.name}' overlaps the procedural FPS player hierarchy. " +
                "Two movement controllers must not drive the same CharacterController.",
                legacy);
        }

        CameraFollowController[] legacyCameraFollowers =
            FindObjectsByType<CameraFollowController>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        for (int i = 0; i < legacyCameraFollowers.Length; i++)
        {
            CameraFollowController follower = legacyCameraFollowers[i];
            if (follower == null || !follower.isActiveAndEnabled || playerCamera == null) continue;

            // CameraFollowController is the old isometric camera driver. It must not
            // write to the camera that is parented to / assigned to the FPS rig.
            bool drivesFpsCamera =
                follower.transform == playerCamera.transform ||
                follower.transform == playerRoot ||
                playerCamera.transform.IsChildOf(follower.transform);

            if (!drivesFpsCamera) continue;

            follower.enabled = false;
            Debug.LogWarning(
                $"[FPS COMPAT] Disabled legacy CameraFollowController '{follower.name}' " +
                "because it drives the procedural FPS camera hierarchy.",
                follower);
        }
    }

    ProceduralRoomInstance BuildSaveRoom()
    {
        var plan = new ProceduralRoomPlan
        {
            Seed = randomSeed,
            Width = 9f,
            Depth = 9f,
            WallHeight = wallHeight,
            HasIncomingSide = false,
            FloorTexture = PickFloorTexture(null),
            CeilingTexture = PickCeilingTexture(null, null),
            WallTexture = PickTexture(wallTextures, null)
        };
        plan.Exits.Add(new ProceduralDoorSocket(ProceduralRoomSide.North, 0f));
        return BuildRoom(plan, Vector3.zero, null);
    }

    ProceduralRoomPlan CreateProceduralPlan(int seed, ProceduralRoomSide incomingSide, Texture2D previousWallTexture = null)
    {
        var rng = new System.Random(seed);
        float min = Mathf.Min(minimumRoomSize, maximumRoomSize);
        float max = Mathf.Max(minimumRoomSize, maximumRoomSize);

        // Архетипы: коридор / перегородки / особые типы (крыша, бассейн, лестница,
        // метро, пустыня-луг) / обычный зал.
        int roll = rng.Next(100);
        bool corridor = roll < 30;
        bool partitioned = !corridor && roll < 55;
        RoomSpecial special = RoomSpecial.None;
        if (!corridor && !partitioned)
        {
            int s = rng.Next(100);
            int forestChance = forestGenerator != null ? Mathf.Clamp(forestGenerator.forestRoomChance, 0, 100) : 22;
            if (s < forestChance)
            {
                special = RoomSpecial.Forest;
            }
            else
            {
                // Preserve the previous relative distribution of the five existing
                // special room types while reserving an independently tunable forest chance.
                int legacyRoll = Mathf.FloorToInt((s - forestChance) * 100f / Mathf.Max(1, 100 - forestChance));
                special = legacyRoll < 19 ? RoomSpecial.Rooftop
                        : legacyRoll < 37 ? RoomSpecial.Pool
                        : legacyRoll < 55 ? RoomSpecial.Stairs
                        : legacyRoll < 67 ? RoomSpecial.Metro
                        : legacyRoll < 80 ? RoomSpecial.Elevator
                        : RoomSpecial.Outdoor;
            }
        }

        float width, depth;
        if (corridor)
        {
            // Коридор вытянут вдоль оси входа: вход обычно попадает на короткий торец.
            float corridorWidth = RoundToHalf(3.5f + (float)rng.NextDouble() * 1.5f);      // 3.5–5 м
            float corridorLength = RoundToHalf(Mathf.Max(max * 1.25f, 11f) + (float)rng.NextDouble() * 4f); // ~11–17 м
            bool corridorAlongDepth = incomingSide == ProceduralRoomSide.North || incomingSide == ProceduralRoomSide.South;
            width = corridorAlongDepth ? corridorWidth : corridorLength;
            depth = corridorAlongDepth ? corridorLength : corridorWidth;
        }
        else if (special == RoomSpecial.Metro)
        {
            // Метро вытянуто вдоль оси входа: платформа и яма с рельсами.
            bool metroRunAlongDepth = incomingSide == ProceduralRoomSide.North || incomingSide == ProceduralRoomSide.South;
            float run = RoundToHalf(14f + (float)rng.NextDouble() * 4f);   // 14–18 м
            float cross = RoundToHalf(7f + (float)rng.NextDouble() * 2f);  // 7–9 м
            width = metroRunAlongDepth ? cross : run;
            depth = metroRunAlongDepth ? run : cross;
        }
        else if (special == RoomSpecial.Elevator)
        {
            // Кабина лифта: тесно (2.8×3.0), низкий потолок, вытянута вдоль оси входа.
            bool elevatorRunAlongDepth = incomingSide == ProceduralRoomSide.North || incomingSide == ProceduralRoomSide.South;
            width = elevatorRunAlongDepth ? 2.8f : 3f;
            depth = elevatorRunAlongDepth ? 3f : 2.8f;
        }
        else if (special == RoomSpecial.Stairs)
        {
            // Лестница — компактный зал вдоль оси входа.
            bool stairsRunAlongDepth = incomingSide == ProceduralRoomSide.North || incomingSide == ProceduralRoomSide.South;
            float run = RoundToHalf(12f + (float)rng.NextDouble() * 1.5f);   // 12–13.5 м
            float cross = RoundToHalf(6.5f + (float)rng.NextDouble() * 1f);  // 6.5–7.5 м
            width = stairsRunAlongDepth ? cross : run;
            depth = stairsRunAlongDepth ? run : cross;
        }
        else if (special == RoomSpecial.Pool)
        {
            width = RoundToHalf(10.5f + (float)rng.NextDouble() * 3.5f);   // 10.5–14 м
            depth = RoundToHalf(9f + (float)rng.NextDouble() * 3f);        // 9–12 м
        }
        else if (special == RoomSpecial.Forest)
        {
            // At least 3x the linear dimensions of the previous outdoor rooms.
            float forestMin = forestGenerator != null ? forestGenerator.GetMinimumForestDimension() : 31.5f;
            float forestMax = forestGenerator != null ? forestGenerator.GetMaximumForestDimension() : 48f;
            width = RoundToHalf(forestMin + (float)rng.NextDouble() * (forestMax - forestMin));
            depth = RoundToHalf(forestMin + (float)rng.NextDouble() * (forestMax - forestMin));
        }
        else if (special == RoomSpecial.Rooftop || special == RoomSpecial.Outdoor)
        {
            width = RoundToHalf(10.5f + (float)rng.NextDouble() * 5.5f);   // 10.5–16 м
            depth = RoundToHalf(10.5f + (float)rng.NextDouble() * 5.5f);
        }
        else
        {
            // Залы: разброс размеров; под перегородки берём верхнюю половину диапазона.
            float lo = partitioned ? Mathf.Lerp(min, max, 0.55f) : min;
            width = RoundToHalf(lo + (float)rng.NextDouble() * (max - lo));
            depth = RoundToHalf(lo + (float)rng.NextDouble() * (max - lo));
        }

        var plan = new ProceduralRoomPlan
        {
            Seed = seed,
            Width = width,
            Depth = depth,
            WallHeight = wallHeight,
            HasIncomingSide = true,
            IncomingSide = incomingSide,
            IsCorridor = corridor,
            HasPartitions = partitioned,
            Special = special,
            FloorTexture = PickFloorTexture(rng),
            CeilingTexture = PickCeilingTexture(rng, null),
            WallTexture = PickWallTexture(rng, previousWallTexture)
        };
        if (special == RoomSpecial.Stairs)
            plan.WallHeight = 6.4f; // два этажа по 3.2 м

        // Комната-карман в конце коридора: отгораживается стеной с внутренней дверью.
        bool alongDepth = depth > width;
        if (corridor && rng.Next(100) < 45)
        {
            plan.HasClosetRoom = true;
            plan.ClosetDepth = 3f + (float)rng.NextDouble() * 1.5f;
        }

        List<ProceduralDoorSocket> candidates = BuildDoorCandidates(width, depth, incomingSide);
        Shuffle(candidates, rng);
        if (corridor)
        {
            // В коридоре двери живут на ДЛИННЫХ стенах; в торцах — максимум одна.
            var longSide = new List<ProceduralDoorSocket>();
            var endSide = new List<ProceduralDoorSocket>();
            foreach (ProceduralDoorSocket socket in candidates)
            {
                bool isLongSide = alongDepth
                    ? socket.Side == ProceduralRoomSide.East || socket.Side == ProceduralRoomSide.West
                    : socket.Side == ProceduralRoomSide.North || socket.Side == ProceduralRoomSide.South;
                if (isLongSide) longSide.Add(socket); else endSide.Add(socket);
            }
            if (plan.HasClosetRoom)
            {
                // Двери не должны попасть внутрь комнаты-кармана у дальнего торца.
                float runHalf = alongDepth ? depth * 0.5f : width * 0.5f;
                float allowed = runHalf - plan.ClosetDepth - 1.2f;
                longSide.RemoveAll(s => Mathf.Abs(s.Along) > allowed);
                endSide.Clear();
            }
            candidates.Clear();
            candidates.AddRange(longSide);
            if (endSide.Count > 0) candidates.Add(endSide[0]);
        }
        if (special == RoomSpecial.Metro)
        {
            // Двери метро — только в торцах: яма с рельсами идёт вдоль длинных стен.
            bool metroAlongDepth = depth > width;
            candidates.RemoveAll(s => metroAlongDepth
                ? s.Side == ProceduralRoomSide.East || s.Side == ProceduralRoomSide.West
                : s.Side == ProceduralRoomSide.North || s.Side == ProceduralRoomSide.South);
        }
        if (special == RoomSpecial.Stairs)
        {
            // Двери лестницы — только на ДАЛЬНЕМ торце у земли: марши и площадки
            // вдоль длинных стен не должны отгораживать проёмы.
            candidates.RemoveAll(s => s.Side != Opposite(incomingSide));
        }
        if (special == RoomSpecial.Elevator)
        {
            // У лифта НЕТ дверей-выходов: единственный проём — входной. После
            // «поездки» дверь в следующую комнату спавнится в этом же проёме.
            candidates.Clear();
        }

        int maxCount = Mathf.Min(Mathf.Clamp(maximumDoorsPerRoom, 1, 3), candidates.Count);
        int minCount = Mathf.Clamp(minimumDoorsPerRoom, 1, maxCount);
        int wanted = rng.Next(minCount, maxCount + 1);
        for (int i = 0; i < wanted; i++)
            plan.Exits.Add(candidates[i]);

        // A room always has at least one onward route, even with unusual settings.
        if (plan.Exits.Count == 0)
        {
            ProceduralRoomSide fallback = Opposite(incomingSide);
            plan.Exits.Add(new ProceduralDoorSocket(fallback, 0f));
        }
        return plan;
    }

    Texture2D PickFloorTexture(System.Random rng)
    {
        // По умолчанию пол всегда ПЕРВАЯ текстура (доски): снег на полу больше не появляется.
        if (floorTextures == null || floorTextures.Length == 0) return null;
        if (!floorVariety || rng == null || floorTextures.Length == 1) return floorTextures[0];
        return PickTexture(floorTextures, rng);
    }

    Texture2D PickCeilingTexture(System.Random rng, Texture2D fallback)
    {
        Texture2D picked = PickTexture(ceilingTextures, rng);
        if (picked != null) return picked;
        return fallback != null ? fallback : (wallTextures != null && wallTextures.Length > 0 ? wallTextures[0] : null);
    }

    List<ProceduralDoorSocket> BuildDoorCandidates(float width, float depth, ProceduralRoomSide incomingSide)
    {
        var candidates = new List<ProceduralDoorSocket>();
        AddSideCandidates(candidates, ProceduralRoomSide.North, width, incomingSide);
        AddSideCandidates(candidates, ProceduralRoomSide.South, width, incomingSide);
        AddSideCandidates(candidates, ProceduralRoomSide.East, depth, incomingSide);
        AddSideCandidates(candidates, ProceduralRoomSide.West, depth, incomingSide);
        return candidates;
    }

    void AddSideCandidates(List<ProceduralDoorSocket> list, ProceduralRoomSide side, float length, ProceduralRoomSide incomingSide)
    {
        float margin = doorWidth * 0.5f + 0.55f;
        var offsets = new List<float> { 0f };
        if (length >= 7.2f)
        {
            offsets.Add(-length * 0.27f);
            offsets.Add(length * 0.27f);
        }
        if (length >= 11.5f)
        {
            offsets.Add(-length * 0.39f);
            offsets.Add(length * 0.39f);
        }

        foreach (float offset in offsets)
        {
            if (Mathf.Abs(offset) > length * 0.5f - margin) continue;
            if (side == incomingSide && Mathf.Abs(offset) < doorWidth + 0.35f) continue;
            list.Add(new ProceduralDoorSocket(side, offset));
        }
    }

    ProceduralRoomInstance BuildRoom(ProceduralRoomPlan plan, Vector3 center, OneWayProceduralDoor sourceDoor)
    {
        var room = new ProceduralRoomInstance { Plan = plan, Center = center };
        GameObject rootObject = new GameObject("Room_" + (++_nextRoomNumber).ToString("000") + "_Seed_" + plan.Seed);
        rootObject.transform.SetParent(worldRoot, false);
        rootObject.transform.position = center;
        room.Root = rootObject;

        // Пол/потолок по типу комнаты.
        if (plan.Special == RoomSpecial.Forest)
        {
            if (forestGenerator == null) forestGenerator = GetComponent<ForestRoomGenerator>();
            if (forestGenerator == null) forestGenerator = gameObject.AddComponent<ForestRoomGenerator>();
            forestGenerator.Generate(room.Root.transform, plan, doorWidth, GetForestFloorMaterial(), textureMetersPerTile,
                room.RuntimeMeshes, room.RuntimeTerrainData);
            Transform landingTransform = room.Root.transform.Find("ForestEntranceLandingCollider");
            room.ForestEntranceLanding = landingTransform != null ? landingTransform.GetComponent<BoxCollider>() : null;
        }
        else if (plan.Special == RoomSpecial.Pool) BuildPoolFloors(room);
        else if (plan.Special == RoomSpecial.Metro) BuildMetroFloors(room);
        else if (plan.Special == RoomSpecial.Rooftop) BuildFloorRect(room, "RoofFloor", 0f,
            -plan.Width * 0.5f, plan.Width * 0.5f, -plan.Depth * 0.5f, plan.Depth * 0.5f, GetRoofMaterial(), true);
        else if (plan.Special == RoomSpecial.Outdoor) BuildFloorRect(room, "GroundFloor", 0f,
            -plan.Width * 0.5f, plan.Width * 0.5f, -plan.Depth * 0.5f, plan.Depth * 0.5f, GetOutdoorFloorMaterial(plan), true);
        else BuildFloorAndCeiling(room);

        // The forest has invisible collider walls only; it has no visible side walls.
        if (plan.Special == RoomSpecial.Rooftop)
            BuildParapetWalls(room, 1.15f, GetRoofMaterial());
        else if (plan.Special == RoomSpecial.Outdoor)
            BuildParapetWalls(room, 0.7f, GetOutdoorWallMaterial());
        else if (plan.Special != RoomSpecial.Forest)
            BuildWalls(room);

        // Open-air forest does not count as an indoor wind shelter.
        if (plan.Special != RoomSpecial.Forest) BuildShelterZone(room);

        // Особый контент комнаты.
        switch (plan.Special)
        {
            case RoomSpecial.Rooftop: BuildRoofProps(room); break;
            case RoomSpecial.Pool: BuildPoolInterior(room); break;
            case RoomSpecial.Stairs: BuildStaircase(room); break;
            case RoomSpecial.Metro: BuildMetroDetails(room); break;
            case RoomSpecial.Outdoor: BuildOutdoorProps(room); break;
            case RoomSpecial.Elevator: BuildElevatorInterior(room); break;
        }

        for (int i = 0; i < plan.Exits.Count; i++)
        {
            ProceduralDoorSocket socket = plan.Exits[i];
            int destinationSeed = NextSeed();
            Texture2D previousWall = _activeRoom != null && _activeRoom.Plan != null
                ? _activeRoom.Plan.WallTexture : null;
            ProceduralRoomPlan destinationPlan = CreateProceduralPlan(destinationSeed, Opposite(socket.Side), previousWall);
            OneWayProceduralDoor door = BuildDoor(room, socket, destinationPlan);
            room.Doors.Add(door);
        }

        if (plan.HasIncomingSide)
            BuildArrivalTrigger(room, sourceDoor);

        return room;
    }

    void BuildFloorAndCeiling(ProceduralRoomInstance room)
    {
        float w = room.Plan.Width;
        float d = room.Plan.Depth;
        float h = room.Plan.WallHeight;

        Mesh floorMesh = CreatePlaneMesh(w, d, 0f, false, "ProceduralFloor_" + room.Plan.Seed);
        room.RuntimeMeshes.Add(floorMesh);
        GameObject floorObject = new GameObject("Floor");
        floorObject.transform.SetParent(room.Root.transform, false);
        floorObject.AddComponent<MeshFilter>().sharedMesh = floorMesh;
        floorObject.AddComponent<MeshRenderer>().sharedMaterial = GetFloorMaterial(room.Plan.FloorTexture);
        BoxCollider floorCollider = floorObject.AddComponent<BoxCollider>();
        floorCollider.center = new Vector3(0f, -0.09f, 0f);
        floorCollider.size = new Vector3(w, 0.18f, d);

        Mesh ceilingMesh = CreatePlaneMesh(w, d, h, true, "ProceduralCeiling_" + room.Plan.Seed);
        room.RuntimeMeshes.Add(ceilingMesh);
        GameObject ceilingObject = new GameObject("Ceiling");
        ceilingObject.transform.SetParent(room.Root.transform, false);
        ceilingObject.AddComponent<MeshFilter>().sharedMesh = ceilingMesh;
        ceilingObject.AddComponent<MeshRenderer>().sharedMaterial = GetCeilingMaterial(room.Plan.CeilingTexture);
    }

    Mesh CreatePlaneMesh(float width, float depth, float y, bool downward, string meshName)
    {
        var mesh = new Mesh { name = meshName };
        Vector3 a, b, c, d;
        if (!downward)
        {
            a = new Vector3(-width * 0.5f, y, -depth * 0.5f);
            b = new Vector3(-width * 0.5f, y, depth * 0.5f);
            c = new Vector3(width * 0.5f, y, depth * 0.5f);
            d = new Vector3(width * 0.5f, y, -depth * 0.5f);
        }
        else
        {
            a = new Vector3(-width * 0.5f, y, -depth * 0.5f);
            b = new Vector3(width * 0.5f, y, -depth * 0.5f);
            c = new Vector3(width * 0.5f, y, depth * 0.5f);
            d = new Vector3(-width * 0.5f, y, depth * 0.5f);
        }

        float uvX = width / Mathf.Max(0.25f, textureMetersPerTile);
        float uvY = depth / Mathf.Max(0.25f, textureMetersPerTile);
        mesh.vertices = new[] { a, b, c, d };
        mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, uvY), new Vector2(uvX, uvY), new Vector2(uvX, 0f) };
        // The vertex order is already reversed for the ceiling, so the same
        // triangle winding gives +Y on the floor and -Y on the ceiling.
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void BuildWalls(ProceduralRoomInstance room)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        AddWallForSide(room, ProceduralRoomSide.North, vertices, uvs, triangles);
        AddWallForSide(room, ProceduralRoomSide.East, vertices, uvs, triangles);
        AddWallForSide(room, ProceduralRoomSide.South, vertices, uvs, triangles);
        AddWallForSide(room, ProceduralRoomSide.West, vertices, uvs, triangles);

        if (room.Plan.HasPartitions)
            BuildInteriorWalls(room, vertices, uvs, triangles);

        var mesh = new Mesh { name = "ProceduralWalls_" + room.Plan.Seed };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        room.RuntimeMeshes.Add(mesh);

        GameObject wallObject = new GameObject("WallSurfaces");
        // FogOfWarController's wall-raster pass intentionally filters layer 31.
        wallObject.layer = 31;
        wallObject.transform.SetParent(room.Root.transform, false);
        wallObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        wallObject.AddComponent<MeshRenderer>().sharedMaterial = GetWallMaterial(room.Plan.WallTexture);
    }

    /// <summary>
    /// Внутренние перегородки. Правила размещения (фикс «стены посредине входа»):
    /// 1) перегородка ВСЕГДА идёт в стык хотя бы с одной внешней стеной (полный
    ///    пролёт — с обеими), никаких висящих в воздухе обрывков;
    /// 2) ни один дверной проём (вход + выходы) не перекрывается: плоскость
    ///    перегородки держит дистанцию от входной стены, опорная стена без
    ///    дверей, пролёт не накрывает двери на параллельных стенах;
    /// 3) проём в перегородке по возможности совмещается с дверью напротив —
    ///    проход продолжается сквозь перегородку.
    /// В коридоре с HasClosetRoom поперечная стена отгораживает дальний торец —
    /// получается комната ВНУТРИ коридора, вход через внутреннюю дверь.
    /// </summary>
    void BuildInteriorWalls(ProceduralRoomInstance room, List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
    {
        var prng = new System.Random(room.Plan.Seed ^ 0x5f3759df);
        var plan = room.Plan;

        // Все дверные проёмы комнаты (вход + выходы): сторона + позиция вдоль стены.
        var openings = new List<(ProceduralRoomSide side, float along)>();
        if (plan.HasIncomingSide) openings.Add((plan.IncomingSide, 0f));
        foreach (ProceduralDoorSocket exit in plan.Exits)
            openings.Add((exit.Side, exit.Along));

        if (plan.IsCorridor)
        {
            if (plan.HasClosetRoom)
                BuildCorridorCloset(room, plan.ClosetDepth, plan.WallHeight, vertices, uvs, triangles);
            return;
        }

        int count = 1 + prng.Next(2); // 1–2 перегородки
        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (TryBuildAnchoredPartition(room, openings, prng, vertices, uvs, triangles)) break;
            }
        }
    }

    bool TryBuildAnchoredPartition(ProceduralRoomInstance room,
        List<(ProceduralRoomSide side, float along)> openings,
        System.Random prng, List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
    {
        var plan = room.Plan;
        bool alongX = prng.Next(2) == 0; // стена параллельна X (делит комнату по Z)
        float halfRun = alongX ? plan.Width * 0.5f : plan.Depth * 0.5f;
        float halfCross = alongX ? plan.Depth * 0.5f : plan.Width * 0.5f;
        if (halfRun < doorWidth + 2.4f || halfCross < 4.2f) return false;

        // 1) Плоскость перегородки (offset по поперечной оси): от входной стены
        //    >= 2.6 м, от любой другой параллельной стены с дверью >= 1.8 м.
        float lo = -halfCross + 0.5f, hi = halfCross - 0.5f;
        foreach (var (side, along) in openings)
        {
            bool parallel = (alongX && (side == ProceduralRoomSide.North || side == ProceduralRoomSide.South))
                         || (!alongX && (side == ProceduralRoomSide.East || side == ProceduralRoomSide.West));
            if (!parallel) continue;
            float wallCross = side == ProceduralRoomSide.North || side == ProceduralRoomSide.East ? halfCross : -halfCross;
            float clear = plan.HasIncomingSide && side == plan.IncomingSide ? 2.6f : 1.8f;
            if (wallCross > 0f) hi = Mathf.Min(hi, wallCross - clear);
            else lo = Mathf.Max(lo, wallCross + clear);
        }
        if (lo > hi) return false;
        float offset = Mathf.Lerp(lo, hi, (float)prng.NextDouble());

        // 2) Опорная стена без дверей: торец перегородки не должен попасть в проём.
        bool anchorAtMin = prng.Next(2) == 0;
        // для alongX опоры — West(-run)/East(+run), их двери: Along по Z (=offset-ось)
        foreach (var (side, along) in openings)
        {
            bool isAnchorWall = (alongX && (anchorAtMin ? side == ProceduralRoomSide.West : side == ProceduralRoomSide.East))
                             || (!alongX && (anchorAtMin ? side == ProceduralRoomSide.South : side == ProceduralRoomSide.North));
            if (isAnchorWall && Mathf.Abs(along - offset) < doorWidth * 0.5f + 0.9f) return false;
        }

        // 3) Пролёт: 55–100% длины, всегда от опорной стены (стык!).
        float span = halfRun * 2f * (0.55f + 0.45f * (float)prng.NextDouble());
        bool fullSpan = span >= halfRun * 2f - 0.05f;
        float start, end;
        if (fullSpan) { start = -halfRun; end = halfRun; }
        else if (anchorAtMin) { start = -halfRun; end = start + span; }
        else { end = halfRun; start = end - span; }

        // 4) Двери на параллельных стенах: пролёт не должен накрывать проём,
        //    кроме случая когда проём перегородки совмещён с этой дверью.
        var blockedSockets = new List<float>();
        foreach (var (side, along) in openings)
        {
            bool parallel = (alongX && (side == ProceduralRoomSide.North || side == ProceduralRoomSide.South))
                         || (!alongX && (side == ProceduralRoomSide.East || side == ProceduralRoomSide.West));
            if (!parallel) continue;
            if (along > start - doorWidth * 0.5f - 0.8f && along < end + doorWidth * 0.5f + 0.8f)
                blockedSockets.Add(along);
        }

        // Проём внутри пролёта; если есть накрытые двери — совмещаем проём с одной из них.
        float gapCenter;
        float gapLo = start + doorWidth * 0.5f + 0.35f;
        float gapHi = end - doorWidth * 0.5f - 0.35f;
        if (gapLo > gapHi) return false;
        var passage = blockedSockets.FindAll(s => s >= gapLo && s <= gapHi);
        if (passage.Count > 0) gapCenter = passage[prng.Next(passage.Count)];
        else gapCenter = Mathf.Lerp(gapLo, gapHi, (float)prng.NextDouble());

        // Все накрытые двери должны быть проходящими через проём перегородки.
        foreach (float s in blockedSockets)
            if (Mathf.Abs(s - gapCenter) > doorWidth * 0.5f + 1.3f) return false;

        // 5) Перпендикулярные двери: плоскость не должна стоять прямо перед проёмом,
        //    если свободный торец почти касается той стены.
        bool anchoredAtMax = !anchorAtMin || fullSpan;
        bool anchoredAtMin = anchorAtMin || fullSpan;
        foreach (var (side, along) in openings)
        {
            bool perpendicular = (alongX && (side == ProceduralRoomSide.East || side == ProceduralRoomSide.West))
                              || (!alongX && (side == ProceduralRoomSide.North || side == ProceduralRoomSide.South));
            if (!perpendicular) continue;
            bool touchesThatWall = side == ProceduralRoomSide.East || side == ProceduralRoomSide.North
                ? anchoredAtMax : anchoredAtMin;
            if (Mathf.Abs(along - offset) < doorWidth * 0.5f + 0.6f && touchesThatWall) return false;
        }

        // 6) Строим панели в стык + физику + иногда внутреннюю дверь в проёме.
        float gapStart = gapCenter - doorWidth * 0.5f;
        float gapEnd = gapCenter + doorWidth * 0.5f;
        AddPartitionPanel(room, alongX, offset, start, gapStart, plan.WallHeight, vertices, uvs, triangles);
        AddPartitionPanel(room, alongX, offset, gapEnd, end, plan.WallHeight, vertices, uvs, triangles);

        if (prng.Next(100) < 35)
        {
            Vector3 doorPos = alongX ? new Vector3(gapCenter, 0f, offset) : new Vector3(offset, 0f, gapCenter);
            Vector3 normal = alongX ? Vector3.forward : Vector3.right;
            BuildInteriorDoor(room, doorPos, Quaternion.LookRotation(normal, Vector3.up));
        }
        return true;
    }

    /// <summary>
    /// Комната-карман в дальнем торце коридора: поперечная стена в стык к обеим
    /// стенам с проёмом и внутренней дверью. Двери-выходы сюда не попадают
    /// (отфильтрованы в CreateProceduralPlan).
    /// </summary>
    void BuildCorridorCloset(ProceduralRoomInstance room, float closetDepth, float height,
        List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
    {
        var plan = room.Plan;
        bool alongDepth = plan.Depth > plan.Width;
        float runHalf = alongDepth ? plan.Depth * 0.5f : plan.Width * 0.5f;
        float crossHalf = alongDepth ? plan.Width * 0.5f : plan.Depth * 0.5f;
        if (crossHalf < doorWidth + 1.0f) return;

        var prng = new System.Random(plan.Seed ^ unchecked((int)0x9e3779b9));
        bool entranceAtPositive = plan.IncomingSide == ProceduralRoomSide.North || plan.IncomingSide == ProceduralRoomSide.East;
        float plane = entranceAtPositive ? -runHalf + closetDepth : runHalf - closetDepth;
        plane = Mathf.Clamp(plane, -runHalf + 0.6f, runHalf - 0.6f);

        float gapCenter = ((float)prng.NextDouble() * 2f - 1f) * Mathf.Max(0.2f, crossHalf - doorWidth * 0.5f - 0.5f);
        float gapStart = gapCenter - doorWidth * 0.5f;
        float gapEnd = gapCenter + doorWidth * 0.5f;

        // Поперечная стена: перпендикулярна оси коридора, полный пролёт в стык.
        bool partitionAlongX = !alongDepth;
        AddPartitionPanel(room, partitionAlongX, plane, -crossHalf, gapStart, height, vertices, uvs, triangles);
        AddPartitionPanel(room, partitionAlongX, plane, gapEnd, crossHalf, height, vertices, uvs, triangles);

        Vector3 doorPos = partitionAlongX
            ? new Vector3(gapCenter, 0f, plane)
            : new Vector3(plane, 0f, gapCenter);
        // Дверь смотрит в сторону входа.
        Vector3 toEntrance = entranceAtPositive ? Vector3.back : Vector3.forward;
        if (!alongDepth) toEntrance = entranceAtPositive ? Vector3.left : Vector3.right;
        BuildInteriorDoor(room, doorPos, Quaternion.LookRotation(toEntrance, Vector3.up));
    }

    /// <summary>
    /// Внутренняя дверь уровня: рамка + полотно, открывается без генерации
    /// следующей комнаты (OneWayProceduralDoor.InteriorDoor).
    /// </summary>
    void BuildInteriorDoor(ProceduralRoomInstance room, Vector3 centerLocal, Quaternion rotation)
    {
        GameObject assembly = new GameObject("InteriorDoor");
        assembly.layer = 31;
        assembly.transform.SetParent(room.Root.transform, false);
        assembly.transform.localPosition = centerLocal;
        assembly.transform.localRotation = rotation;

        Material trimMat = GetDoorTrimMaterial();
        Material doorMat = GetDoorMaterial(room.Plan.WallTexture);
        float halfWidth = doorWidth * 0.5f;

        GameObject frame = new GameObject("DoorFrame");
        frame.layer = 31;
        frame.transform.SetParent(assembly.transform, false);
        CreateVisualCube(frame.transform, "Frame_Hinge", new Vector3(-halfWidth, doorHeight * 0.5f, 0f),
            new Vector3(0.13f, doorHeight, wallThickness * 1.2f), trimMat);
        CreateVisualCube(frame.transform, "Frame_Latch", new Vector3(halfWidth, doorHeight * 0.5f, 0f),
            new Vector3(0.13f, doorHeight, wallThickness * 1.2f), trimMat);
        CreateVisualCube(frame.transform, "Frame_Top", new Vector3(0f, doorHeight, 0f),
            new Vector3(doorWidth + 0.13f, 0.14f, wallThickness * 1.2f), trimMat);

        GameObject hinge = new GameObject("DoorLeafHinge");
        hinge.layer = 31;
        hinge.transform.SetParent(assembly.transform, false);
        hinge.transform.localPosition = new Vector3(-halfWidth, 0f, 0f);

        GameObject leafObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leafObject.name = "DoorLeaf";
        leafObject.layer = 31;
        leafObject.transform.SetParent(hinge.transform, false);
        leafObject.transform.localPosition = new Vector3(halfWidth, doorHeight * 0.5f, 0f);
        leafObject.transform.localScale = new Vector3(doorWidth - 0.09f, doorHeight - 0.06f, 0.13f);
        leafObject.GetComponent<Renderer>().sharedMaterial = doorMat;

        CreateVisualCube(leafObject.transform, "Handle",
            new Vector3(doorWidth * 0.5f - 0.19f, -doorHeight * 0.02f, -0.11f),
            new Vector3(0.045f, 0.12f, 0.055f), trimMat);

        OneWayProceduralDoor door = assembly.AddComponent<OneWayProceduralDoor>();
        door.InteriorDoor = true;
        door.Initialize(this, null, hinge.transform, room.Root.transform.TransformPoint(centerLocal), rotation * Vector3.forward);
        ConfigureDoorAudio(door, room.Plan);
        room.Doors.Add(door);
    }

    void AddPartitionPanel(ProceduralRoomInstance room, bool alongX, float offset,
        float from, float to, float height, List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
    {
        float span = to - from;
        if (span < 0.15f || height < 0.15f) return;

        Vector3 a, b, c, d;
        if (alongX)
        {
            a = new Vector3(from, 0f, offset);
            b = new Vector3(to, 0f, offset);
            c = new Vector3(to, height, offset);
            d = new Vector3(from, height, offset);
        }
        else
        {
            a = new Vector3(offset, 0f, from);
            b = new Vector3(offset, 0f, to);
            c = new Vector3(offset, height, to);
            d = new Vector3(offset, height, from);
        }

        // Двусторонность: два квада с противоположной намоткой — нормали в обе стороны.
        float tileScale = Mathf.Max(0.25f, textureMetersPerTile);
        AddQuad(vertices, uvs, triangles, a, b, c, d, span / tileScale, height / tileScale);
        AddQuad(vertices, uvs, triangles, b, a, d, c, span / tileScale, height / tileScale);

        // Физический коллайдер перегородки.
        float mid = (from + to) * 0.5f;
        Vector3 localCenter = alongX
            ? new Vector3(mid, height * 0.5f, offset)
            : new Vector3(offset, height * 0.5f, mid);
        Vector3 size = alongX
            ? new Vector3(span, height, wallThickness)
            : new Vector3(wallThickness, height, span);

        BoxCollider box = wallOcclusionRoot.gameObject.AddComponent<BoxCollider>();
        box.center = wallOcclusionRoot.InverseTransformPoint(room.Root.transform.TransformPoint(localCenter));
        box.size = size;
        box.isTrigger = false;
        room.WallColliders.Add(box);
    }

    // ------------------------------------------------------------------
    // Особые типы комнат: крыша / бассейн / лестница / метро / пустыня-луг.
    // ------------------------------------------------------------------

    Material _roofMat, _waterMat, _railMat, _rockMat, _tuftMat, _meadowFloorMat, _forestFloorMat, _desertFloorMat, _bermMat, _metroMat, _poolMat;

    Material GetRoofMaterial()
    {
        if (_roofMat == null)
        {
            Texture2D tex = FirstOf(roofTextures);
            if (tex == null) tex = PickFloorTexture(null);
            _roofMat = CreateMaterial("RoofMat_Tar", tex, new Color(0.24f, 0.24f, 0.27f, 1f));
        }
        return _roofMat;
    }

    Material GetWaterMaterial()
    {
        // Матовая тёмная вода: сильный глянец ловил блик фонаря во весь экран
        // и выжигал глаза (bloom поверх спекуляра = белая простыня).
        if (_waterMat == null)
        {
            _waterMat = CreateMaterial("WaterMat_", null, new Color(0.03f, 0.12f, 0.14f, 1f));
            if (_waterMat.HasProperty("_Smoothness")) _waterMat.SetFloat("_Smoothness", 0.3f);
        }
        return _waterMat;
    }

    Material GetRailMaterial()
    {
        if (_railMat == null) _railMat = CreateMaterial("RailMat_", null, new Color(0.17f, 0.17f, 0.19f, 1f));
        return _railMat;
    }

    Material GetRockMaterial()
    {
        if (_rockMat == null) _rockMat = CreateMaterial("RockMat_", null, new Color(0.5f, 0.5f, 0.53f, 1f));
        return _rockMat;
    }

    Material GetTuftMaterial()
    {
        if (_tuftMat == null) _tuftMat = CreateMaterial("GrassTuftMat_", null, new Color(0.22f, 0.42f, 0.16f, 1f));
        return _tuftMat;
    }

    Material GetMetroMaterial(ProceduralRoomPlan plan)
    {
        if (_metroMat == null)
        {
            Texture2D tex = FirstOf(metroTextures);
            if (tex == null) tex = plan.WallTexture;
            _metroMat = CreateMaterial("MetroMat_", tex, new Color(0.55f, 0.55f, 0.58f, 1f));
        }
        return _metroMat;
    }

    Material GetPoolMaterial(ProceduralRoomPlan plan)
    {
        if (_poolMat == null)
        {
            Texture2D tex = FirstOf(poolTextures);
            if (tex == null) tex = plan.WallTexture;
            _poolMat = CreateMaterial("PoolMat_Tile", tex, new Color(0.42f, 0.48f, 0.52f, 1f));
        }
        return _poolMat;
    }

    Material GetForestFloorMaterial()
    {
        if (_forestFloorMat == null)
        {
            Texture2D tex = FirstOf(meadowTextures);
            if (tex == null) tex = PickFloorTexture(null);
            _forestFloorMat = CreateMaterial("ForestGround_", tex, new Color(0.31f, 0.43f, 0.27f, 1f));
        }
        return _forestFloorMat;
    }

    Material GetOutdoorFloorMaterial(ProceduralRoomPlan plan)
    {
        // Чётный сид — луг, нечётный — пустыня (визуально различаются тинтом,
        // пока пользователь не добавит отдельных текстур).
        bool meadow = (plan.Seed & 1) == 0;
        if (meadow)
        {
            if (_meadowFloorMat == null)
            {
                Texture2D tex = FirstOf(meadowTextures);
                if (tex == null) tex = PickFloorTexture(null);
                _meadowFloorMat = CreateMaterial("MeadowFloor_", tex, new Color(0.36f, 0.5f, 0.3f, 1f));
            }
            return _meadowFloorMat;
        }
        if (_desertFloorMat == null)
        {
            Texture2D tex = FirstOf(desertTextures);
            if (tex == null) tex = PickFloorTexture(null);
            _desertFloorMat = CreateMaterial("DesertFloor_", tex, new Color(0.76f, 0.66f, 0.47f, 1f));
        }
        return _desertFloorMat;
    }

    Material GetOutdoorWallMaterial()
    {
        if (_bermMat == null) _bermMat = CreateMaterial("BermMat_", PickFloorTexture(null), new Color(0.3f, 0.28f, 0.22f, 1f));
        return _bermMat;
    }

    /// <summary>Прямоугольная горизонтальная плита пола/дна с опциональным коллайдером.</summary>
    GameObject BuildFloorRect(ProceduralRoomInstance room, string meshName, float y,
        float x0, float x1, float z0, float z1, Material material, bool withCollider)
    {
        float dx = x1 - x0, dz = z1 - z0;
        if (dx < 0.05f || dz < 0.05f) return null;
        var mesh = new Mesh { name = meshName };
        mesh.vertices = new[]
        {
            new Vector3(x0, y, z0), new Vector3(x1, y, z0),
            new Vector3(x1, y, z1), new Vector3(x0, y, z1)
        };
        float uvTiles = Mathf.Max(0.25f, textureMetersPerTile);
        mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(dx / uvTiles, 0f), new Vector2(dx / uvTiles, dz / uvTiles), new Vector2(0f, dz / uvTiles) };
        // Нормаль строго вверх: cross(c−a, b−a) = +Y при таком порядке индексов.
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateBounds();
        room.RuntimeMeshes.Add(mesh);

        var go = new GameObject(meshName);
        go.transform.SetParent(room.Root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        if (withCollider)
        {
            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3((x0 + x1) * 0.5f, y - 0.09f, (z0 + z1) * 0.5f);
            col.size = new Vector3(dx, 0.18f, dz);
        }
        return go;
    }

    /// <summary>
    /// Двусторонняя вертикальная квад-стена (стенки бассейна/ямы). 8 вершин с
    /// ЯВНЫМИ нормалями: RecalculateNormals на двух противоположных намотках
    /// одних и тех же вершин усреднял их в ~ноль, и освещение стенок
    /// дегенерировало в белые/чёрные артефакты.
    /// </summary>
    void BuildQuadMesh(ProceduralRoomInstance room, string name,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, Material material, Transform parent = null)
    {
        var mesh = new Mesh { name = name };
        Vector3 n = Vector3.Cross(b - a, c - a).normalized;
        if (n.sqrMagnitude < 0.5f) n = Vector3.up;
        mesh.vertices = new[] { a, b, c, d, a, b, c, d };
        mesh.normals = new[] { n, n, n, n, -n, -n, -n, -n };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
        mesh.RecalculateBounds();
        room.RuntimeMeshes.Add(mesh);
        var go = new GameObject(name);
        go.transform.SetParent(parent != null ? parent : room.Root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    void AddRoomBoxCollider(ProceduralRoomInstance room, Vector3 localCenter, Vector3 size)
    {
        BoxCollider box = wallOcclusionRoot.gameObject.AddComponent<BoxCollider>();
        box.center = wallOcclusionRoot.InverseTransformPoint(room.Root.transform.TransformPoint(localCenter));
        box.size = size;
        box.isTrigger = false;
        room.WallColliders.Add(box);
    }

    /// <summary>Низкий парапет/земляной вал по периметру (крыша, открытые пространства): дверные проёмы — сквозные.</summary>
    void BuildParapetWalls(ProceduralRoomInstance room, float height, Material material)
    {
        var vertices = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        AddWallForSide(room, ProceduralRoomSide.North, vertices, uvs, triangles, height);
        AddWallForSide(room, ProceduralRoomSide.East, vertices, uvs, triangles, height);
        AddWallForSide(room, ProceduralRoomSide.South, vertices, uvs, triangles, height);
        AddWallForSide(room, ProceduralRoomSide.West, vertices, uvs, triangles, height);

        var mesh = new Mesh { name = "Parapet_" + room.Plan.Seed };
        if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        room.RuntimeMeshes.Add(mesh);

        GameObject go = new GameObject("Parapet");
        go.layer = 31;
        go.transform.SetParent(room.Root.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    /// <summary>Пол зала с вырезом под бассейн (4 полосы вокруг прямоугольного проёма).</summary>
    void BuildPoolFloors(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        float w = plan.Width, d = plan.Depth;
        float pw = Mathf.Min(4f + (plan.Seed % 47) / 47f * 4f, w - 2.8f);
        float pd = Mathf.Min(3f + (plan.Seed % 31) / 31f * 3f, d - 2.8f);
        float px0 = -w * 0.5f + 1.4f + (plan.Seed % 17) / 17f * (w - 2.8f - pw);
        float pz0 = -d * 0.5f + 1.4f + (plan.Seed % 23) / 23f * (d - 2.8f - pd);
        room.PoolRect = new Vector4(px0, pz0, px0 + pw, pz0 + pd);

        Material floorMat = GetFloorMaterial(plan.FloorTexture);
        BuildFloorRect(room, "PoolFloor_N", 0f, -w * 0.5f, w * 0.5f, room.PoolRect.w, d * 0.5f, floorMat, false);
        BuildFloorRect(room, "PoolFloor_S", 0f, -w * 0.5f, w * 0.5f, -d * 0.5f, room.PoolRect.y, floorMat, false);
        BuildFloorRect(room, "PoolFloor_W", 0f, -w * 0.5f, room.PoolRect.x, room.PoolRect.y, room.PoolRect.w, floorMat, false);
        BuildFloorRect(room, "PoolFloor_E", 0f, room.PoolRect.z, w * 0.5f, room.PoolRect.y, room.PoolRect.w, floorMat, false);

        // Цельный коллайдер пола: бортик не даёт зайти в воду, вырез чисто визуальный.
        var col = new GameObject("PoolFloorCollider");
        col.transform.SetParent(room.Root.transform, false);
        var box = col.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, -0.09f, 0f);
        box.size = new Vector3(w, 0.18f, d);
    }

    /// <summary>Чаша бассейна: стенки, дно, вода и бортик с коллайдерами.</summary>
    void BuildPoolInterior(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        float px0 = room.PoolRect.x, pz0 = room.PoolRect.y, px1 = room.PoolRect.z, pz1 = room.PoolRect.w;
        float depth = 1.7f, waterY = -0.55f;
        Material floorMat = GetFloorMaterial(plan.FloorTexture);
        Material wallMat = GetPoolMaterial(plan);

        BuildQuadMesh(room, "PoolWall_N", new Vector3(px0, 0f, pz1), new Vector3(px1, 0f, pz1), new Vector3(px1, -depth, pz1), new Vector3(px0, -depth, pz1), wallMat);
        BuildQuadMesh(room, "PoolWall_S", new Vector3(px0, 0f, pz0), new Vector3(px1, 0f, pz0), new Vector3(px1, -depth, pz0), new Vector3(px0, -depth, pz0), wallMat);
        BuildQuadMesh(room, "PoolWall_W", new Vector3(px0, 0f, pz0), new Vector3(px0, 0f, pz1), new Vector3(px0, -depth, pz1), new Vector3(px0, -depth, pz0), wallMat);
        BuildQuadMesh(room, "PoolWall_E", new Vector3(px1, 0f, pz0), new Vector3(px1, 0f, pz1), new Vector3(px1, -depth, pz1), new Vector3(px1, -depth, pz0), wallMat);
        BuildFloorRect(room, "PoolBottom", -depth, px0, px1, pz0, pz1, floorMat, false);
        BuildFloorRect(room, "PoolWater", waterY, px0, px1, pz0, pz1, GetWaterMaterial(), false);

        // Бортик 0.4 м: stepOffset 0.35 его не перешагивает — в воду не провалиться.
        float midX = (px0 + px1) * 0.5f, midZ = (pz0 + pz1) * 0.5f;
        float pw = px1 - px0, pd = pz1 - pz0;
        CreateVisualCube(room.Root.transform, "PoolRim_N", new Vector3(midX, 0.2f, pz1 + 0.175f), new Vector3(pw + 0.7f, 0.4f, 0.35f), wallMat, true);
        CreateVisualCube(room.Root.transform, "PoolRim_S", new Vector3(midX, 0.2f, pz0 - 0.175f), new Vector3(pw + 0.7f, 0.4f, 0.35f), wallMat, true);
        CreateVisualCube(room.Root.transform, "PoolRim_W", new Vector3(px0 - 0.175f, 0.2f, midZ), new Vector3(0.35f, 0.4f, pd), wallMat, true);
        CreateVisualCube(room.Root.transform, "PoolRim_E", new Vector3(px1 + 0.175f, 0.2f, midZ), new Vector3(0.35f, 0.4f, pd), wallMat, true);
    }

    /// <summary>
    /// Лестница-многоэтажка (2 этажа): прямой широкий марш строго по центру —
    /// игрок входит и лестница прямо перед ним, её невозможно не найти.
    /// Подъём на второй этаж (3.2 м), наверху — мецанин-площадка с перилами
    /// во весь поперечник. Ступени — текстурированные боксы с UV, замасштаби-
    /// рованными под реальный размер (кубы-примитивы растягивали текстуру).
    /// </summary>
    void BuildStaircase(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        bool runAlongZ = plan.Depth >= plan.Width;
        Vector3 u = plan.IncomingSide == ProceduralRoomSide.North ? Vector3.back
                  : plan.IncomingSide == ProceduralRoomSide.South ? Vector3.forward
                  : plan.IncomingSide == ProceduralRoomSide.East ? Vector3.left : Vector3.right;

        float storeyH = 3.2f;
        int stepsPerFlight = 13;
        float rise = storeyH / stepsPerFlight; // ≈0.246
        float tread = 0.42f;
        float stairW = 3.2f;
        float margin = 1.5f;                   // отступ марша от входной стены
        float runHalf = (runAlongZ ? plan.Depth : plan.Width) * 0.5f;
        Material stairMat = GetFloorMaterial(plan.FloorTexture);

        Vector3 AxisScale(float along, float height, float across) =>
            new Vector3(runAlongZ ? across : along, height, runAlongZ ? along : across);

        // Марш строго по центру: от отступа от входной стены, подъём к дальнему торцу.
        float startU = -runHalf + margin;
        for (int i = 0; i < stepsPerFlight; i++)
        {
            float h = (i + 1) * rise;
            float uCoord = startU + tread * (i + 0.5f);
            Vector3 pos = u * uCoord + Vector3.up * (h * 0.5f);
            BuildTexturedBox(room, $"Stair_{i}", pos, AxisScale(tread, h, stairW), stairMat, true);
        }

        // Меццанин второго этажа: от конца марша до дальней стены, во весь поперечник.
        float slabNear = startU + flightLen();
        float slabFar = runHalf;
        float slabLen = slabFar - slabNear;
        if (slabLen > 0.3f)
        {
            Vector3 slabPos = u * ((slabNear + slabFar) * 0.5f) + Vector3.up * (storeyH - 0.125f);
            BuildTexturedBox(room, "Mezzanine", slabPos, AxisScale(slabLen, 0.25f, crossHalf(plan) * 2f), stairMat, true);

            // Перила: продольные по краям мецанина + два сегмента у внутреннего
            // края по бокам от схода лестницы (сам сход открыт).
            Vector3 v = runAlongZ ? Vector3.right : Vector3.forward;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 railPos = slabPos + v * (crossHalf(plan) - 0.06f) * side + Vector3.up * 0.45f;
                BuildTexturedBox(room, $"MezzRail_{(side > 0 ? "R" : "L")}", railPos,
                    AxisScale(slabLen, 0.9f, 0.08f), stairMat, true);
            }
            float stairHalf = stairW * 0.5f;
            float innerL0 = -crossHalf(plan) + 0.1f, innerL1 = -stairHalf - 0.1f;
            if (innerL1 - innerL0 > 0.2f)
                BuildTexturedBox(room, "MezzRail_InL",
                    slabPos + v * ((innerL0 + innerL1) * 0.5f) + u * (slabNear + 0.06f) + Vector3.up * 0.45f,
                    AxisScale(0.08f, 0.9f, innerL1 - innerL0), stairMat, true);
            float innerR0 = stairHalf + 0.1f, innerR1 = crossHalf(plan) - 0.1f;
            if (innerR1 - innerR0 > 0.2f)
                BuildTexturedBox(room, "MezzRail_InR",
                    slabPos + v * ((innerR0 + innerR1) * 0.5f) + u * (slabNear + 0.06f) + Vector3.up * 0.45f,
                    AxisScale(0.08f, 0.9f, innerR1 - innerR0), stairMat, true);
        }

        float flightLen() => stepsPerFlight * tread;
        float crossHalf(ProceduralRoomPlan p) => runAlongZ ? p.Width * 0.5f : p.Depth * 0.5f;
    }

    /// <summary>
    /// Бокс с UV каждой грани, замасштабированными под реальный размер грани
    /// (масштаб = textureMetersPerTile). В отличие от кубов-примитивов не
    /// растягивает текстуру на нестандартных пропорциях.
    /// </summary>
    void BuildTexturedBox(ProceduralRoomInstance room, string name, Vector3 center,
        Vector3 size, Material material, bool withCollider)
    {
        float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
        float tiles = Mathf.Max(0.25f, textureMetersPerTile);
        var mesh = new Mesh { name = name };
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        // Каждая грань: origin, du, dv подобраны так, что cross(du, dv) —
        // наружная нормаль (проверено), и квад лежит в границах бокса.
        void Face(Vector3 o, Vector3 du, Vector3 dv)
        {
            int b = verts.Count;
            verts.Add(o); verts.Add(o + du); verts.Add(o + du + dv); verts.Add(o + dv);
            Vector3 n = Vector3.Cross(du, dv).normalized;
            normals.Add(n); normals.Add(n); normals.Add(n); normals.Add(n);
            float lu = du.magnitude / tiles, lv = dv.magnitude / tiles;
            uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(lu, 0f));
            uvs.Add(new Vector2(lu, lv)); uvs.Add(new Vector2(0f, lv));
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        // top (+Y)
        Face(center + new Vector3(-hx, hy, -hz), new Vector3(0, 0, size.z), new Vector3(size.x, 0, 0));
        // bottom (−Y)
        Face(center + new Vector3(-hx, -hy, -hz), new Vector3(size.x, 0, 0), new Vector3(0, 0, size.z));
        // front (+Z)
        Face(center + new Vector3(-hx, -hy, hz), new Vector3(size.x, 0, 0), new Vector3(0, size.y, 0));
        // back (−Z)
        Face(center + new Vector3(-hx, -hy, -hz), new Vector3(0, size.y, 0), new Vector3(size.x, 0, 0));
        // right (+X)
        Face(center + new Vector3(hx, -hy, -hz), new Vector3(0, size.y, 0), new Vector3(0, 0, size.z));
        // left (−X)
        Face(center + new Vector3(-hx, -hy, -hz), new Vector3(0, 0, size.z), new Vector3(0, size.y, 0));

        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        room.RuntimeMeshes.Add(mesh);

        var go = new GameObject(name);
        go.transform.SetParent(room.Root.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        if (withCollider)
        {
            var col = go.AddComponent<BoxCollider>();
            col.center = center;
            col.size = size;
        }
    }

    /// <summary>Пол метро: платформа вдоль одной стены, яма с рельсами вдоль другой.</summary>
    void BuildMetroFloors(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        bool alongDepth = plan.Depth >= plan.Width;
        float runHalf = alongDepth ? plan.Depth * 0.5f : plan.Width * 0.5f;
        float crossHalf = alongDepth ? plan.Width * 0.5f : plan.Depth * 0.5f;
        float pitWidth = Mathf.Min(3.2f + (plan.Seed % 13) / 13f * 1.3f, crossHalf * 2f - 3.5f);
        float pitDepth = 1.25f;
        bool pitOnPositive = (plan.Seed & 1) == 0;
        Material floorMat = GetMetroMaterial(plan);

        float pitNear = pitOnPositive ? crossHalf - pitWidth : -crossHalf;
        float pitFar = pitOnPositive ? crossHalf : -crossHalf;

        // Платформа — поперечная полоса НЕ со стороны ямы.
        float platA = pitOnPositive ? -crossHalf : pitFar;
        float platB = pitOnPositive ? pitNear : crossHalf;
        float px0 = alongDepth ? platA : -runHalf;
        float px1 = alongDepth ? platB : runHalf;
        float pz0 = alongDepth ? -runHalf : platA;
        float pz1 = alongDepth ? runHalf : platB;
        BuildFloorRect(room, "MetroPlatform", 0f, px0, px1, pz0, pz1, floorMat, true);

        // Дно ямы.
        float bx0 = alongDepth ? pitNear : -runHalf;
        float bx1 = alongDepth ? pitFar : runHalf;
        float bz0 = alongDepth ? -runHalf : pitNear;
        float bz1 = alongDepth ? runHalf : pitFar;
        BuildFloorRect(room, "MetroPit", -pitDepth, bx0, bx1, bz0, bz1, floorMat, true);

        // Обычный потолок.
        Mesh ceilingMesh = CreatePlaneMesh(plan.Width, plan.Depth, plan.WallHeight, true, "MetroCeiling_" + plan.Seed);
        room.RuntimeMeshes.Add(ceilingMesh);
        GameObject ceilingObject = new GameObject("Ceiling");
        ceilingObject.transform.SetParent(room.Root.transform, false);
        ceilingObject.AddComponent<MeshFilter>().sharedMesh = ceilingMesh;
        ceilingObject.AddComponent<MeshRenderer>().sharedMaterial = GetCeilingMaterial(plan.CeilingTexture);

        // Стенки ямы (визуал) + коллайдеры дальнй и торцевых стен.
        Material wallMat = GetMetroMaterial(plan);
        if (alongDepth)
        {
            BuildQuadMesh(room, "PitFace_Drop", new Vector3(pitNear, 0f, -runHalf), new Vector3(pitNear, 0f, runHalf), new Vector3(pitNear, -pitDepth, runHalf), new Vector3(pitNear, -pitDepth, -runHalf), wallMat);
            BuildQuadMesh(room, "PitFace_Far", new Vector3(pitFar, 0f, -runHalf), new Vector3(pitFar, 0f, runHalf), new Vector3(pitFar, -pitDepth, runHalf), new Vector3(pitFar, -pitDepth, -runHalf), wallMat);
            BuildQuadMesh(room, "PitFace_EndA", new Vector3(pitNear, 0f, -runHalf), new Vector3(pitFar, 0f, -runHalf), new Vector3(pitFar, -pitDepth, -runHalf), new Vector3(pitNear, -pitDepth, -runHalf), wallMat);
            BuildQuadMesh(room, "PitFace_EndB", new Vector3(pitNear, 0f, runHalf), new Vector3(pitFar, 0f, runHalf), new Vector3(pitFar, -pitDepth, runHalf), new Vector3(pitNear, -pitDepth, runHalf), wallMat);
            AddRoomBoxCollider(room, new Vector3(pitFar - 0.1f, -pitDepth * 0.5f, 0f), new Vector3(0.2f, pitDepth + 0.2f, runHalf * 2f));
            AddRoomBoxCollider(room, new Vector3((pitNear + pitFar) * 0.5f, -pitDepth * 0.5f, -runHalf + 0.1f), new Vector3(pitWidth, pitDepth + 0.2f, 0.2f));
            AddRoomBoxCollider(room, new Vector3((pitNear + pitFar) * 0.5f, -pitDepth * 0.5f, runHalf - 0.1f), new Vector3(pitWidth, pitDepth + 0.2f, 0.2f));
        }
        else
        {
            BuildQuadMesh(room, "PitFace_Drop", new Vector3(-runHalf, 0f, pitNear), new Vector3(runHalf, 0f, pitNear), new Vector3(runHalf, -pitDepth, pitNear), new Vector3(-runHalf, -pitDepth, pitNear), wallMat);
            BuildQuadMesh(room, "PitFace_Far", new Vector3(-runHalf, 0f, pitFar), new Vector3(runHalf, 0f, pitFar), new Vector3(runHalf, -pitDepth, pitFar), new Vector3(-runHalf, -pitDepth, pitFar), wallMat);
            BuildQuadMesh(room, "PitFace_EndA", new Vector3(-runHalf, 0f, pitNear), new Vector3(-runHalf, 0f, pitFar), new Vector3(-runHalf, -pitDepth, pitFar), new Vector3(-runHalf, -pitDepth, pitNear), wallMat);
            BuildQuadMesh(room, "PitFace_EndB", new Vector3(runHalf, 0f, pitNear), new Vector3(runHalf, 0f, pitFar), new Vector3(runHalf, -pitDepth, pitFar), new Vector3(runHalf, -pitDepth, pitNear), wallMat);
            AddRoomBoxCollider(room, new Vector3(pitFar - 0.1f, -pitDepth * 0.5f, 0f), new Vector3(0.2f, pitDepth + 0.2f, crossHalf * 2f));
            AddRoomBoxCollider(room, new Vector3(-runHalf + 0.1f, -pitDepth * 0.5f, (pitNear + pitFar) * 0.5f), new Vector3(0.2f, pitDepth + 0.2f, pitWidth));
            AddRoomBoxCollider(room, new Vector3(runHalf - 0.1f, -pitDepth * 0.5f, (pitNear + pitFar) * 0.5f), new Vector3(0.2f, pitDepth + 0.2f, pitWidth));
        }
    }

    /// <summary>Рельсы в яме и колонны на платформе.</summary>
    void BuildMetroDetails(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        bool alongDepth = plan.Depth >= plan.Width;
        float runHalf = alongDepth ? plan.Depth * 0.5f : plan.Width * 0.5f;
        float crossHalf = alongDepth ? plan.Width * 0.5f : plan.Depth * 0.5f;
        float pitWidth = Mathf.Min(3.2f + (plan.Seed % 13) / 13f * 1.3f, crossHalf * 2f - 3.5f);
        bool pitOnPositive = (plan.Seed & 1) == 0;
        float pitDepth = 1.25f;
        float pitNear = pitOnPositive ? crossHalf - pitWidth : -crossHalf;
        float pitCenterCross = pitOnPositive ? crossHalf - pitWidth * 0.5f : -crossHalf + pitWidth * 0.5f;

        Vector3 AxisScale(float along, float height, float across) =>
            new Vector3(alongDepth ? across : along, height, alongDepth ? along : across);

        // Рельсы: две длинные полосы на дне ямы (декор).
        float railLen = runHalf * 2f - 0.6f;
        float railOff = 0.75f;
        Vector3 railPerp = alongDepth ? Vector3.right : Vector3.forward;
        for (int i = -1; i <= 1; i += 2)
        {
            Vector3 pos = (alongDepth
                ? new Vector3(pitCenterCross, 0f, 0f)
                : new Vector3(0f, 0f, pitCenterCross)) + railPerp * (railOff * i) + Vector3.up * (-1.25f + 0.09f);
            CreateVisualCube(room.Root.transform, $"Rail_{(i > 0 ? "E" : "W")}", pos, AxisScale(railLen, 0.09f, 0.14f), GetRailMaterial());
        }

        // Колонны вдоль края платформы.
        var prng = new System.Random(plan.Seed ^ unchecked((int)0x5bd1e995));
        int pillars = 3 + prng.Next(3);
        float pillarCross = pitOnPositive ? pitNear - 0.5f : pitNear + 0.5f;
        for (int i = 0; i < pillars; i++)
        {
            float t = -runHalf + 2.5f + (runHalf * 2f - 5f) * (i + 0.5f) / pillars;
            Vector3 pos = alongDepth ? new Vector3(pillarCross, plan.WallHeight * 0.5f, t)
                                     : new Vector3(t, plan.WallHeight * 0.5f, pillarCross);
            CreateVisualCube(room.Root.transform, $"Pillar_{i}", pos, AxisScale(0.45f, plan.WallHeight, 0.45f), GetMetroMaterial(plan), true);
        }

        // Лестница обратно из ямы: 5 ступеней по 0.25 у дальнего торца
        // (stepOffset 0.35 их перешагивает), наверху выход на платформу.
        Material pitStairMat = GetMetroMaterial(plan);
        for (int i = 0; i < 5; i++)
        {
            float h = (i + 1) * 0.25f;
            Vector3 pos = alongDepth
                ? new Vector3(pitCenterCross, -pitDepth + h * 0.5f, runHalf - 0.45f - (4 - i) * 0.35f)
                : new Vector3(runHalf - 0.45f - (4 - i) * 0.35f, -pitDepth + h * 0.5f, pitCenterCross);
            CreateVisualCube(room.Root.transform, $"PitStair_{i}", pos, AxisScale(0.35f, h, 1.6f), pitStairMat, true);
        }
    }

    /// <summary>Ящики-вентустановки на крыше.</summary>
    void BuildRoofProps(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        var prng = new System.Random(plan.Seed ^ unchecked((int)0x1f2e3d4c));
        int count = 2 + prng.Next(3);
        for (int i = 0; i < count; i++)
        {
            float sx = 1.2f + (float)prng.NextDouble() * 1.8f;
            float sy = 0.8f + (float)prng.NextDouble() * 1.4f;
            float sz = 1.0f + (float)prng.NextDouble() * 1.5f;
            float x = ((float)prng.NextDouble() * 2f - 1f) * Mathf.Max(0.2f, plan.Width * 0.5f - 1.6f - sx * 0.5f);
            float z = ((float)prng.NextDouble() * 2f - 1f) * Mathf.Max(0.2f, plan.Depth * 0.5f - 1.6f - sz * 0.5f);
            GameObject box = CreateVisualCube(room.Root.transform, $"RoofBox_{i}",
                new Vector3(x, sy * 0.5f, z), new Vector3(sx, sy, sz), GetWallMaterial(plan.WallTexture), true);
            box.transform.localRotation = Quaternion.Euler(0f, prng.Next(4) * 90f, 0f);
        }
    }

    /// <summary>Пустыня/луг: камни и (на лугу) кустики травы.</summary>
    /// <summary>
    /// Кабина лифта: створки из металла на поперечной оси (0.9 м от входной
    /// стены), тёплая лампочка под потолком, контроллер последовательности.
    /// Дверь в следующую комнату — на дальнем торце, скрыта створками до их
    /// открытия.
    /// </summary>
    void BuildElevatorInterior(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        bool runAlongZ = plan.Depth >= plan.Width;
        Vector3 u = plan.IncomingSide == ProceduralRoomSide.North ? Vector3.back
                  : plan.IncomingSide == ProceduralRoomSide.South ? Vector3.forward
                  : plan.IncomingSide == ProceduralRoomSide.East ? Vector3.left : Vector3.right;
        Vector3 slideAxis = runAlongZ ? Vector3.right : Vector3.forward; // поперечная ось
        float runHalf = (runAlongZ ? plan.Depth : plan.Width) * 0.5f;

        // Плоскость створок: 0.9 м внутрь комнаты от входной стены.
        float doorPlaneU = -runHalf + 0.9f;
        Vector3 panelCenter = u * doorPlaneU + Vector3.up * (doorHeight * 0.5f);
        float panelW = (runAlongZ ? plan.Width : plan.Depth) * 0.5f * 2f + 0.12f; // перекрывают весь поперечник

        Material metalMat = GetRailMaterial();
        Vector3 panelScale = runAlongZ
            ? new Vector3(panelW * 0.5f + 0.01f, doorHeight + 0.04f, 0.07f)
            : new Vector3(0.07f, doorHeight + 0.04f, panelW * 0.5f + 0.01f);

        // Створки: закрытые стыкуются в центре поперечника; открытые уезжают
        // за стены комнаты (визуальный «карман»).
        Vector3 closedL = panelCenter - slideAxis * (panelW * 0.25f);
        Vector3 closedR = panelCenter + slideAxis * (panelW * 0.25f);
        Vector3 openL = panelCenter - slideAxis * (panelW * 0.75f + 0.12f);
        Vector3 openR = panelCenter + slideAxis * (panelW * 0.75f + 0.12f);

        GameObject panelL = CreateVisualCube(room.Root.transform, "ElevDoor_L",
            closedL, panelScale, metalMat, true);
        GameObject panelR = CreateVisualCube(room.Root.transform, "ElevDoor_R",
            closedR, panelScale, metalMat, true);
        panelL.layer = 0; panelR.layer = 0;

        // Тёплая лампочка в потолке — иначе кабина мёртво-чёрная.
        var lampGo = new GameObject("ElevatorLamp");
        lampGo.transform.SetParent(room.Root.transform, false);
        lampGo.transform.localPosition = u * doorPlaneU + Vector3.up * (plan.WallHeight - 0.25f);
        var lamp = lampGo.AddComponent<Light>();
        lamp.type = LightType.Point;
        lamp.color = new Color(1f, 0.86f, 0.68f);
        lamp.intensity = 0.85f;
        lamp.range = 3.5f;
        lamp.shadows = LightShadows.None;

        var controller = room.Root.AddComponent<ElevatorRoomController>();
        controller.Setup(panelL.transform, panelR.transform, closedL, closedR, openL, openR,
            slideAxis, plan.Seed, this);
    }

    void BuildOutdoorProps(ProceduralRoomInstance room)
    {
        var plan = room.Plan;
        bool meadow = (plan.Seed & 1) == 0;
        var prng = new System.Random(plan.Seed ^ unchecked((int)0x2c1b3a4d));

        int rocks = meadow ? 3 + prng.Next(3) : 5 + prng.Next(4);
        for (int i = 0; i < rocks; i++)
        {
            float s = 0.35f + (float)prng.NextDouble() * 0.75f;
            float x = ((float)prng.NextDouble() * 2f - 1f) * Mathf.Max(0.2f, plan.Width * 0.5f - 1.4f - s * 0.5f);
            float z = ((float)prng.NextDouble() * 2f - 1f) * Mathf.Max(0.2f, plan.Depth * 0.5f - 1.4f - s * 0.5f);
            GameObject rock = CreateVisualCube(room.Root.transform, $"Rock_{i}",
                new Vector3(x, s * 0.35f, z), new Vector3(s, s * 0.7f, s), GetRockMaterial(), true);
            rock.transform.localRotation = Quaternion.Euler(0f, (float)prng.NextDouble() * 360f, 0f);
        }

        if (meadow)
        {
            int tufts = 10 + prng.Next(7);
            for (int i = 0; i < tufts; i++)
            {
                float x = ((float)prng.NextDouble() * 2f - 1f) * (plan.Width * 0.5f - 1.2f);
                float z = ((float)prng.NextDouble() * 2f - 1f) * (plan.Depth * 0.5f - 1.2f);
                GameObject tuft = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                tuft.name = $"GrassTuft_{i}";
                tuft.transform.SetParent(room.Root.transform, false);
                tuft.transform.localPosition = new Vector3(x, 0.15f, z);
                tuft.transform.localScale = new Vector3(0.3f, 0.55f, 0.3f);
                tuft.GetComponent<Renderer>().sharedMaterial = GetTuftMaterial();
                Collider col = tuft.GetComponent<Collider>();
                if (col != null)
                {
                    if (Application.isPlaying) Destroy(col);
                    else DestroyImmediate(col);
                }
            }
        }
    }

    void AddWallForSide(
        ProceduralRoomInstance room,
        ProceduralRoomSide side,
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<int> triangles,
        float topOverride = -1f)
    {
        float length = side == ProceduralRoomSide.North || side == ProceduralRoomSide.South
            ? room.Plan.Width : room.Plan.Depth;
        float half = length * 0.5f;
        var openings = new List<float>();
        foreach (ProceduralDoorSocket socket in room.Plan.Exits)
            if (socket.Side == side) openings.Add(socket.Along);
        if (room.Plan.HasIncomingSide && room.Plan.IncomingSide == side)
            openings.Add(0f);
        openings.Sort();

        // topOverride > 0 — парапет/вал: проёмы дверей становятся сквозными
        // (нижняя панель от 0 до высоты парапета, «наддверная» отсеётся сама).
        float top = topOverride > 0f ? topOverride : room.Plan.WallHeight;

        float cursor = -half;
        float halfDoor = doorWidth * 0.5f;
        foreach (float openingCenter in openings)
        {
            float openingStart = Mathf.Clamp(openingCenter - halfDoor, -half, half);
            float openingEnd = Mathf.Clamp(openingCenter + halfDoor, -half, half);
            AddWallPanel(room, side, cursor, openingStart, 0f, top, vertices, uvs, triangles);
            AddWallPanel(room, side, openingStart, openingEnd, doorHeight, top, vertices, uvs, triangles);
            cursor = Mathf.Max(cursor, openingEnd);
        }
        AddWallPanel(room, side, cursor, half, 0f, top, vertices, uvs, triangles);
    }

    void AddWallPanel(
        ProceduralRoomInstance room,
        ProceduralRoomSide side,
        float start,
        float end,
        float bottom,
        float top,
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<int> triangles)
    {
        float span = end - start;
        float height = top - bottom;
        if (span < 0.015f || height < 0.015f) return;

        Vector3 a, b, c, d;
        // Vertex order per side is chosen so that combined with AddQuad's
        // triangle winding every wall's recalculated normal points INTO the
        // room. The old mixed winding (outside-facing on North/South, inside-
        // facing on East/West) made those walls backfaces with NdotL < 0, so
        // no Unity light could illuminate them from inside the room.
        switch (side)
        {
            case ProceduralRoomSide.North:
                a = new Vector3(start, bottom, room.Plan.Depth * 0.5f);
                b = new Vector3(end, bottom, room.Plan.Depth * 0.5f);
                c = new Vector3(end, top, room.Plan.Depth * 0.5f);
                d = new Vector3(start, top, room.Plan.Depth * 0.5f);
                break;
            case ProceduralRoomSide.South:
                a = new Vector3(end, bottom, -room.Plan.Depth * 0.5f);
                b = new Vector3(start, bottom, -room.Plan.Depth * 0.5f);
                c = new Vector3(start, top, -room.Plan.Depth * 0.5f);
                d = new Vector3(end, top, -room.Plan.Depth * 0.5f);
                break;
            case ProceduralRoomSide.East:
                a = new Vector3(room.Plan.Width * 0.5f, bottom, end);
                b = new Vector3(room.Plan.Width * 0.5f, bottom, start);
                c = new Vector3(room.Plan.Width * 0.5f, top, start);
                d = new Vector3(room.Plan.Width * 0.5f, top, end);
                break;
            default: // West
                a = new Vector3(-room.Plan.Width * 0.5f, bottom, start);
                b = new Vector3(-room.Plan.Width * 0.5f, bottom, end);
                c = new Vector3(-room.Plan.Width * 0.5f, top, end);
                d = new Vector3(-room.Plan.Width * 0.5f, top, start);
                break;
        }

        float wallTiles = Mathf.Max(0.25f, textureMetersPerTile);
        AddQuad(vertices, uvs, triangles, a, b, c, d, span / wallTiles, height / wallTiles);
        AddWallCollider(room, side, start, end, bottom, top);
    }

    static void AddQuad(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles,
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, float uvWidth, float uvHeight)
    {
        int index = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        uvs.Add(new Vector2(0f, 0f));
        uvs.Add(new Vector2(uvWidth, 0f));
        uvs.Add(new Vector2(uvWidth, uvHeight));
        uvs.Add(new Vector2(0f, uvHeight));
        // Triangle winding must stay paired with the per-side vertex order in
        // AddWallPanel: together they make RecalculateNormals produce normals
        // pointing INTO the room so Unity lights illuminate walls from inside.
        triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 1);
        triangles.Add(index); triangles.Add(index + 3); triangles.Add(index + 2);
    }

    void AddWallCollider(ProceduralRoomInstance room, ProceduralRoomSide side,
        float start, float end, float bottom, float top)
    {
        float mid = (start + end) * 0.5f;
        float height = top - bottom;
        Vector3 localCenter;
        Vector3 size;
        switch (side)
        {
            case ProceduralRoomSide.North:
                localCenter = new Vector3(mid, (bottom + top) * 0.5f, room.Plan.Depth * 0.5f);
                size = new Vector3(end - start, height, wallThickness);
                break;
            case ProceduralRoomSide.South:
                localCenter = new Vector3(mid, (bottom + top) * 0.5f, -room.Plan.Depth * 0.5f);
                size = new Vector3(end - start, height, wallThickness);
                break;
            case ProceduralRoomSide.East:
                localCenter = new Vector3(room.Plan.Width * 0.5f, (bottom + top) * 0.5f, mid);
                size = new Vector3(wallThickness, height, end - start);
                break;
            default:
                localCenter = new Vector3(-room.Plan.Width * 0.5f, (bottom + top) * 0.5f, mid);
                size = new Vector3(wallThickness, height, end - start);
                break;
        }

        BoxCollider box = wallOcclusionRoot.gameObject.AddComponent<BoxCollider>();
        box.center = wallOcclusionRoot.InverseTransformPoint(room.Root.transform.TransformPoint(localCenter));
        box.size = size;
        box.isTrigger = false;
        room.WallColliders.Add(box);
    }

    void BuildShelterZone(ProceduralRoomInstance room)
    {
        GameObject zoneObject = new GameObject("IndoorShelterZone");
        zoneObject.transform.SetParent(room.Root.transform, false);
        BoxCollider box = zoneObject.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, room.Plan.WallHeight * 0.5f, 0f);
        box.size = new Vector3(room.Plan.Width + 0.08f, room.Plan.WallHeight + 0.25f, room.Plan.Depth + 0.08f);
        IndoorShelterZone zone = zoneObject.AddComponent<IndoorShelterZone>();
        zone.shelterFactor = 0.98f;
        // Do not reveal the whole room just because the player is indoors.
        // Visibility comes from the flashlight cone; without light the room stays dark.
        zone.fogRevealFactor = 0f;
        zone.fogEdgeFeather = 0.45f;
        zone.acousticMuffleFactor = 0.94f;
    }

    OneWayProceduralDoor BuildDoor(ProceduralRoomInstance room, ProceduralDoorSocket socket,
        ProceduralRoomPlan destinationPlan)
    {
        Vector3 normal = SideNormal(socket.Side);
        Quaternion rotation = Quaternion.LookRotation(normal, Vector3.up);
        Vector3 centerLocal = SocketLocalPosition(room.Plan, socket);

        // The assembly stays centred in the wall opening. The stationary frame
        // and the moving leaf are separate children, so only the leaf can swing.
        GameObject assembly = new GameObject("Door_" + socket.Side + "_" + socket.Along.ToString("F1"));
        assembly.layer = 31;
        assembly.transform.SetParent(room.Root.transform, false);
        assembly.transform.localPosition = centerLocal;
        assembly.transform.localRotation = rotation;

        Material trimMat = GetDoorTrimMaterial();
        Material doorMat = GetDoorMaterial(room.Plan.WallTexture);
        float halfWidth = doorWidth * 0.5f;

        GameObject frame = new GameObject("DoorFrame");
        frame.layer = 31;
        frame.transform.SetParent(assembly.transform, false);
        CreateVisualCube(frame.transform, "Frame_Hinge", new Vector3(-halfWidth, doorHeight * 0.5f, 0f),
            new Vector3(0.13f, doorHeight, wallThickness * 1.2f), trimMat);
        CreateVisualCube(frame.transform, "Frame_Latch", new Vector3(halfWidth, doorHeight * 0.5f, 0f),
            new Vector3(0.13f, doorHeight, wallThickness * 1.2f), trimMat);
        CreateVisualCube(frame.transform, "Frame_Top", new Vector3(0f, doorHeight, 0f),
            new Vector3(doorWidth + 0.13f, 0.14f, wallThickness * 1.2f), trimMat);

        // The pivot origin is placed exactly on the left hinge. The leaf mesh
        // extends along +X from that point instead of rotating around its centre.
        GameObject hinge = new GameObject("DoorLeafHinge");
        hinge.layer = 31;
        hinge.transform.SetParent(assembly.transform, false);
        hinge.transform.localPosition = new Vector3(-halfWidth, 0f, 0f);
        hinge.transform.localRotation = Quaternion.identity;

        GameObject leafObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        leafObject.name = "DoorLeaf";
        leafObject.layer = 31;
        leafObject.transform.SetParent(hinge.transform, false);
        leafObject.transform.localPosition = new Vector3(halfWidth, doorHeight * 0.5f, 0f);
        leafObject.transform.localScale = new Vector3(doorWidth - 0.09f, doorHeight - 0.06f, 0.13f);
        leafObject.GetComponent<Renderer>().sharedMaterial = doorMat;

        GameObject handle = CreateVisualCube(leafObject.transform, "Handle",
            new Vector3(doorWidth * 0.5f - 0.19f, -doorHeight * 0.02f, -0.11f),
            new Vector3(0.045f, 0.12f, 0.055f), trimMat);
        handle.transform.localRotation = Quaternion.Euler(0f, 0f, 15f);

        OneWayProceduralDoor door = assembly.AddComponent<OneWayProceduralDoor>();
        Vector3 openingWorld = room.Root.transform.TransformPoint(centerLocal);
        door.Initialize(this, destinationPlan, hinge.transform, openingWorld, normal);

        // A stationary invisible blocker is separate from the swinging leaf.
        // It is enabled only after the player capsule fully crosses the plane,
        // preventing a turn-back without the moving leaf shoving the player.
        GameObject blockerObject = new GameObject("OneWayDoorPassageBlocker");
        // Use Default physics layer, not the decorative door layer 31. Some scenes
        // exclude layer 31 from player collision, which made the blocker pass-through.
        blockerObject.layer = 0;
        blockerObject.transform.SetParent(assembly.transform, false);
        blockerObject.transform.localPosition = new Vector3(0f, doorHeight * 0.5f, 0f);
        blockerObject.transform.localRotation = Quaternion.identity;
        BoxCollider passageBlocker = blockerObject.AddComponent<BoxCollider>();
        passageBlocker.size = new Vector3(
            Mathf.Max(0.25f, doorWidth - 0.08f),
            Mathf.Max(0.5f, doorHeight - 0.08f),
            Mathf.Max(0.24f, wallThickness + 0.14f));
        passageBlocker.enabled = false;
        door.SetPassageBlocker(passageBlocker);

        ConfigureDoorAudio(door, room.Plan);
        return door;
    }

    static GameObject CreateVisualCube(Transform parent, string objectName, Vector3 localPosition,
        Vector3 localScale, Material material, bool keepCollider = false)
    {
        GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.name = objectName;
        obj.layer = parent.gameObject.layer;
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = localPosition;
        obj.transform.localScale = localScale;
        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer != null) renderer.sharedMaterial = material;
        Collider collider = obj.GetComponent<Collider>();
        if (collider != null && !keepCollider)
        {
            // ExecuteAlways строит комнату и в редакторе — там разрешён
            // только DestroyImmediate.
            if (Application.isPlaying) Destroy(collider);
            else DestroyImmediate(collider);
        }
        return obj;
    }

    void BuildArrivalTrigger(ProceduralRoomInstance room, OneWayProceduralDoor sourceDoor)
    {
        Vector3 outward = SideNormal(room.Plan.IncomingSide);
        Vector3 inward = -outward;
        Vector3 boundary = IncomingBoundaryLocalPosition(room.Plan);
        // The old 35 cm slab was easy to skip between physics steps. Make the
        // trigger a generous secondary signal; the swept plane test in Update
        // remains authoritative and validates the actual crossing direction.
        Vector3 localPosition = boundary + inward * 0.86f + Vector3.up * 1.08f;

        GameObject triggerObject = new GameObject("ArrivalTrigger");
        triggerObject.transform.SetParent(room.Root.transform, false);
        triggerObject.transform.localPosition = localPosition;
        BoxCollider trigger = triggerObject.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        float across = Mathf.Max(doorWidth + 0.75f, 1.8f);
        const float triggerHeight = 2.25f;
        const float triggerDepth = 1.65f;
        trigger.size = room.Plan.IncomingSide == ProceduralRoomSide.North || room.Plan.IncomingSide == ProceduralRoomSide.South
            ? new Vector3(across, triggerHeight, triggerDepth)
            : new Vector3(triggerDepth, triggerHeight, across);
        triggerObject.AddComponent<ProceduralDoorwayTrigger>().Initialize(this, sourceDoor, room);
    }

    public void TryOpenDoor(OneWayProceduralDoor door)
    {
        if (door == null || door.IsLocked || door.IsOpen) return;
        if (_pendingDoor != null)
        {
            if (_pendingDoor != door)
                Debug.Log("[ProceduralRooms] Сначала пройдите через уже открытую дверь.");
            return;
        }

        ProceduralRoomPlan plan = door.DestinationPlan;
        if (plan == null)
        {
            int seed = NextSeed();
            plan = CreateProceduralPlan(seed, ProceduralRoomSide.South);
        }

        Vector3 normal = door.OutwardNormal.normalized;
        if (plan.Special == RoomSpecial.Forest)
            LogForestPlayerPhysicsSnapshot("BEFORE_BUILD", null);

        if (plan.Special == RoomSpecial.Forest)
        {
            // IncomingSide is the destination wall facing BACK toward the source.
            // Reconcile it with the actual door normal before creating the landing
            // pad and invisible boundary gaps; floor raycasts alone cannot detect
            // a landing pad accidentally built on the opposite side of the room.
            plan.HasIncomingSide = true;
            plan.IncomingSide = SideFromNormal(-normal);
        }
        float halfExtent = Mathf.Abs(normal.z) > 0.5f ? plan.Depth * 0.5f : plan.Width * 0.5f;
        Vector3 targetCenter = door.OpeningCenterWorld + normal * halfExtent;
        _pendingRoom = BuildRoom(plan, targetCenter, door);
        if (_pendingRoom != null && _pendingRoom.Root != null && plan.Special == RoomSpecial.Forest)
        {
            // Reuse the generator's destination-side landing. Never create a
            // second runway that extends backward under the player in the source
            // room; BuildWorldSpaceForestEntranceRunway now only validates/reuses
            // the existing landing and creates a destination-only fallback if needed.
            BuildWorldSpaceForestEntranceRunway(_pendingRoom, door);
            Physics.SyncTransforms();
            LogForestPlayerPhysicsSnapshot("AFTER_BUILD_AND_SYNC", _pendingRoom);
        }
        if (_pendingRoom == null || _pendingRoom.Root == null)
        {
            _pendingRoom = null;
            Debug.LogError("[ProceduralRooms] Destination room build failed; source room and door were left untouched.", this);
            return;
        }

        // The project's player may use a custom layer whose collision matrix
        // ignores Default. Forest floors are generated at runtime, so explicitly
        // choose a layer that is guaranteed to collide with the CharacterController.
        // This is done before validation, portal setup, or opening the door.
        if (plan.Special == RoomSpecial.Forest && !AlignForestCollisionLayersToPlayer(_pendingRoom))
        {
            DestroyRoom(_pendingRoom);
            _pendingRoom = null;
            Debug.LogError("[Forest] No collision-compatible layer exists for the player. Door kept closed; current room preserved.", this);
            return;
        }

        // Finish generated transforms and synchronise PhysX before the door becomes
        // traversable. Forest rooms get explicit TerrainCollider/runway validation.
        Physics.SyncTransforms();
        if (plan.Special == RoomSpecial.Forest && !ValidateForestEntranceSupport(_pendingRoom, door))
        {
            DestroyRoom(_pendingRoom);
            _pendingRoom = null;
            Debug.LogError("[ProceduralRooms] Forest entrance has no confirmed solid floor. Door kept closed; active room was not unloaded.", this);
            return;
        }

        SetRoomPortalLayer(_pendingRoom, true);
        if (!BuildPortalWindow(_pendingRoom, door))
        {
            SetRoomPortalLayer(_pendingRoom, false);
            DestroyRoom(_pendingRoom);
            _pendingRoom = null;
            Debug.LogError("[ProceduralRooms] Door portal could not be created; door remains closed.", this);
            return;
        }
        _pendingDoor = door;
        _pendingLastPlayerPosition = playerRoot != null ? playerRoot.position : Vector3.zero;
        _pendingPlayerSampleValid = playerRoot != null;
        _pendingDoorPlaneCrossed = false;
        _pendingRoomRevealedToMainCamera = false;
        _pendingGroundedClearTime = 0f;
        _pendingSupportWarningLogged = false;
        Physics.SyncTransforms();
        if (plan.Special == RoomSpecial.Forest)
            LogForestPlayerPhysicsSnapshot("IMMEDIATELY_BEFORE_DOOR_OPEN", _pendingRoom);
        door.Open();
    }

    void LogForestPlayerPhysicsSnapshot(string stage, ProceduralRoomInstance room)
    {
        CharacterController cc = ResolvePlayerCharacterController();
        if (playerRoot == null)
        {
            Debug.LogWarning($"[Forest PLAYER SNAPSHOT] {stage}: playerRoot=MISSING.", this);
            return;
        }

        Vector3 playerPosition = playerRoot.position;
        Bounds bounds = cc != null
            ? cc.bounds
            : new Bounds(playerPosition + Vector3.up * 0.9f, new Vector3(0.62f, 1.8f, 0.62f));

        string controllerState = cc != null
            ? $"enabled={cc.enabled},detect={cc.detectCollisions},grounded={cc.isGrounded}," +
              $"height={cc.height:0.000},radius={cc.radius:0.000},center={cc.center},layer={cc.gameObject.layer}"
            : "CharacterController=MISSING";

        string parentName = playerRoot.parent != null ? playerRoot.parent.name : "<root>";
        Debug.Log(
            $"[Forest PLAYER SNAPSHOT] {stage}: pos={playerPosition}, parent={parentName}, " +
            $"bounds={bounds}, {controllerState}, pendingRoom={(room != null && room.Root != null ? room.Root.name : "<none>")}.",
            playerRoot);

        if (cc != null && cc.enabled)
        {
            Vector3 scale = cc.transform.lossyScale;
            float radius = cc.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float height = Mathf.Max(cc.height * Mathf.Abs(scale.y), radius * 2f);
            Vector3 capsuleCenter = cc.transform.TransformPoint(cc.center);
            Vector3 up = cc.transform.up;
            float halfSegment = Mathf.Max(0f, height * 0.5f - radius);
            Vector3 point1 = capsuleCenter + up * halfSegment;
            Vector3 point2 = capsuleCenter - up * halfSegment;

            Collider[] overlaps = Physics.OverlapCapsule(
                point1, point2, radius, ~0, QueryTriggerInteraction.Ignore);
            int logged = 0;
            for (int i = 0; i < overlaps.Length && logged < 32; i++)
            {
                Collider other = overlaps[i];
                if (other == null || other == cc || other.transform == cc.transform ||
                    other.transform.IsChildOf(cc.transform))
                    continue;

                bool belongsToPendingRoom = room != null && room.Root != null &&
                    (other.transform == room.Root.transform || other.transform.IsChildOf(room.Root.transform));

                Vector3 penetrationDirection = Vector3.zero;
                float penetrationDistance = 0f;
                bool penetration = false;
                try
                {
                    penetration = Physics.ComputePenetration(
                        cc, cc.transform.position, cc.transform.rotation,
                        other, other.transform.position, other.transform.rotation,
                        out penetrationDirection, out penetrationDistance);
                }
                catch (System.Exception)
                {
                    // Some collider pairs are not supported by ComputePenetration;
                    // the overlap itself is still useful diagnostic information.
                }

                Debug.Log(
                    $"[Forest CAPSULE OVERLAP] stage={stage}, pendingRoom={belongsToPendingRoom}, " +
                    $"object='{other.name}', type={other.GetType().Name}, layer={other.gameObject.layer}, " +
                    $"trigger={other.isTrigger}, bounds={other.bounds}, " +
                    $"penetration={penetration}, direction={penetrationDirection}, depth={penetrationDistance:0.000}.",
                    other);
                logged++;
            }

            if (logged == 0)
                Debug.Log($"[Forest CAPSULE OVERLAP] stage={stage}: no solid collider overlaps the player capsule.", playerRoot);
            else if (overlaps.Length > logged)
                Debug.LogWarning($"[Forest CAPSULE OVERLAP] stage={stage}: logged {logged} overlaps; total query results={overlaps.Length}.", playerRoot);
        }

        Vector3 feet = bounds.center - Vector3.up * bounds.extents.y;
        RaycastHit[] groundHits = Physics.RaycastAll(
            feet + Vector3.up * 0.35f, Vector3.down, 1.5f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        Array.Sort(groundHits, (a, b) => a.distance.CompareTo(b.distance));
        int hitLimit = Mathf.Min(groundHits.Length, 8);
        for (int i = 0; i < hitLimit; i++)
        {
            RaycastHit hit = groundHits[i];
            Debug.Log(
                $"[Forest FOOT RAY] stage={stage}, hit='{hit.collider.name}', " +
                $"type={hit.collider.GetType().Name}, distance={hit.distance:0.000}, " +
                $"normal={hit.normal}, layer={hit.collider.gameObject.layer}.",
                hit.collider);
        }

        PlayerCharacterController[] legacyPlayers =
            FindObjectsByType<PlayerCharacterController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < legacyPlayers.Length; i++)
        {
            PlayerCharacterController legacy = legacyPlayers[i];
            if (legacy == null) continue;
            Debug.Log(
                $"[Forest LEGACY DRIVER] PlayerCharacterController='{legacy.name}', " +
                $"active={legacy.isActiveAndEnabled}, target={(legacy.character != null ? legacy.character.name : "<none>")}, " +
                $"sameRoot={legacy.character == playerRoot}.",
                legacy);
        }

        CameraFollowController[] followers =
            FindObjectsByType<CameraFollowController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < followers.Length; i++)
        {
            CameraFollowController follower = followers[i];
            if (follower == null) continue;
            Debug.Log(
                $"[Forest LEGACY DRIVER] CameraFollowController='{follower.name}', " +
                $"active={follower.isActiveAndEnabled}, target={(follower.target != null ? follower.target.name : "<auto>")}, " +
                $"onPlayerCamera={(playerCamera != null && follower.transform == playerCamera.transform)}.",
                follower);
        }
    }

    void BuildWorldSpaceForestEntranceRunway(ProceduralRoomInstance room, OneWayProceduralDoor door)
    {
        if (room == null || room.Root == null || door == null) return;

        // ForestRoomGenerator already creates a destination-side landing whose
        // near edge is aligned with the incoming wall. Reuse it. Do NOT create a
        // second box that extends backwards into the source room: that box can
        // spawn around the player's CharacterController and PhysX may resolve the
        // initial overlap through the underside, snapping the player several metres
        // down (the exact failure captured in [Forest CONTROLLER HIT]).
        if (room.ForestEntranceLanding != null)
        {
            room.ForestEntranceLanding.enabled = true;
            Debug.Log(
                $"[Forest] Using generator-owned destination landing only: " +
                $"name={room.ForestEntranceLanding.name}, bounds={room.ForestEntranceLanding.bounds}. " +
                "No source-side runway will be spawned around the player.",
                room.ForestEntranceLanding);
            return;
        }

        Transform existing = room.Root.transform.Find("ForestEntranceLandingCollider");
        if (existing != null)
        {
            BoxCollider landing = existing.GetComponent<BoxCollider>();
            if (landing != null)
            {
                landing.enabled = true;
                room.ForestEntranceLanding = landing;
                Debug.Log(
                    $"[Forest] Reusing destination landing only: name={landing.name}, bounds={landing.bounds}.",
                    landing);
                return;
            }
        }

        // Last-resort landing if the generator did not provide one. It begins
        // 0.40 m INSIDE the destination, leaving the currently occupied source
        // room completely untouched. The source room's own floor supports the
        // player until the capsule crosses the threshold.
        Vector3 normal = door.OutwardNormal;
        normal.y = 0f;
        if (normal.sqrMagnitude < 0.0001f) normal = Vector3.forward;
        normal.Normalize();

        const float destinationInset = 0.40f;
        const float destinationDepth = 6.0f;
        const float thickness = 0.32f;
        const float topOffset = 0.0f;
        float width = Mathf.Max(doorWidth + 2.0f, 4.2f);
        Vector3 threshold = door.OpeningCenterWorld;
        Vector3 center = threshold + normal * (destinationInset + destinationDepth * 0.5f)
            + Vector3.up * (topOffset - thickness * 0.5f);

        GameObject runway = new GameObject("ForestDoorEntranceSafetyCollider");
        runway.layer = room.Root.gameObject.layer;
        runway.transform.SetPositionAndRotation(center, Quaternion.LookRotation(normal, Vector3.up));
        runway.transform.SetParent(room.Root.transform, true);
        BoxCollider box = runway.AddComponent<BoxCollider>();
        box.center = Vector3.zero;
        box.size = new Vector3(width, thickness, destinationDepth);
        box.isTrigger = false;
        box.enabled = true;
        box.contactOffset = 0.02f;
        room.ForestEntranceLanding = box;

        Physics.SyncTransforms();
        Debug.Log(
            $"[Forest] Destination-only emergency landing created: threshold={threshold}, " +
            $"normal={normal}, bounds={box.bounds}. Source-side player volume is not covered.",
            runway);
    }

    bool AlignForestCollisionLayersToPlayer(ProceduralRoomInstance room)
    {
        if (room == null || room.Root == null) return false;
        CharacterController player = ResolvePlayerCharacterController();
        int playerLayer = player != null ? player.gameObject.layer : (playerRoot != null ? playerRoot.gameObject.layer : 0);
        int collisionLayer = -1;

        // Prefer Default when it is valid; otherwise find the first layer which
        // the player's layer is configured to collide with.
        if (!Physics.GetIgnoreLayerCollision(playerLayer, 0)) collisionLayer = 0;
        else
        {
            for (int layer = 0; layer < 32; layer++)
            {
                if (!Physics.GetIgnoreLayerCollision(playerLayer, layer))
                {
                    collisionLayer = layer;
                    break;
                }
            }
        }

        if (collisionLayer < 0)
        {
            Debug.LogError($"[Forest] Player layer {playerLayer} ignores collisions with every layer. Cannot guarantee forest floor contact; door must remain closed.", room.Root);
            return false;
        }

        Collider[] colliders = room.Root.GetComponentsInChildren<Collider>(true);
        int changed = 0;
        int checkedFloorColliders = 0;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider = colliders[i];
            if (collider == null) continue;

            // Keep door leaves and unrelated prop colliders on their authored
            // layers. Only the actual ground/support/boundary colliders need to
            // be guaranteed compatible with the player's CharacterController.
            Transform ancestor = collider.transform;
            bool isForestPhysics = false;
            while (ancestor != null && ancestor != room.Root.transform)
            {
                string n = ancestor.name;
                if (n == "ForestTerrainCollision" || n == "ForestTerrainSupportGrid" ||
                    n == "ForestTerrainSafetyCatch" || n == "ForestEntranceLandingCollider" ||
                    n == "ForestDoorEntranceSafetyCollider" || n == "InvisibleForestBoundaries")
                {
                    isForestPhysics = true;
                    break;
                }
                ancestor = ancestor.parent;
            }
            if (!isForestPhysics) continue;

            checkedFloorColliders++;
            if (collider.gameObject.layer != collisionLayer)
            {
                collider.gameObject.layer = collisionLayer;
                changed++;
            }
            collider.enabled = true;
        }

        if (player != null) player.detectCollisions = true;
        Debug.Log($"[Forest] Collision layers aligned: playerLayer={playerLayer}, floorLayer={collisionLayer}, " +
            $"playerIgnoresFloor={Physics.GetIgnoreLayerCollision(playerLayer, collisionLayer)}, " +
            $"floorColliders={checkedFloorColliders}, changed={changed}, CharacterController=" +
            (player != null ? $"enabled:{player.enabled}/detect:{player.detectCollisions}" : "MISSING") + ".", room.Root);
        return !Physics.GetIgnoreLayerCollision(playerLayer, collisionLayer);
    }

    bool ValidateForestEntranceSupport(ProceduralRoomInstance room, OneWayProceduralDoor door)
    {
        if (room == null || room.Root == null || door == null) return false;

        if (forestGenerator == null || !forestGenerator.IsTerrainPhysicsReadyForRoom(room.Root.transform))
        {
            Debug.LogError("[Forest] TerrainCollider or backup support grid did not pass its probe test. Refusing to open this door.", room.Root);
            return false;
        }

        Vector3 normal = door.OutwardNormal.normalized;
        Vector3 sample = door.OpeningCenterWorld + normal * 1.35f;
        bool terrainHit = HasDestinationFloorAt(room, sample);
        bool landingHit = HasSpecificFloorAt(room.ForestEntranceLanding, sample);
        // The player is still standing in the SOURCE room when the door is opened.
        // Validate support against the currently active room, not by extending a
        // destination collider underneath the player. The latter caused a real
        // underside collision and a 2.25 m downward snap in the supplied trace.
        bool playerSideHit = HasCurrentPlayerSourceSupport(room);
        if (!playerSideHit)
        {
            Debug.LogError(
                "[Forest] Current player is not grounded on a source-room floor. " +
                "Refusing to open the door; no source-side safety box will be spawned.",
                room.Root);
            return false;
        }
        if (terrainHit && landingHit)
        {
            Debug.Log("[Forest] Entrance physics validated: forest terrain and destination landing are ready; player remains supported by the source-room floor until crossing.", room.Root);
            return true;
        }
        Debug.LogWarning($"[Forest] Entrance checks FAILED: terrainHit={terrainHit}, destinationRunwayHit={landingHit}, " +
            $"playerSideRunwayHit={playerSideHit}, playerPos={(playerRoot != null ? playerRoot.position.ToString() : "MISSING")}, " +
            $"sample={sample}, incoming={room.Plan.IncomingSide}, normal={normal}, runway=" +
            (room.ForestEntranceLanding != null ? room.ForestEntranceLanding.bounds.ToString() : "MISSING") + ".", room.Root);

        // Fallback strip is placed inside the destination, starting at the
        // threshold. It is invisible and collider-only; it cannot change the
        // renderer/portal layers and cannot push the player from the source side.
        GameObject safety = new GameObject("ForestDoorEntranceSafetyCollider");
        safety.layer = 0;
        safety.transform.SetParent(room.Root.transform, false);
        Vector3 local = room.Root.transform.InverseTransformPoint(sample);
        safety.transform.localPosition = new Vector3(local.x, 0f, local.z);
        BoxCollider box = safety.AddComponent<BoxCollider>();
        room.ForestEntranceLanding = box;
        bool alongZ = Mathf.Abs(normal.z) > 0.5f;
        const float runwayThickness = 0.24f;
        box.center = new Vector3(0f, 0.025f - runwayThickness * 0.5f, 0f);
        box.size = alongZ
            ? new Vector3(doorWidth + 1.5f, runwayThickness, 3.2f)
            : new Vector3(3.2f, runwayThickness, doorWidth + 1.5f);
        box.isTrigger = false;
        box.enabled = true;

        Physics.SyncTransforms();
        if (HasDestinationFloorAt(room, sample) && HasSpecificFloorAt(box, sample))
        {
            Debug.LogWarning("[Forest] Emergency entrance floor created and validated before opening the door.", safety);
            return true;
        }

        if (Application.isPlaying) Destroy(safety); else DestroyImmediate(safety);
        Physics.SyncTransforms();
        return false;
    }

    bool HasCurrentPlayerSourceSupport(ProceduralRoomInstance destinationRoom)
    {
        CharacterController player = ResolvePlayerCharacterController();
        if (player == null || !player.enabled || !player.detectCollisions || !player.isGrounded)
            return false;

        Bounds bounds = player.bounds;
        Vector3 origin = new Vector3(
            bounds.center.x,
            bounds.min.y + Mathf.Max(0.12f, player.skinWidth + 0.08f),
            bounds.center.z);
        RaycastHit[] hits = Physics.RaycastAll(
            origin, Vector3.down, 0.85f, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            Collider hitCollider = hits[i].collider;
            if (hitCollider == null || hitCollider.isTrigger || hits[i].normal.y < 0.55f)
                continue;

            // A destination collider must never certify support for a player who
            // has not crossed the doorway yet.
            if (destinationRoom != null && destinationRoom.Root != null &&
                (hitCollider.transform == destinationRoom.Root.transform ||
                 hitCollider.transform.IsChildOf(destinationRoom.Root.transform)))
                continue;

            return true;
        }

        return false;
    }

    static bool HasSpecificFloorAt(Collider floorCollider, Vector3 worldSample)
    {
        if (floorCollider == null || !floorCollider.enabled || floorCollider.isTrigger) return false;
        Vector3 origin = worldSample + Vector3.up * 2.2f;
        return floorCollider.Raycast(new Ray(origin, Vector3.down), out RaycastHit hit, 3.2f) && hit.normal.y >= 0.55f;
    }

    bool HasDestinationFloorAt(ProceduralRoomInstance room, Vector3 worldSample)
    {
        Vector3 origin = worldSample + Vector3.up * 2.2f;
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 3.2f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float bestDistance = float.PositiveInfinity;
        Collider bestCollider = null;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider candidate = hits[i].collider;
            if (candidate == null || candidate.isTrigger) continue;
            Transform t = candidate.transform;
            if (t != room.Root.transform && !t.IsChildOf(room.Root.transform)) continue;
            if (hits[i].normal.y < 0.55f) continue;
            if (hits[i].distance < bestDistance)
            {
                bestDistance = hits[i].distance;
                bestCollider = candidate;
            }
        }
        if (bestCollider != null)
        {
            Debug.Log($"[Forest] Entrance support hit '{bestCollider.name}' at {bestDistance:0.000}m.", bestCollider);
            return true;
        }
        Debug.LogWarning("[Forest] No destination collider detected beneath the entrance sample.", room.Root);
        return false;
    }

    // Keep the original public void API for any scene/editor integrations that
    // may still call it. The trigger uses the bool variant so it can retry until
    // the player's transform has actually crossed the plane.
    public void NotifyPlayerEnteredRoom(OneWayProceduralDoor sourceDoor, ProceduralRoomInstance destinationRoom)
    {
        TryNotifyPlayerEnteredRoom(sourceDoor, destinationRoom);
    }

    public bool TryNotifyPlayerEnteredRoom(OneWayProceduralDoor sourceDoor, ProceduralRoomInstance destinationRoom)
    {
        if (sourceDoor == null || destinationRoom == null || playerRoot == null) return false;
        if (sourceDoor != _pendingDoor || destinationRoom != _pendingRoom) return false;

        // The trigger can be entered by the leading edge of the capsule before
        // the player's transform crosses the plane. Return false in that case so
        // ProceduralDoorwayTrigger keeps checking with OnTriggerStay. Once the
        // transform is across the opening, latch the signal permanently.
        if (IsPlayerPastDoorOpening(sourceDoor, 0.05f))
        {
            _pendingDoorPlaneCrossed = true;
            return true;
        }
        return _pendingDoorPlaneCrossed;
    }

    void CommitPendingTransition()
    {
        if (_pendingDoor == null || _pendingRoom == null || playerRoot == null) return;
        if (!IsPlayerFullyClearOfDoorway(_pendingDoor) ||
            !IsPlayerDeepInsideDestination(_pendingDoor) ||
            !IsPlayerSupportedByRoom(_pendingRoom))
            return;

        ProceduralRoomInstance previousRoom = _activeRoom;
        ProceduralRoomInstance destinationRoom = _pendingRoom;
        OneWayProceduralDoor sourceDoor = _pendingDoor;

        Debug.Log("[ProceduralRooms] Door crossing committed: player capsule is clear AND supported by destination floor.", this);
        FinishPortalEntry(destinationRoom);

        // Close/lock only after the player is safely standing on the destination
        // floor. The stationary blocker is on Default layer and cannot shove them.
        sourceDoor.CommitAsOneWayEntrance(destinationRoom.Root.transform);
        _activeRoom = destinationRoom;
        _pendingDoor = null;
        _pendingRoom = null;
        _pendingPlayerSampleValid = false;
        _pendingDoorPlaneCrossed = false;
        _pendingRoomRevealedToMainCamera = false;
        _pendingGroundedClearTime = 0f;
        _pendingSupportWarningLogged = false;

        DestroyRoom(previousRoom);
        ApplyRoomAtmosphere(destinationRoom.Plan);
        // Кабина лифта: запускаем последовательность после закрытия входной двери.
        if (destinationRoom.Plan.Special == RoomSpecial.Elevator)
            destinationRoom.Root.GetComponent<ElevatorRoomController>()?.BeginSequence();
    }

    const int PortalLayer = 30; // Destination visuals are visible only to the portal camera while pending.
    const int PortalWindowLayer = 28; // Квад-окно: исключается из рендера портал-камеры (иначе она видит сама себя).

    /// <summary>Moves visuals to the portal-only layer without ever moving physical colliders.</summary>
    void SetRoomPortalLayer(ProceduralRoomInstance room, bool portal)
    {
        if (room == null || room.Root == null) return;
        if (room.Renderers.Count == 0)
            room.Renderers.AddRange(room.Root.GetComponentsInChildren<Renderer>(true));

        Camera cam = room.PortalMainCamera != null
            ? room.PortalMainCamera
            : (playerCamera != null ? playerCamera : Camera.main);

        if (portal)
        {
            if (cam != null && !room.PortalCullingMaskSaved)
            {
                room.PortalMainCamera = cam;
                room.PortalOriginalCullingMask = cam.cullingMask;
                room.PortalCullingMaskSaved = true;
            }

            room.OriginalRendererLayers.Clear();
            room.OriginalRendererEnabled.Clear();
            room.PortalVisualProxies.Clear();

            foreach (Renderer renderer in room.Renderers)
            {
                if (renderer == null) continue;
                GameObject renderObject = renderer.gameObject;
                Collider[] sameObjectColliders = renderObject.GetComponents<Collider>();
                if (sameObjectColliders.Length > 0)
                {
                    // A collider and renderer share this object (common for floors/walls).
                    // Do not move the object to layer 30: that could remove player collision.
                    // Instead, mirror only its visual component on a collider-free child.
                    room.OriginalRendererEnabled[renderer] = renderer.enabled;
                    if (renderer.enabled && CreatePortalVisualProxy(room, renderer))
                        renderer.enabled = false;
                    continue;
                }

                if (!room.OriginalRendererLayers.ContainsKey(renderObject))
                    room.OriginalRendererLayers.Add(renderObject, renderObject.layer);
                renderObject.layer = PortalLayer;
            }

            if (cam != null) cam.cullingMask &= ~(1 << PortalLayer);
        }
        else
        {
            foreach (KeyValuePair<GameObject, int> entry in room.OriginalRendererLayers)
                if (entry.Key != null) entry.Key.layer = entry.Value;
            room.OriginalRendererLayers.Clear();

            foreach (KeyValuePair<Renderer, bool> entry in room.OriginalRendererEnabled)
                if (entry.Key != null) entry.Key.enabled = entry.Value;
            room.OriginalRendererEnabled.Clear();

            for (int i = 0; i < room.PortalVisualProxies.Count; i++)
            {
                GameObject proxy = room.PortalVisualProxies[i];
                if (proxy == null) continue;
                proxy.SetActive(false);
                if (Application.isPlaying) Destroy(proxy);
                else DestroyImmediate(proxy);
            }
            room.PortalVisualProxies.Clear();

            if (room.PortalCullingMaskSaved && room.PortalMainCamera != null)
                room.PortalMainCamera.cullingMask = room.PortalOriginalCullingMask;
            room.PortalCullingMaskSaved = false;
            room.PortalMainCamera = null;
        }
    }

    bool CreatePortalVisualProxy(ProceduralRoomInstance room, Renderer source)
    {
        if (room == null || source == null) return false;
        GameObject proxy = new GameObject("PortalVisual_" + source.gameObject.name);
        proxy.layer = PortalLayer;
        proxy.transform.SetParent(source.transform, false);
        proxy.transform.localPosition = Vector3.zero;
        proxy.transform.localRotation = Quaternion.identity;
        proxy.transform.localScale = Vector3.one;

        Renderer proxyRenderer = null;
        MeshRenderer sourceMeshRenderer = source as MeshRenderer;
        MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
        if (sourceMeshRenderer != null && sourceFilter != null && sourceFilter.sharedMesh != null)
        {
            proxy.AddComponent<MeshFilter>().sharedMesh = sourceFilter.sharedMesh;
            MeshRenderer target = proxy.AddComponent<MeshRenderer>();
            target.sharedMaterials = sourceMeshRenderer.sharedMaterials;
            proxyRenderer = target;
        }
        else
        {
            SkinnedMeshRenderer sourceSkinned = source as SkinnedMeshRenderer;
            if (sourceSkinned != null && sourceSkinned.sharedMesh != null)
            {
                SkinnedMeshRenderer target = proxy.AddComponent<SkinnedMeshRenderer>();
                target.sharedMesh = sourceSkinned.sharedMesh;
                target.sharedMaterials = sourceSkinned.sharedMaterials;
                target.bones = sourceSkinned.bones;
                target.rootBone = sourceSkinned.rootBone;
                target.localBounds = sourceSkinned.localBounds;
                target.quality = sourceSkinned.quality;
                target.updateWhenOffscreen = sourceSkinned.updateWhenOffscreen;
                proxyRenderer = target;
            }
            else
            {
                SpriteRenderer sourceSprite = source as SpriteRenderer;
                if (sourceSprite != null)
                {
                    SpriteRenderer target = proxy.AddComponent<SpriteRenderer>();
                    target.sprite = sourceSprite.sprite;
                    target.color = sourceSprite.color;
                    target.flipX = sourceSprite.flipX;
                    target.flipY = sourceSprite.flipY;
                    target.drawMode = sourceSprite.drawMode;
                    target.size = sourceSprite.size;
                    target.maskInteraction = sourceSprite.maskInteraction;
                    proxyRenderer = target;
                }
            }
        }

        if (proxyRenderer == null)
        {
            if (Application.isPlaying) Destroy(proxy);
            else DestroyImmediate(proxy);
            return false;
        }

        proxyRenderer.enabled = source.enabled;
        proxyRenderer.shadowCastingMode = source.shadowCastingMode;
        proxyRenderer.receiveShadows = source.receiveShadows;
        proxyRenderer.lightProbeUsage = source.lightProbeUsage;
        proxyRenderer.reflectionProbeUsage = source.reflectionProbeUsage;
        proxyRenderer.lightmapIndex = source.lightmapIndex;
        proxyRenderer.lightmapScaleOffset = source.lightmapScaleOffset;
        room.PortalVisualProxies.Add(proxy);
        return true;
    }

    CharacterController ResolvePlayerCharacterController()
    {
        if (playerController != null)
        {
            CharacterController controller = playerController.GetComponent<CharacterController>();
            if (controller != null) return controller;
            controller = playerController.GetComponentInChildren<CharacterController>();
            if (controller != null) return controller;
        }

        if (playerRoot != null)
        {
            CharacterController controller = playerRoot.GetComponent<CharacterController>();
            if (controller != null) return controller;
            return playerRoot.GetComponentInChildren<CharacterController>();
        }
        return null;
    }

    bool IsPlayerPastDoorOpening(OneWayProceduralDoor door, float minimumDistance)
    {
        if (door == null || playerRoot == null) return false;
        Vector3 normal = door.OutwardNormal.normalized;
        Vector3 center = door.OpeningCenterWorld;
        Vector3 sample = playerRoot.position;
        float signedDistance = Vector3.Dot(sample - center, normal);
        if (signedDistance <= minimumDistance) return false;

        Vector3 tangent = new Vector3(-normal.z, 0f, normal.x).normalized;
        float lateralOffset = Mathf.Abs(Vector3.Dot(sample - center, tangent));
        return lateralOffset <= doorWidth * 0.5f + 0.55f;
    }

    bool IsPlayerDeepInsideDestination(OneWayProceduralDoor door)
    {
        if (door == null || playerRoot == null) return false;
        CharacterController controller = ResolvePlayerCharacterController();
        Vector3 bodyCenter = controller != null ? controller.bounds.center : playerRoot.position + Vector3.up * 0.9f;
        float depth = Vector3.Dot(bodyCenter - door.OpeningCenterWorld, door.OutwardNormal.normalized);
        // Keep the source room loaded until the player is well onto the destination
        // floor, not merely with their capsule barely beyond the threshold.
        return depth >= 1.15f;
    }

    /// <summary>True only when the player's whole controller capsule is beyond the doorway.</summary>
    bool IsPlayerFullyClearOfDoorway(OneWayProceduralDoor door)
    {
        if (door == null || playerRoot == null) return false;

        Vector3 normal = door.OutwardNormal.normalized;
        Vector3 tangent = new Vector3(-normal.z, 0f, normal.x).normalized;
        Vector3 center = door.OpeningCenterWorld;
        CharacterController controller = ResolvePlayerCharacterController();
        Bounds bounds = controller != null ? controller.bounds : new Bounds(playerRoot.position + Vector3.up * 0.9f, new Vector3(0.9f, 1.8f, 0.9f));
        Vector3 bodyCenter = bounds.center;

        float projectedDepthRadius = Mathf.Abs(normal.x) * bounds.extents.x + Mathf.Abs(normal.z) * bounds.extents.z;
        float projectedSideRadius = Mathf.Abs(tangent.x) * bounds.extents.x + Mathf.Abs(tangent.z) * bounds.extents.z;
        float signedDistance = Vector3.Dot(bodyCenter - center, normal);
        float lateralOffset = Mathf.Abs(Vector3.Dot(bodyCenter - center, tangent));

        // The capsule is clear either when its BACK has passed the doorway, or
        // when the whole capsule has moved sideways outside the aperture. The old
        // check required the player to stay close to the centre line, so diagonal
        // entry could leave the transition pending forever even after the doorway
        // was physically clear.
        bool clearPastDoor = signedDistance >= projectedDepthRadius + 0.20f;
        bool clearBesideDoor = lateralOffset >= doorWidth * 0.5f + projectedSideRadius + 0.15f;
        return clearPastDoor || clearBesideDoor;
    }

    /// <summary>True only if a downward probe from the player capsule finds nearby solid ground owned by the destination room.</summary>
    bool IsPlayerSupportedByRoom(ProceduralRoomInstance room)
    {
        if (room == null || room.Root == null || playerRoot == null) return false;

        CharacterController controller = ResolvePlayerCharacterController();
        Vector3 probeOrigin;
        float halfHeight;
        float sideProbe;
        if (controller != null)
        {
            Bounds bounds = controller.bounds;
            probeOrigin = bounds.center;
            halfHeight = bounds.extents.y;
            sideProbe = Mathf.Clamp(controller.radius * 0.65f, 0.12f, 0.22f);
        }
        else
        {
            probeOrigin = playerRoot.position + Vector3.up * 0.9f;
            halfHeight = 0.9f;
            sideProbe = 0.16f;
        }

        // One centre ray can miss a sloped/uneven forest surface when the
        // CharacterController is actually resting on the edge of its capsule.
        // Probe the centre and four points around the footprint. A valid room-owned
        // hit must still be close to the feet and have an upward-facing normal.
        const float minFootGap = -0.20f;
        const float maxFootGap = 0.20f;
        float probeDistance = halfHeight + maxFootGap + 0.05f;

        for (int sampleIndex = 0; sampleIndex < GroundSupportProbeOffsets.Length; sampleIndex++)
        {
            Vector3 offset = GroundSupportProbeOffsets[sampleIndex] * sideProbe;
            int hitCount = Physics.RaycastNonAlloc(
                probeOrigin + offset, Vector3.down, GroundSupportProbeHits, probeDistance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = GroundSupportProbeHits[i];
                if (hit.collider == null || hit.collider.isTrigger) continue;
                Transform hitTransform = hit.collider.transform;
                if (hitTransform != room.Root.transform && !hitTransform.IsChildOf(room.Root.transform)) continue;
                if (hit.normal.y < 0.55f) continue;

                float footGap = hit.distance - halfHeight;
                if (footGap < minFootGap || footGap > maxFootGap) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>Used by the door to delay re-enabling its leaf collider until the player is clear.</summary>
    public bool IsPlayerClearOfDoorLeaf(OneWayProceduralDoor door)
    {
        return IsPlayerFullyClearOfDoorway(door);
    }

    bool DidPlayerCrossDoorPlane(Vector3 previousPosition, Vector3 currentPosition, OneWayProceduralDoor door)
    {
        if (door == null) return false;
        Vector3 normal = door.OutwardNormal.normalized;
        Vector3 center = door.OpeningCenterWorld;
        const float destinationThreshold = 0.05f;
        float previousDistance = Vector3.Dot(previousPosition - center, normal);
        float currentDistance = Vector3.Dot(currentPosition - center, normal);
        if (previousDistance > destinationThreshold || currentDistance <= destinationThreshold)
            return false;

        float distanceDelta = currentDistance - previousDistance;
        if (distanceDelta <= 0.0001f) return false;
        float t = Mathf.Clamp01((destinationThreshold - previousDistance) / distanceDelta);
        Vector3 crossingPoint = Vector3.Lerp(previousPosition, currentPosition, t);
        Vector3 tangent = new Vector3(-normal.z, 0f, normal.x).normalized;
        float lateralOffset = Mathf.Abs(Vector3.Dot(crossingPoint - center, tangent));
        float allowedHalfWidth = doorWidth * 0.5f + 0.45f;
        return lateralOffset <= allowedHalfWidth;
    }

    void FinishPortalEntry(ProceduralRoomInstance room)
    {
        if (room == null) return;
        if (room.OriginalRendererLayers.Count > 0 || room.PortalCullingMaskSaved)
            SetRoomPortalLayer(room, false);
        ReleasePortalResources(room);
    }

    void ReleasePortalResources(ProceduralRoomInstance room)
    {
        if (room == null) return;
        if (room.PortalWindow != null)
        {
            MeshFilter portalFilter = room.PortalWindow.GetComponent<MeshFilter>();
            if (portalFilter != null && portalFilter.sharedMesh != null)
            {
                Mesh portalMesh = portalFilter.sharedMesh;
                room.RuntimeMeshes.Remove(portalMesh);
                if (Application.isPlaying) Destroy(portalMesh);
                else DestroyImmediate(portalMesh);
                portalFilter.sharedMesh = null;
            }
            room.PortalWindow.SetActive(false);
            if (Application.isPlaying) Destroy(room.PortalWindow);
            else DestroyImmediate(room.PortalWindow);
            room.PortalWindow = null;
        }
        if (room.PortalCamera != null)
        {
            Camera portalCam = room.PortalCamera.GetComponent<Camera>();
            if (portalCam != null) { portalCam.targetTexture = null; portalCam.enabled = false; }
            room.PortalCamera.SetActive(false);
            if (Application.isPlaying) Destroy(room.PortalCamera);
            else DestroyImmediate(room.PortalCamera);
            room.PortalCamera = null;
        }
        if (room.PortalRT != null)
        {
            room.PortalRT.Release();
            if (Application.isPlaying) Destroy(room.PortalRT);
            else DestroyImmediate(room.PortalRT);
            room.PortalRT = null;
        }
        if (room.PortalMaterial != null)
        {
            if (Application.isPlaying) Destroy(room.PortalMaterial);
            else DestroyImmediate(room.PortalMaterial);
            room.PortalMaterial = null;
        }
    }

    bool BuildPortalWindow(ProceduralRoomInstance room, OneWayProceduralDoor door)
    {
        if (room == null || room.Root == null || door == null) return false;
        Camera mainCam = playerCamera != null ? playerCamera : Camera.main;
        if (mainCam == null) return false;

        Shader portalShader = Resources.Load<Shader>("PortalWindow");
        if (portalShader == null) portalShader = Shader.Find("Hidden/ColdCar/PortalWindow");
        if (portalShader == null || !portalShader.isSupported)
        {
            Debug.LogError("[ProceduralRooms] PortalWindow shader missing/unsupported; door kept closed.", this);
            return false;
        }

        Vector3 normal = door.OutwardNormal.normalized;
        Vector3 center = door.OpeningCenterWorld + Vector3.up * (doorHeight * 0.5f) - normal * 0.012f;
        int viewportW = Mathf.Max(256, mainCam.pixelWidth);
        int viewportH = Mathf.Max(256, mainCam.pixelHeight);
        int rtW = Mathf.Clamp(Mathf.RoundToInt(viewportW * 0.75f), 256, 2048);
        int rtH = Mathf.Clamp(Mathf.RoundToInt(viewportH * 0.75f), 256, 2048);
        var rt = new RenderTexture(rtW, rtH, 24, RenderTextureFormat.Default)
        {
            name = "ProceduralDoorPortalRT",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            antiAliasing = 1
        };
        if (!rt.Create())
        {
            Destroy(rt);
            Debug.LogError("[ProceduralRooms] Could not allocate door portal RenderTexture.", this);
            return false;
        }

        GameObject camGo = new GameObject("PortalCamera");
        camGo.transform.SetParent(room.Root.transform, true);
        camGo.transform.SetPositionAndRotation(mainCam.transform.position, mainCam.transform.rotation);
        Camera portalCam = camGo.AddComponent<Camera>();
        portalCam.CopyFrom(mainCam);
        portalCam.enabled = false; // We explicitly render it after syncing the pose.
        portalCam.targetTexture = rt;
        portalCam.rect = new Rect(0f, 0f, 1f, 1f);
        portalCam.aspect = mainCam.aspect;
        // Портал-камера видит ВЕСЬ мир, КРОМЕ самого окна-квада (слой 28):
        // так в RT попадает настоящая картина из точки игрока — комната в
        // проёме, рама вокруг неё, и главное — СВЕТ ФОНАРЯ освещает интерьер
        // (фонарь на слое 0; исключённые слои гасят и источники света).
        portalCam.cullingMask = ~(1 << PortalWindowLayer);
        portalCam.clearFlags = CameraClearFlags.SolidColor;
        portalCam.backgroundColor = new Color(0.008f, 0.01f, 0.014f, 1f);
        portalCam.depth = -100f;
        portalCam.useOcclusionCulling = false;
        portalCam.allowMSAA = false;

        Material windowMat = new Material(portalShader) { name = "ProceduralDoorPortalMaterial", renderQueue = 2001 };
        windowMat.SetTexture("_PortalTex", rt);

        // Build a four-vertex aperture mesh instead of CreatePrimitive, so it
        // never has even a one-frame collider and cannot block the player.
        GameObject quad = new GameObject("PortalWindow");
        quad.layer = PortalWindowLayer; // портал-камера не рендерит окно (иначе самопоказ)
        quad.transform.SetParent(room.Root.transform, true);
        quad.transform.SetPositionAndRotation(center, Quaternion.LookRotation(-normal, Vector3.up));
        quad.transform.localScale = Vector3.one;

        // Апертура: РАБОЧИЕ значения из бэкапа (врезка 10 см с боков, 7 см сверху).
        // Квад чуть меньше проёма — края проёма закрывает рамка двери.
        float apertureWidth = Mathf.Max(0.2f, doorWidth - 0.20f);
        float apertureHeight = Mathf.Max(0.2f, doorHeight - 0.14f);
        float halfW = apertureWidth * 0.5f;
        float halfH = apertureHeight * 0.5f;
        Mesh mesh = new Mesh { name = "DoorPortalApertureMesh" };
        mesh.vertices = new[]
        {
            new Vector3(-halfW, -halfH, 0f), new Vector3(halfW, -halfH, 0f),
            new Vector3(halfW, halfH, 0f), new Vector3(-halfW, halfH, 0f)
        };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        quad.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer quadRenderer = quad.AddComponent<MeshRenderer>();
        quadRenderer.sharedMaterial = windowMat;
        quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        quadRenderer.receiveShadows = false;

        room.RuntimeMeshes.Add(mesh);
        room.PortalWindow = quad;
        room.PortalRT = rt;
        room.PortalCamera = camGo;
        room.PortalMaterial = windowMat;

        // Populate the RT before Door.Open() is called. Otherwise the first
        // visible frame can be the clear colour (an apparent empty void).
        SetPortalCameraPose(portalCam, mainCam, door);
        portalCam.ResetProjectionMatrix();
        portalCam.ResetCullingMatrix();
        UpdatePortalProjectiveMatrix(room, portalCam);
        portalCam.Render();
        int visible30 = 0, visible31 = 0;
        Renderer[] portalRenderers = room.Root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < portalRenderers.Length; i++)
        {
            if (portalRenderers[i] == null || !portalRenderers[i].enabled) continue;
            int layer = portalRenderers[i].gameObject.layer;
            if (layer == PortalLayer) visible30++;
            else if (layer == 31) visible31++;
        }
        Debug.Log($"[Portal] Window created: quadActive={quad.activeInHierarchy}, layer={quad.layer}, pos={quad.transform.position:0.00}, " +
            $"rtSize={rt.width}x{rt.height}, portalCamPos={camGo.transform.position:0.00} (main={mainCam.transform.position:0.00}), " +
            $"portalCamMask={portalCam.cullingMask}, renderersOn30={visible30}, on31={visible31}, renderQueue={windowMat.renderQueue}.", quad);
        if (visible30 == 0)
        {
            Debug.LogError("[ProceduralRooms] Portal camera sees no destination renderers on layer 30. Door kept closed instead of showing a blank portal.", this);
            ReleasePortalResources(room);
            return false;
        }
        Debug.Log($"[ProceduralRooms] Door portal ready and pre-rendered: aperture={apertureWidth:F2}x{apertureHeight:F2}m, " +
            $"RT={rtW}x{rtH}, cameraPos={portalCam.transform.position}, layer30Renderers={visible30}, layer31Renderers={visible31}, " +
            $"cullingMask={portalCam.cullingMask}.", this);
        return true;
    }

    void DestroyRoom(ProceduralRoomInstance room)
    {
        if (room == null) return;
        FinishPortalEntry(room);
        // На всякий случай режим-зависимо: ExecuteAlways может вызвать это
        // и в редакторе, где Destroy запрещён.
        foreach (BoxCollider box in room.WallColliders)
            if (box != null) { if (Application.isPlaying) Destroy(box); else DestroyImmediate(box); }
        if (room.Root != null)
        {
            TerrainCollider[] terrainColliders = room.Root.GetComponentsInChildren<TerrainCollider>(true);
            for (int i = 0; i < terrainColliders.Length; i++)
                if (terrainColliders[i] != null) terrainColliders[i].terrainData = null;
        }
        foreach (Mesh mesh in room.RuntimeMeshes)
            if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); }
        foreach (TerrainData terrainData in room.RuntimeTerrainData)
            if (terrainData != null) { if (Application.isPlaying) Destroy(terrainData); else DestroyImmediate(terrainData); }
        if (room.Root != null) { if (Application.isPlaying) Destroy(room.Root); else DestroyImmediate(room.Root); }
    }

    /// <summary>Ищет поверхность, чья текстура совпадает с текстурой пола под ногами.</summary>
    public RoomFootstepSurface FindFootstepSurface(Texture2D floorTexture)
    {
        if (floorTexture == null || footstepSurfaces == null) return null;
        foreach (RoomFootstepSurface surface in footstepSurfaces)
        {
            if (surface == null || surface.textures == null) continue;
            foreach (Texture2D tex in surface.textures)
                if (tex == floorTexture) return surface;
        }
        return null;
    }

    List<Texture2D> _wallTextureBag;

    /// <summary>
    /// Wall-текстура для комнаты из «мешка»: все варианты из wallTextures
    /// расходуются по одному, пока не кончатся, потом мешок перемешивается
    /// заново. Ни одна текстура не выпадает в 4 раза чаще других, и все 9
    /// встречаются за один круг. Текстура предыдущей комнаты исключается.
    /// </summary>
    Texture2D PickWallTexture(System.Random rng, Texture2D exclude)
    {
        if (wallTextures == null || wallTextures.Length == 0) return null;

        if (_wallTextureBag == null || _wallTextureBag.Count == 0)
        {
            _wallTextureBag = new List<Texture2D>();
            foreach (Texture2D texture in wallTextures)
                if (texture != null) _wallTextureBag.Add(texture);
            for (int i = _wallTextureBag.Count - 1; i > 0; i--)
            {
                int j = _random != null ? _random.Next(i + 1) : rng.Next(i + 1);
                (_wallTextureBag[i], _wallTextureBag[j]) = (_wallTextureBag[j], _wallTextureBag[i]);
            }
        }

        // Извлекаем кандидата; если он совпал с предыдущей комнатой —
        // откладываем в конец и берём следующий. Ограничение попыток =
        // размеру мешка, чтобы исключение не зациклило выбор.
        Texture2D fallback = exclude != null ? exclude : wallTextures[0];
        int attempts = _wallTextureBag.Count;
        while (attempts-- > 0)
        {
            Texture2D candidate = _wallTextureBag[0];
            _wallTextureBag.RemoveAt(0);
            if (candidate != exclude) return candidate;
            _wallTextureBag.Add(candidate);
        }
        return fallback;
    }

    int NextSeed()
    {
        // ExecuteAlways-глюк: в edit-mode Update может выполниться ДО Awake,
        // поэтому _random лениво инициализируем на месте.
        if (_random == null) _random = new System.Random(randomSeed == 0 ? 1 : randomSeed);
        return _random.Next(1, int.MaxValue);
    }

    static float RoundToHalf(float value) => Mathf.Round(value * 2f) * 0.5f;

    Texture2D PickTexture(Texture2D[] candidates, System.Random rng)
    {
        if (candidates == null || candidates.Length == 0) return null;
        // Без rng — детерминированный выбор первой текстуры (сейв-комната и
        // edit-превью должны быть стабильными, а не перекрашиваться каждый билд).
        if (rng == null) return candidates[0];
        return candidates[rng.Next(candidates.Length)];
    }

    static void Shuffle<T>(IList<T> list, System.Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            T item = list[i];
            list[i] = list[j];
            list[j] = item;
        }
    }

    static ProceduralRoomSide Opposite(ProceduralRoomSide side)
    {
        switch (side)
        {
            case ProceduralRoomSide.North: return ProceduralRoomSide.South;
            case ProceduralRoomSide.East: return ProceduralRoomSide.West;
            case ProceduralRoomSide.South: return ProceduralRoomSide.North;
            default: return ProceduralRoomSide.East;
        }
    }

    static ProceduralRoomSide SideFromNormal(Vector3 normal)
    {
        if (Mathf.Abs(normal.z) >= Mathf.Abs(normal.x))
            return normal.z >= 0f ? ProceduralRoomSide.North : ProceduralRoomSide.South;
        return normal.x >= 0f ? ProceduralRoomSide.East : ProceduralRoomSide.West;
    }

    static Vector3 SideNormal(ProceduralRoomSide side)
    {
        switch (side)
        {
            case ProceduralRoomSide.North: return Vector3.forward;
            case ProceduralRoomSide.East: return Vector3.right;
            case ProceduralRoomSide.South: return Vector3.back;
            default: return Vector3.left;
        }
    }

    static Vector3 SocketLocalPosition(ProceduralRoomPlan plan, ProceduralDoorSocket socket)
    {
        switch (socket.Side)
        {
            case ProceduralRoomSide.North: return new Vector3(socket.Along, 0f, plan.Depth * 0.5f);
            case ProceduralRoomSide.South: return new Vector3(socket.Along, 0f, -plan.Depth * 0.5f);
            case ProceduralRoomSide.East: return new Vector3(plan.Width * 0.5f, 0f, socket.Along);
            default: return new Vector3(-plan.Width * 0.5f, 0f, socket.Along);
        }
    }

    static Vector3 IncomingBoundaryLocalPosition(ProceduralRoomPlan plan)
    {
        switch (plan.IncomingSide)
        {
            case ProceduralRoomSide.North: return new Vector3(0f, 0f, plan.Depth * 0.5f);
            case ProceduralRoomSide.East: return new Vector3(plan.Width * 0.5f, 0f, 0f);
            case ProceduralRoomSide.South: return new Vector3(0f, 0f, -plan.Depth * 0.5f);
            default: return new Vector3(-plan.Width * 0.5f, 0f, 0f);
        }
    }

    Material GetFloorMaterial(Texture2D texture) => GetCachedMaterial(_floorMaterialCache, texture, "FloorMat_", new Color(0.87f, 0.87f, 0.87f, 1f));
    Material GetWallMaterial(Texture2D texture) => GetCachedMaterial(_wallMaterialCache, texture, "WallMat_", Color.white);

    Material GetCeilingMaterial(Texture2D texture) => GetCachedMaterial(_ceilingMaterialCache, texture, "CeilingMat_", new Color(0.38f, 0.38f, 0.4f, 1f));

    Material GetDoorMaterial(Texture2D texture)
    {
        if (_doorMaterial != null) return _doorMaterial;
        _doorMaterial = CreateMaterial("DoorMat_Procedural", texture, new Color(0.52f, 0.49f, 0.43f, 1f));
        return _doorMaterial;
    }

    Material GetDoorTrimMaterial()
    {
        if (_doorTrimMaterial != null) return _doorTrimMaterial;
        _doorTrimMaterial = CreateMaterial("DoorTrimMat_Procedural", null, new Color(0.18f, 0.17f, 0.15f, 1f));
        return _doorTrimMaterial;
    }

    Material GetCachedMaterial(Dictionary<Texture2D, Material> cache, Texture2D texture, string prefix, Color tint)
    {
        // Dictionary<TKey, TValue> does not accept a null Texture2D key.
        Texture2D cacheKey = texture != null ? texture : Texture2D.whiteTexture;
        if (cache.TryGetValue(cacheKey, out Material material) && material != null) return material;
        material = CreateMaterial(prefix + (texture != null ? texture.name : "Fallback"), texture, tint);
        cache[cacheKey] = material;
        return material;
    }

    Material CreateMaterial(string materialName, Texture2D texture, Color tint)
    {
        // Plain Unity lighting: the standard URP/Lit shader guarantees the
        // flashlight and any scene light illuminate the generated surfaces.
        // The fog-era TileSurfaceLitFPS shader stays on disk for the isometric scene.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null && proceduralSurfaceShader != null) shader = proceduralSurfaceShader;
        if (shader == null) shader = Shader.Find("Hidden/ColdCar/TileSurfaceLitFPS");
        if (shader == null) shader = Shader.Find("Hidden/ColdCar/TileSurface");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        var material = new Material(shader) { name = materialName };
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
        if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
        // Matte PS1-era look: kill specular gloss on snow/wall/door surfaces.
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.08f);
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.08f);
        _ownedMaterials.Add(material);
        return material;
    }

    void OnDestroy()
    {
        // This component uses ExecuteAlways, so OnDestroy can run in Edit Mode
        // while Unity is preparing a build or the editor is closing/reloading.
        // Destroy is only valid in Play Mode; temporary materials must be
        // destroyed immediately while running in the editor.
        foreach (Material material in _ownedMaterials)
        {
            if (material == null) continue;

            if (Application.isPlaying)
                Destroy(material);
            else
                DestroyImmediate(material);
        }

        _ownedMaterials.Clear();

        if (_daySkybox != null)
        {
            if (Application.isPlaying) Destroy(_daySkybox);
            else DestroyImmediate(_daySkybox);
            _daySkybox = null;
        }
        if (_generatedDaySun != null)
        {
            GameObject sunObject = _generatedDaySun.gameObject;
            _generatedDaySun = null;
            if (Application.isPlaying) Destroy(sunObject);
            else DestroyImmediate(sunObject);
        }
    }
}
