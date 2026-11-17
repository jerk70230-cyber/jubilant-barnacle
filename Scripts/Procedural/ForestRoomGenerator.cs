using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the outdoor forest room. Prefabs assigned in the three arrays are
/// used when available; otherwise lightweight low-poly fallback meshes are
/// generated once and shared by all instances.
/// All placement is deterministic for a room seed.
/// </summary>
[DisallowMultipleComponent]
public sealed class ForestRoomGenerator : MonoBehaviour
{
    [Header("Room generation")]
    [Tooltip("Вероятность леса среди особых комнат — не среди коридоров и помещений с перегородками.")]
    [Range(0, 100)] public int forestRoomChance = 22;
    [Min(1f)] public float forestMinimumBaseSize = 10.5f;
    [Min(1f)] public float forestMaximumBaseSize = 16f;
    [Min(3f)] public float forestSizeMultiplier = 3f;

    [Header("3D model lists — assign project prefabs here")]
    [Tooltip("Ели, сосны и другие деревья. Pivot префаба должен находиться у основания (Y=0), верх модели направлен по +Y. Пустой список включает две встроенные low-poly модели.")]
    public GameObject[] treePrefabs = new GameObject[0];
    [Tooltip("Кусты. Pivot у основания (Y=0). Пустой список включает встроенный low-poly куст.")]
    public GameObject[] bushPrefabs = new GameObject[0];
    [Tooltip("Одиночные грибы или готовые группы. Pivot у земли (Y=0). Пустой список включает встроенные цветовые варианты грибных групп.")]
    public GameObject[] mushroomPrefabs = new GameObject[0];

    [Header("Per-room vegetation variation")]
    [Tooltip("Густой лес: деревьев много, минимальная дистанция между ними мала.")]
    [Min(0)] public int minimumTrees = 85;
    [Min(0)] public int maximumTrees = 150;
    [Min(0)] public int minimumBushes = 10;
    [Min(0)] public int maximumBushes = 32;
    [Min(0)] public int minimumMushroomGroups = 4;
    [Min(0)] public int maximumMushroomGroups = 16;
    [Tooltip("Количество в диапазонах выше рассчитано на площадь такого размера. Фактическое число растений масштабируется по площади комнаты.")]
    [Min(100f)] public float vegetationReferenceArea = 1600f;
    [Range(0f, 1f)] public float bushRoomPresenceChance = 0.85f;
    [Range(0f, 1f)] public float mushroomRoomPresenceChance = 0.8f;
    [Tooltip("Случайная вариация плотности внутри выбранного диапазона.")]
    [Range(0.5f, 1.5f)] public float vegetationDensityVariation = 1f;

    [Header("Placement and collision")]
    [Tooltip("Минимальная дистанция между стволами: 2.2–2.5 м даёт густой лес без сплошной стены.")]
    [Min(0.5f)] public float treeMinimumSpacing = 2.3f;
    [Min(0.2f)] public float bushMinimumSpacing = 1.05f;
    [Min(0.5f)] public float edgePadding = 1.6f;
    [Min(0.5f)] public float doorwayClearRadius = 2.8f;
    [Min(4)] public int placementAttemptsPerObject = 40;
    [Min(2f)] public float invisibleBoundaryHeight = 5f;
    [Min(0.1f)] public float invisibleBoundaryThickness = 0.8f;

    [Header("Soft uneven terrain")]
    [Tooltip("Крупные холмы: перепад высот до ±амплитуда. 0.5 = заметные лесные холмы.")]
    [Range(0.05f, 0.65f)] public float largeBumpAmplitude = 0.5f;
    [Tooltip("Мелкая неровность поверхности (кочки).")]
    [Range(0.01f, 0.3f)] public float smallBumpAmplitude = 0.12f;
    [Range(0.015f, 0.12f)] public float largeNoiseFrequency = 0.052f;
    [Range(0.04f, 0.3f)] public float smallNoiseFrequency = 0.17f;
    [Range(24, 90)] public int terrainGridResolution = 56;
    [Min(0.5f)] public float flatDoorwayDepth = 3.2f;

    [Header("Rendering and performance")]
    [Tooltip("Деревья отбрасывают тени; более мелкие растения по умолчанию не создают отдельные тени, чтобы не перегружать Directional Light.")]
    public bool treesCastShadows = true;
    public bool bushesCastShadows = false;
    public bool mushroomsCastShadows = false;
    [Tooltip("Позволяет растительности принимать тени от солнца.")]
    public bool vegetationReceivesShadows = true;
    [Tooltip("Опционально объединяет неподвижные префабы через Static Batching. Для встроенных повторяющихся моделей по умолчанию выключено: их общие Mesh/Material используют GPU Instancing.")]
    public bool useRuntimeStaticBatching = false;
    [Tooltip("Если включено, неподвижные префабы без Animator/скриптов помечаются Static.")]
    public bool markSimplePrefabsStatic = true;

    Mesh _spruceMesh;
    Mesh _pineMesh;
    Mesh _bushMesh;
    Mesh _mushroomMesh;
    Material _barkMaterial;
    Material _foliageMaterial;
    Material _bushMaterial;
    Material _mushroomStemMaterial;
    readonly Material[] _mushroomCapMaterials = new Material[4];
    Transform _lastTerrainPhysicsRoom;
    bool _lastTerrainPhysicsReady;

    public bool IsTerrainPhysicsReadyForRoom(Transform roomRoot)
    {
        return roomRoot != null && _lastTerrainPhysicsRoom == roomRoot && _lastTerrainPhysicsReady;
    }

    public float GetMinimumForestDimension()
    {
        return Mathf.Min(forestMinimumBaseSize, forestMaximumBaseSize) * Mathf.Max(3f, forestSizeMultiplier);
    }

    public float GetMaximumForestDimension()
    {
        return Mathf.Max(forestMinimumBaseSize, forestMaximumBaseSize) * Mathf.Max(3f, forestSizeMultiplier);
    }

    public void Generate(Transform roomRoot, ProceduralRoomPlan plan, float doorWidth, Material groundMaterial,
        float textureMetersPerTile, List<Mesh> roomRuntimeMeshes, List<TerrainData> roomRuntimeTerrainData)
    {
        if (roomRoot == null || plan == null) return;
        _lastTerrainPhysicsRoom = roomRoot;
        _lastTerrainPhysicsReady = false;
        System.Random rng = new System.Random(plan.Seed ^ unchecked((int)0x6f27a1b9));

        Mesh terrainMesh = BuildTerrain(roomRoot, plan, groundMaterial, textureMetersPerTile, roomRuntimeTerrainData);
        if (terrainMesh != null && roomRuntimeMeshes != null) roomRuntimeMeshes.Add(terrainMesh);
        BuildInvisibleBoundaries(roomRoot, plan, doorWidth);
        BuildEntranceLanding(roomRoot, plan, doorWidth);

        Transform vegetationRoot = new GameObject("ForestVegetation").transform;
        vegetationRoot.SetParent(roomRoot, false);
        Transform treesRoot = new GameObject("Trees").transform;
        treesRoot.SetParent(vegetationRoot, false);
        Transform bushesRoot = new GameObject("Bushes").transform;
        bushesRoot.SetParent(vegetationRoot, false);
        Transform mushroomsRoot = new GameObject("Mushrooms").transform;
        mushroomsRoot.SetParent(vegetationRoot, false);

        var doorKeepOut = BuildDoorKeepOutPoints(plan);
        var treePoints = new List<Vector2>();
        var bushPoints = new List<Vector2>();

        List<GameObject> treeModels = GetValidPrefabs(treePrefabs);
        List<GameObject> bushModels = GetValidPrefabs(bushPrefabs);
        List<GameObject> mushroomModels = GetValidPrefabs(mushroomPrefabs);

        float[] treeWeights = BuildRoomWeights(treeModels.Count > 0 ? treeModels.Count : 2, rng);
        float[] bushWeights = BuildRoomWeights(bushModels.Count > 0 ? bushModels.Count : 1, rng);
        float[] mushroomWeights = BuildRoomWeights(mushroomModels.Count > 0 ? mushroomModels.Count : _mushroomCapMaterials.Length, rng);

        float areaMultiplier = Mathf.Clamp((plan.Width * plan.Depth) / Mathf.Max(100f, vegetationReferenceArea), 0.6f, 1.6f);
        int treeCount = ScaleCountForArea(RandomCount(minimumTrees, maximumTrees, rng, true), areaMultiplier, true);
        int bushCount = rng.NextDouble() < bushRoomPresenceChance
            ? ScaleCountForArea(RandomCount(minimumBushes, maximumBushes, rng, false), areaMultiplier, false) : 0;
        int mushroomCount = rng.NextDouble() < mushroomRoomPresenceChance
            ? ScaleCountForArea(RandomCount(minimumMushroomGroups, maximumMushroomGroups, rng, false), areaMultiplier, false) : 0;

        int actualTrees = SpawnCategory(treeCount, treesRoot, treeModels, treeWeights, Category.Tree, plan, rng, doorKeepOut, treePoints, null);
        int actualBushes = SpawnCategory(bushCount, bushesRoot, bushModels, bushWeights, Category.Bush, plan, rng, doorKeepOut, bushPoints, treePoints);
        int actualMushrooms = SpawnCategory(mushroomCount, mushroomsRoot, mushroomModels, mushroomWeights, Category.Mushroom, plan, rng, doorKeepOut, null, null);

        if (useRuntimeStaticBatching && Application.isPlaying)
        {
            try
            {
                // The generated terrain is separate; only immutable decoration is batched.
                StaticBatchingUtility.Combine(vegetationRoot.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Forest] Static batching skipped: " + e.Message, this);
            }
        }

        Debug.Log($"[Forest] Seed {plan.Seed}: area {plan.Width * plan.Depth:0} m², density x{areaMultiplier:0.00}; trees {actualTrees}/{treeCount}, bushes {actualBushes}/{bushCount}, mushroom groups {actualMushrooms}/{mushroomCount}.", this);
    }

    enum Category { Tree, Bush, Mushroom }

    static int ScaleCountForArea(int count, float areaMultiplier, bool neverZero)
    {
        int scaled = Mathf.RoundToInt(count * areaMultiplier);
        return Mathf.Max(neverZero ? 1 : 0, scaled);
    }

    int RandomCount(int minimum, int maximum, System.Random rng, bool neverZero)
    {
        int min = Mathf.Max(neverZero ? 1 : 0, Mathf.Min(minimum, maximum));
        int max = Mathf.Max(minimum, maximum);
        int value = rng.Next(min, max + 1);
        if (vegetationDensityVariation != 1f)
            value = Mathf.RoundToInt(value * Mathf.Lerp(1f, vegetationDensityVariation, (float)rng.NextDouble()));
        return Mathf.Max(neverZero ? 1 : 0, value);
    }

    static List<GameObject> GetValidPrefabs(GameObject[] source)
    {
        var result = new List<GameObject>();
        if (source == null) return result;
        for (int i = 0; i < source.Length; i++)
            if (source[i] != null) result.Add(source[i]);
        return result;
    }

    static float[] BuildRoomWeights(int count, System.Random rng)
    {
        var weights = new float[Mathf.Max(1, count)];
        if (weights.Length == 1) { weights[0] = 1f; return weights; }

        int profile = rng.Next(100);
        if (profile < 24)
        {
            // Occasionally one species dominates the entire room.
            int chosen = rng.Next(weights.Length);
            weights[chosen] = 1f;
        }
        else if (profile < 65)
        {
            // Or one species is dominant while other species remain rare.
            int chosen = rng.Next(weights.Length);
            float dominant = 0.72f + (float)rng.NextDouble() * 0.23f;
            float rest = (1f - dominant) / (weights.Length - 1);
            for (int i = 0; i < weights.Length; i++) weights[i] = i == chosen ? dominant : rest;
        }
        else
        {
            // Sometimes the available species are distributed more evenly.
            float sum = 0f;
            for (int i = 0; i < weights.Length; i++) { weights[i] = 0.7f + (float)rng.NextDouble() * 0.6f; sum += weights[i]; }
            for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
        }
        return weights;
    }

    static int ChooseIndex(float[] weights, System.Random rng)
    {
        if (weights == null || weights.Length == 0) return 0;
        double total = 0d;
        for (int i = 0; i < weights.Length; i++) total += Math.Max(0f, weights[i]);
        if (total <= 0.000001d) return rng.Next(weights.Length);
        double roll = rng.NextDouble() * total;
        for (int i = 0; i < weights.Length; i++)
        {
            float weight = Mathf.Max(0f, weights[i]);
            if (weight <= 0f) continue;
            roll -= weight;
            if (roll < 0d) return i;
        }
        return weights.Length - 1;
    }

    int SpawnCategory(int count, Transform parent, List<GameObject> prefabs, float[] weights,
        Category category, ProceduralRoomPlan plan, System.Random rng, List<Vector2> doorKeepOut,
        List<Vector2> placedPoints, List<Vector2> avoidPoints)
    {
        int spawned = 0;
        int attempts = Mathf.Max(placementAttemptsPerObject, count * placementAttemptsPerObject);
        for (int attempt = 0; attempt < attempts && spawned < count; attempt++)
        {
            float x = ((float)rng.NextDouble() * 2f - 1f) * (plan.Width * 0.5f - edgePadding);
            float z = ((float)rng.NextDouble() * 2f - 1f) * (plan.Depth * 0.5f - edgePadding);
            Vector2 point = new Vector2(x, z);
            float keepOutRadius = category == Category.Tree ? doorwayClearRadius + 0.65f : doorwayClearRadius;
            if (IsNearAny(point, doorKeepOut, keepOutRadius)) continue;

            if (category == Category.Tree && IsNearAny(point, placedPoints, treeMinimumSpacing)) continue;
            if (category == Category.Bush && IsNearAny(point, placedPoints, bushMinimumSpacing)) continue;
            if (avoidPoints != null && IsNearAny(point, avoidPoints, category == Category.Bush ? 1.45f : 0.8f)) continue;

            float y = SampleTerrainHeight(x, z, plan.Seed, plan.Width, plan.Depth);
            float scale = category == Category.Tree
                ? 0.82f + (float)rng.NextDouble() * 0.46f
                : category == Category.Bush ? 0.82f + (float)rng.NextDouble() * 0.38f
                : 0.75f + (float)rng.NextDouble() * 0.55f;
            float yaw = (float)rng.NextDouble() * 360f;

            GameObject instance = null;
            bool isPrefabInstance = prefabs.Count > 0;
            if (isPrefabInstance)
            {
                int selected = ChooseIndex(weights, rng);
                selected = Mathf.Clamp(selected, 0, prefabs.Count - 1);
                instance = Instantiate(prefabs[selected], parent, false);
                instance.transform.localScale = instance.transform.localScale * scale;
                ConfigureVegetationRendering(instance, category);
                PrepareStaticObject(instance);
            }
            else
            {
                instance = CreateFallbackInstance(parent, category, ChooseIndex(weights, rng));
                instance.transform.localScale = Vector3.one * scale;
                ConfigureVegetationRendering(instance, category);
            }

            if (instance == null) continue;
            instance.name = category.ToString() + "_" + spawned.ToString("000");
            Vector3 localPosition = new Vector3(x, y, z);
            instance.transform.localPosition = localPosition;
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            // Imported vegetation prefabs can contain colliders authored in a
            // different unit scale. Some current tree prefabs produce world bounds
            // hundreds of metres tall/wide, which overlap and depenetrate the player.
            // Keep their visuals/scales intact but replace their physics with a
            // small, unscaled trunk/bush proxy in the room's own coordinate space.
            if (isPrefabInstance)
                EnsureObstacleCollider(instance, parent, category, localPosition, yaw, scale);

            if (placedPoints != null) placedPoints.Add(point);
            spawned++;
        }
        return spawned;
    }

    void EnsureObstacleCollider(GameObject instance, Transform physicsParent, Category category,
        Vector3 localPosition, float yaw, float scale)
    {
        if (category == Category.Mushroom || instance == null || physicsParent == null) return;

        // Never let an imported collider inherit an arbitrary FBX/prefab root scale.
        // Disable all authored colliders (including child colliders) but preserve
        // the rendered prefab and its original transform hierarchy.
        Collider[] authoredColliders = instance.GetComponentsInChildren<Collider>(true);
        int disabledCount = 0;
        for (int i = 0; i < authoredColliders.Length; i++)
        {
            Collider authored = authoredColliders[i];
            if (authored == null) continue;
            if (authored.enabled) disabledCount++;

            Bounds authoredBounds = authored.bounds;
            Vector3 authoredSize = authoredBounds.size;
            if (authoredSize.x > 10f || authoredSize.y > 10f || authoredSize.z > 10f)
            {
                Debug.LogWarning(
                    $"[Forest] DISABLED OVERSIZED PREFAB COLLIDER: object='{instance.name}', " +
                    $"collider='{authored.name}', type={authored.GetType().Name}, " +
                    $"worldSize={authoredSize}, lossyScale={authored.transform.lossyScale}.",
                    authored);
            }

            authored.enabled = false;
        }

        // This proxy is a sibling of the visual prefab, not its child. Its dimensions
        // therefore remain in metres even when the imported visual root is scaled
        // by 100x or has unusual nested scales.
        GameObject proxy = new GameObject(instance.name + "_ObstacleCollider");
        proxy.layer = instance.layer;
        Transform proxyTransform = proxy.transform;
        proxyTransform.SetParent(physicsParent, false);
        proxyTransform.localPosition = localPosition;
        proxyTransform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        proxyTransform.localScale = Vector3.one;

        CapsuleCollider capsule = proxy.AddComponent<CapsuleCollider>();
        capsule.direction = 1;
        if (category == Category.Tree)
        {
            capsule.radius = 0.34f * scale;
            capsule.height = Mathf.Max(capsule.radius * 2f, 2.5f * scale);
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
        }
        else
        {
            capsule.radius = 0.5f * scale;
            capsule.height = Mathf.Max(capsule.radius * 2f, 1.05f * scale);
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
        }

        Debug.Log(
            $"[Forest] Replaced prefab obstacle colliders: visual='{instance.name}', " +
            $"disabledAuthored={disabledCount}, proxy='{proxy.name}', " +
            $"radius={capsule.radius:0.000}, height={capsule.height:0.000}, layer={proxy.layer}.",
            proxy);
    }

    void ConfigureVegetationRendering(GameObject instance, Category category)
    {
        if (instance == null) return;
        bool castsShadows = category == Category.Tree ? treesCastShadows
            : category == Category.Bush ? bushesCastShadows : mushroomsCastShadows;
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null) continue;
            renderer.shadowCastingMode = castsShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = vegetationReceivesShadows;
        }
    }

    void PrepareStaticObject(GameObject instance)
    {
        if (!markSimplePrefabsStatic) return;
        if (instance.GetComponentInChildren<Animator>() != null ||
            instance.GetComponentInChildren<Animation>() != null ||
            instance.GetComponentInChildren<ParticleSystem>() != null ||
            instance.GetComponentInChildren<Rigidbody>() != null ||
            instance.GetComponentInChildren<LODGroup>() != null ||
            instance.GetComponentInChildren<SkinnedMeshRenderer>() != null) return;
        MonoBehaviour[] scripts = instance.GetComponentsInChildren<MonoBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
            if (scripts[i] != null) return;
        instance.isStatic = true;
        Transform[] children = instance.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < children.Length; i++) children[i].gameObject.isStatic = true;
    }

    GameObject CreateFallbackInstance(Transform parent, Category category, int variant)
    {
        EnsureFallbackAssets();
        var go = new GameObject("Fallback_" + category);
        go.transform.SetParent(parent, false);
        MeshFilter filter = go.AddComponent<MeshFilter>();
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        if (category == Category.Tree)
        {
            bool pine = variant == 1;
            filter.sharedMesh = pine ? _pineMesh : _spruceMesh;
            renderer.sharedMaterials = new[] { _barkMaterial, _foliageMaterial };
            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.radius = 0.34f;
            capsule.height = 2.5f;
            capsule.center = new Vector3(0f, 1.25f, 0f);
        }
        else if (category == Category.Bush)
        {
            filter.sharedMesh = _bushMesh;
            renderer.sharedMaterials = new[] { _barkMaterial, _bushMaterial };
            SphereCollider sphere = go.AddComponent<SphereCollider>();
            sphere.radius = 0.72f;
            sphere.center = new Vector3(0f, 0.52f, 0f);
        }
        else
        {
            filter.sharedMesh = _mushroomMesh;
            renderer.sharedMaterials = new[] { _mushroomStemMaterial, _mushroomCapMaterials[Mathf.Clamp(variant, 0, _mushroomCapMaterials.Length - 1)] };
        }
        PrepareStaticObject(go);
        return go;
    }

    Mesh BuildTerrain(Transform roomRoot, ProceduralRoomPlan plan, Material material,
        float textureMetersPerTile, List<TerrainData> roomRuntimeTerrainData)
    {
        int resolution = Mathf.Clamp(terrainGridResolution, 24, 90);
        float halfW = plan.Width * 0.5f;
        float halfD = plan.Depth * 0.5f;
        var vertices = new Vector3[(resolution + 1) * (resolution + 1)];
        var uvs = new Vector2[vertices.Length];
        var triangles = new int[resolution * resolution * 6];
        float uvScale = Mathf.Max(0.25f, textureMetersPerTile);
        int index = 0;
        for (int z = 0; z <= resolution; z++)
        {
            float v = z / (float)resolution;
            float localZ = Mathf.Lerp(-halfD, halfD, v);
            for (int x = 0; x <= resolution; x++)
            {
                float u = x / (float)resolution;
                float localX = Mathf.Lerp(-halfW, halfW, u);
                float y = SampleTerrainHeight(localX, localZ, plan.Seed, plan.Width, plan.Depth);
                vertices[index] = new Vector3(localX, y, localZ);
                uvs[index] = new Vector2(localX / uvScale, localZ / uvScale);
                index++;
            }
        }

        index = 0;
        int row = resolution + 1;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int a = z * row + x;
                int b = a + 1;
                int c = a + row;
                int d = c + 1;
                // Winding faces upward in Unity's X/Y/Z coordinate system.
                triangles[index++] = a; triangles[index++] = c; triangles[index++] = b;
                triangles[index++] = b; triangles[index++] = c; triangles[index++] = d;
            }
        }

        var mesh = new Mesh { name = "ForestTerrain_" + plan.Seed };
        if (vertices.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Rendering and collision deliberately use different GameObjects/layers.
        // The visible mesh is mirrored by a dedicated static MeshCollider below;
        // both use exactly the same vertices, so there is no resampling mismatch.
        GameObject terrain = new GameObject("ForestTerrain");
        terrain.transform.SetParent(roomRoot, false);

        GameObject visual = new GameObject("ForestTerrainVisual");
        visual.transform.SetParent(terrain.transform, false);
        MeshFilter filter = visual.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;

        float maximumDisplacement = Mathf.Abs(largeBumpAmplitude) + Mathf.Abs(smallBumpAmplitude);
        float terrainMin = -maximumDisplacement - 0.02f;
        float terrainMax = maximumDisplacement + 0.02f;
        // The collider uses the EXACT same mesh as the visible ground. The old
        // TerrainCollider used a separately resampled heightmap; on the first
        // frames after a procedural spawn its contact surface could disagree
        // with the visual mesh and let a CharacterController tunnel through.
        GameObject collisionRoot = new GameObject("ForestTerrainCollision");
        collisionRoot.layer = 0;
        collisionRoot.transform.SetParent(terrain.transform, false);
        MeshCollider terrainCollider = collisionRoot.AddComponent<MeshCollider>();
        terrainCollider.convex = false;
        bool baked = false;
        try
        {
            Physics.BakeMesh(mesh.GetInstanceID(), false);
            baked = true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning("[Forest] Mesh bake failed; assigning the shared mesh without pre-bake: " + exception.Message, collisionRoot);
        }
        terrainCollider.sharedMesh = null;
        terrainCollider.sharedMesh = mesh;
        terrainCollider.enabled = true;
        collisionRoot.transform.localPosition = Vector3.zero;
        collisionRoot.transform.localRotation = Quaternion.identity;
        collisionRoot.transform.localScale = Vector3.one;

        // Independent physical backup: a grid of ordinary BoxColliders following
        // conservative local minima of the same height function. They sit only
        // 6 cm below the visible surface, so a failed MeshCollider cannot let
        // the CharacterController fall through the entire forest. The 24x24
        // grid is cheap for a single active forest and uses overlapping edges to
        // avoid hairline gaps between adjacent support tiles.
        const int supportTiles = 24;
        const int samplesPerTileAxis = 4;
        const float supportInset = 0.06f;
        float supportBottom = terrainMin - 0.35f;
        GameObject supportRoot = new GameObject("ForestTerrainSupportGrid");
        supportRoot.layer = 0;
        supportRoot.transform.SetParent(terrain.transform, false);
        int supportColliderCount = 0;
        for (int tz = 0; tz < supportTiles; tz++)
        {
            float z0 = Mathf.Lerp(-halfD, halfD, tz / (float)supportTiles);
            float z1 = Mathf.Lerp(-halfD, halfD, (tz + 1) / (float)supportTiles);
            for (int tx = 0; tx < supportTiles; tx++)
            {
                float x0 = Mathf.Lerp(-halfW, halfW, tx / (float)supportTiles);
                float x1 = Mathf.Lerp(-halfW, halfW, (tx + 1) / (float)supportTiles);
                float minimumInTile = float.PositiveInfinity;
                for (int sz = 0; sz <= samplesPerTileAxis; sz++)
                {
                    float sampleZ = Mathf.Lerp(z0, z1, sz / (float)samplesPerTileAxis);
                    for (int sx = 0; sx <= samplesPerTileAxis; sx++)
                    {
                        float sampleX = Mathf.Lerp(x0, x1, sx / (float)samplesPerTileAxis);
                        float h = SampleTerrainHeight(sampleX, sampleZ, plan.Seed, plan.Width, plan.Depth);
                        if (h < minimumInTile) minimumInTile = h;
                    }
                }

                float supportTop = Mathf.Max(supportBottom + 0.08f, minimumInTile - supportInset);
                GameObject tile = new GameObject($"Support_{tx:00}_{tz:00}");
                tile.layer = 0;
                tile.transform.SetParent(supportRoot.transform, false);
                tile.transform.localPosition = new Vector3((x0 + x1) * 0.5f,
                    (supportBottom + supportTop) * 0.5f, (z0 + z1) * 0.5f);
                BoxCollider box = tile.AddComponent<BoxCollider>();
                box.center = Vector3.zero;
                box.size = new Vector3((x1 - x0) + 0.025f, supportTop - supportBottom,
                    (z1 - z0) + 0.025f);
                box.isTrigger = false;
                box.enabled = true;
                supportColliderCount++;
            }
        }

        // Full-area emergency catch sits just below the lowest possible visible
        // terrain height. The previous version placed this slab ~0.7m below the
        // surface; if the per-tile support grid was ignored by the collision
        // matrix, the player could fall for a long time before reaching it.
        // This slab is deliberately continuous and independent of MeshCollider.
        float safetyTop = terrainMin - 0.045f;
        const float safetyThickness = 0.30f;
        GameObject safetyObject = new GameObject("ForestTerrainSafetyCatch");
        safetyObject.layer = 0;
        safetyObject.transform.SetParent(terrain.transform, false);
        BoxCollider safetyCollider = safetyObject.AddComponent<BoxCollider>();
        safetyCollider.center = new Vector3(0f, safetyTop - safetyThickness * 0.5f, 0f);
        safetyCollider.size = new Vector3(plan.Width, safetyThickness, plan.Depth);
        safetyCollider.isTrigger = false;
        safetyCollider.enabled = true;
        safetyCollider.contactOffset = 0.01f;

        Physics.SyncTransforms();
        Vector3 centerProbe = new Vector3(0f, maximumDisplacement + 2f, 0f);
        bool colliderCenterHit = ProbeAnySolidRoomFloor(roomRoot, terrain.transform, centerProbe,
            maximumDisplacement + 4f, out string centerColliderName);
        bool colliderEntranceHit = true;
        string entranceColliderName = "not-applicable";
        if (plan.HasIncomingSide)
        {
            Vector3 entry = GetEntranceProbeLocal(plan, 0.65f);
            colliderEntranceHit = ProbeAnySolidRoomFloor(roomRoot, terrain.transform,
                new Vector3(entry.x, maximumDisplacement + 2f, entry.z), maximumDisplacement + 4f,
                out entranceColliderName);
        }
        bool terrainReady = colliderCenterHit && colliderEntranceHit && safetyCollider.enabled && supportColliderCount > 0;
        _lastTerrainPhysicsRoom = roomRoot;
        _lastTerrainPhysicsReady = terrainReady;
        Debug.Log($"[Forest] Terrain physics ready: visualMesh={mesh.vertexCount} vertices/{mesh.triangles.Length / 3} triangles, " +
            $"MeshCollider={(terrainCollider.enabled && terrainCollider.sharedMesh == mesh ? "ON" : "OFF")}, baked={baked}, " +
            $"layer={collisionRoot.layer}, centerHit={colliderCenterHit}({centerColliderName}), " +
            $"entranceHit={colliderEntranceHit}({entranceColliderName}), supportTiles={supportColliderCount}, " +
            $"supportGrid=ON, entranceRunway=5.0m, backupEnabled={safetyCollider.enabled}, backupTop={safetyTop:0.000}.", collisionRoot);
        return mesh;
    }

    static bool ProbeAnySolidRoomFloor(Transform roomRoot, Transform terrainTransform, Vector3 localOrigin,
        float distance, out string colliderName)
    {
        colliderName = "none";
        if (roomRoot == null || terrainTransform == null) return false;
        Ray ray = new Ray(terrainTransform.TransformPoint(localOrigin), Vector3.down);
        RaycastHit[] hits = Physics.RaycastAll(ray, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float closest = float.PositiveInfinity;
        Collider found = null;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider candidate = hits[i].collider;
            if (candidate == null || candidate.isTrigger || candidate.name == "ForestTerrainSafetyCatch") continue;
            Transform candidateTransform = candidate.transform;
            if (candidateTransform != roomRoot && !candidateTransform.IsChildOf(roomRoot)) continue;
            if (hits[i].normal.y < 0.55f || hits[i].distance >= closest) continue;
            closest = hits[i].distance;
            found = candidate;
        }
        if (found == null) return false;
        colliderName = found.name;
        return true;
    }


    static Vector3 GetEntranceProbeLocal(ProceduralRoomPlan plan, float inwardDistance)
    {
        float halfW = plan.Width * 0.5f;
        float halfD = plan.Depth * 0.5f;
        switch (plan.IncomingSide)
        {
            case ProceduralRoomSide.North: return new Vector3(0f, 0f, halfD - inwardDistance);
            case ProceduralRoomSide.East: return new Vector3(halfW - inwardDistance, 0f, 0f);
            case ProceduralRoomSide.South: return new Vector3(0f, 0f, -halfD + inwardDistance);
            default: return new Vector3(-halfW + inwardDistance, 0f, 0f);
        }
    }

    float SampleTerrainHeight(float x, float z, int seed, float width, float depth)
    {
        float seedX = (seed & 0x7fff) * 0.0137f;
        float seedZ = ((seed >> 7) & 0x7fff) * 0.0173f;
        float large = Mathf.PerlinNoise((x + seedX) * largeNoiseFrequency + 13.4f,
            (z + seedZ) * largeNoiseFrequency + 7.2f) - 0.5f;
        float small = Mathf.PerlinNoise((x - seedZ) * smallNoiseFrequency + 3.1f,
            (z + seedX) * smallNoiseFrequency + 19.6f) - 0.5f;

        float edgeDistance = Mathf.Min(width * 0.5f - Mathf.Abs(x), depth * 0.5f - Mathf.Abs(z));
        float edgeBlend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edgeDistance / Mathf.Max(0.5f, flatDoorwayDepth)));
        return (large * 2f * largeBumpAmplitude + small * 2f * smallBumpAmplitude) * edgeBlend;
    }

    void BuildEntranceLanding(Transform roomRoot, ProceduralRoomPlan plan, float doorWidth)
    {
        if (!plan.HasIncomingSide) return;

        // A broad, level runway starts exactly at the threshold. This remains a
        // normal Default-layer BoxCollider independent from the terrain surface,
        // so the first several steps never depend on TerrainCollider cooking.
        float halfW = plan.Width * 0.5f;
        float halfD = plan.Depth * 0.5f;
        const float landingDepth = 5.0f;
        // Keep the near edge safely inside the destination. A CharacterController
        // standing just outside the doorway has a horizontal capsule radius, so a
        // collider starting exactly at the threshold can spawn partly inside its
        // skin and generate an underside contact.
        const float landingInset = 0.45f;
        float landingWidth = Mathf.Max(doorWidth + 2.0f, 3.2f);
        const float landingTop = 0.035f;
        const float landingThickness = 0.32f;
        Vector3 center = Vector3.zero;
        Vector3 size;

        switch (plan.IncomingSide)
        {
            case ProceduralRoomSide.North:
                center = new Vector3(0f, landingTop - landingThickness * 0.5f,
                    halfD - landingInset - landingDepth * 0.5f);
                size = new Vector3(landingWidth, landingThickness, landingDepth);
                break;
            case ProceduralRoomSide.South:
                center = new Vector3(0f, landingTop - landingThickness * 0.5f,
                    -halfD + landingInset + landingDepth * 0.5f);
                size = new Vector3(landingWidth, landingThickness, landingDepth);
                break;
            case ProceduralRoomSide.East:
                center = new Vector3(halfW - landingInset - landingDepth * 0.5f,
                    landingTop - landingThickness * 0.5f, 0f);
                size = new Vector3(landingDepth, landingThickness, landingWidth);
                break;
            default: // West
                center = new Vector3(-halfW + landingInset + landingDepth * 0.5f,
                    landingTop - landingThickness * 0.5f, 0f);
                size = new Vector3(landingDepth, landingThickness, landingWidth);
                break;
        }

        GameObject landing = new GameObject("ForestEntranceLandingCollider");
        landing.layer = 0;
        landing.transform.SetParent(roomRoot, false);
        BoxCollider collider = landing.AddComponent<BoxCollider>();
        collider.center = center;
        collider.size = size;
        collider.isTrigger = false;
        collider.enabled = true;
        Debug.Log($"[Forest] Entrance runway collider ready: layer={landing.layer}, enabled={collider.enabled}, " +
            $"trigger={collider.isTrigger}, size={collider.size}, bounds={collider.bounds}.", landing);
    }

    void BuildInvisibleBoundaries(Transform roomRoot, ProceduralRoomPlan plan, float doorWidth)
    {
        Transform root = new GameObject("InvisibleForestBoundaries").transform;
        root.SetParent(roomRoot, false);
        float halfW = plan.Width * 0.5f;
        float halfD = plan.Depth * 0.5f;
        float halfGap = Mathf.Max(doorWidth * 0.5f + 0.42f, 0.9f);
        BuildBoundarySide(root, ProceduralRoomSide.North, plan, halfW, halfD, halfGap);
        BuildBoundarySide(root, ProceduralRoomSide.South, plan, halfW, halfD, halfGap);
        BuildBoundarySide(root, ProceduralRoomSide.East, plan, halfW, halfD, halfGap);
        BuildBoundarySide(root, ProceduralRoomSide.West, plan, halfW, halfD, halfGap);
    }

    void BuildBoundarySide(Transform root, ProceduralRoomSide side, ProceduralRoomPlan plan,
        float halfW, float halfD, float halfGap)
    {
        float sideLength = side == ProceduralRoomSide.North || side == ProceduralRoomSide.South
            ? plan.Width : plan.Depth;
        var gaps = new List<float>();
        if (plan.HasIncomingSide && plan.IncomingSide == side) gaps.Add(0f);
        for (int i = 0; i < plan.Exits.Count; i++)
            if (plan.Exits[i].Side == side) gaps.Add(plan.Exits[i].Along);
        gaps.Sort();

        float cursor = -sideLength * 0.5f;
        for (int i = 0; i < gaps.Count; i++)
        {
            float gapStart = Mathf.Clamp(gaps[i] - halfGap, -sideLength * 0.5f, sideLength * 0.5f);
            float gapEnd = Mathf.Clamp(gaps[i] + halfGap, -sideLength * 0.5f, sideLength * 0.5f);
            AddBoundarySegment(root, side, cursor, gapStart, halfW, halfD);
            cursor = Mathf.Max(cursor, gapEnd);
        }
        AddBoundarySegment(root, side, cursor, sideLength * 0.5f, halfW, halfD);
    }

    void AddBoundarySegment(Transform root, ProceduralRoomSide side, float start, float end, float halfW, float halfD)
    {
        float length = end - start;
        if (length < 0.15f) return;
        GameObject segment = new GameObject("InvisibleWall_" + side);
        segment.transform.SetParent(root, false);
        BoxCollider collider = segment.AddComponent<BoxCollider>();
        float height = Mathf.Max(2f, invisibleBoundaryHeight);
        float thickness = Mathf.Max(0.1f, invisibleBoundaryThickness);
        float mid = (start + end) * 0.5f;
        if (side == ProceduralRoomSide.North || side == ProceduralRoomSide.South)
        {
            float z = side == ProceduralRoomSide.North ? halfD : -halfD;
            segment.transform.localPosition = new Vector3(mid, height * 0.5f, z);
            collider.size = new Vector3(length, height, thickness);
        }
        else
        {
            float x = side == ProceduralRoomSide.East ? halfW : -halfW;
            segment.transform.localPosition = new Vector3(x, height * 0.5f, mid);
            collider.size = new Vector3(thickness, height, length);
        }
    }

    List<Vector2> BuildDoorKeepOutPoints(ProceduralRoomPlan plan)
    {
        var points = new List<Vector2>();
        if (plan.HasIncomingSide) points.Add(SocketGroundPosition(plan, plan.IncomingSide, 0f));
        for (int i = 0; i < plan.Exits.Count; i++)
            points.Add(SocketGroundPosition(plan, plan.Exits[i].Side, plan.Exits[i].Along));
        return points;
    }

    static Vector2 SocketGroundPosition(ProceduralRoomPlan plan, ProceduralRoomSide side, float along)
    {
        switch (side)
        {
            case ProceduralRoomSide.North: return new Vector2(along, plan.Depth * 0.5f);
            case ProceduralRoomSide.South: return new Vector2(along, -plan.Depth * 0.5f);
            case ProceduralRoomSide.East: return new Vector2(plan.Width * 0.5f, along);
            default: return new Vector2(-plan.Width * 0.5f, along);
        }
    }

    static bool IsNearAny(Vector2 point, List<Vector2> points, float distance)
    {
        if (points == null) return false;
        float sqr = distance * distance;
        for (int i = 0; i < points.Count; i++)
            if ((point - points[i]).sqrMagnitude < sqr) return true;
        return false;
    }

    void EnsureFallbackAssets()
    {
        if (_spruceMesh != null) return;
        _barkMaterial = CreateFallbackMaterial("Forest_Bark", new Color(0.19f, 0.15f, 0.12f));
        _foliageMaterial = CreateFallbackMaterial("Forest_SpruceNeedles", new Color(0.105f, 0.19f, 0.12f));
        _bushMaterial = CreateFallbackMaterial("Forest_BushLeaves", new Color(0.16f, 0.25f, 0.13f));
        _mushroomStemMaterial = CreateFallbackMaterial("Forest_MushroomStem", new Color(0.68f, 0.61f, 0.48f));
        _mushroomCapMaterials[0] = CreateFallbackMaterial("Forest_MushroomCap_Red", new Color(0.48f, 0.105f, 0.075f));
        _mushroomCapMaterials[1] = CreateFallbackMaterial("Forest_MushroomCap_Brown", new Color(0.32f, 0.21f, 0.12f));
        _mushroomCapMaterials[2] = CreateFallbackMaterial("Forest_MushroomCap_Cream", new Color(0.68f, 0.58f, 0.39f));
        _mushroomCapMaterials[3] = CreateFallbackMaterial("Forest_MushroomCap_Purple", new Color(0.29f, 0.22f, 0.42f));
        _spruceMesh = CreateConiferMesh(false);
        _pineMesh = CreateConiferMesh(true);
        _bushMesh = CreateBushMesh();
        _mushroomMesh = CreateMushroomClusterMesh();
    }

    static Material CreateFallbackMaterial(string name, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        var material = new Material(shader) { name = name, enableInstancing = true };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.05f);
        return material;
    }

    static Mesh CreateConiferMesh(bool pine)
    {
        var vertices = new List<Vector3>();
        var bark = new List<int>();
        var leaves = new List<int>();
        float trunkHeight = pine ? 4.5f : 3.9f;
        AddCylinder(vertices, bark, Vector3.zero, trunkHeight, pine ? 0.27f : 0.31f, pine ? 0.16f : 0.20f, 7);
        if (pine)
        {
            AddConeTier(vertices, leaves, 2.0f, 1.7f, 2.15f, 1.48f, 8);
            AddConeTier(vertices, leaves, 2.75f, 1.8f, 1.75f, 1.42f, 8);
            AddConeTier(vertices, leaves, 3.45f, 1.85f, 1.35f, 1.36f, 8);
            AddConeTier(vertices, leaves, 4.1f, 1.9f, 1.0f, 1.25f, 8);
            AddConeTier(vertices, leaves, 4.8f, 1.2f, 0.55f, 1.05f, 7);
        }
        else
        {
            AddConeTier(vertices, leaves, 0.65f, 1.55f, 1.48f, 1.85f, 8);
            AddConeTier(vertices, leaves, 1.55f, 1.45f, 1.3f, 1.7f, 8);
            AddConeTier(vertices, leaves, 2.4f, 1.35f, 1.05f, 1.55f, 8);
            AddConeTier(vertices, leaves, 3.2f, 1.25f, 0.83f, 1.45f, 8);
            AddConeTier(vertices, leaves, 4.0f, 1.1f, 0.56f, 1.32f, 7);
        }
        return CreateTwoSubmeshMesh("Forest_" + (pine ? "Pine" : "Spruce"), vertices, bark, leaves);
    }

    static Mesh CreateBushMesh()
    {
        var vertices = new List<Vector3>();
        var branches = new List<int>();
        var leaves = new List<int>();
        AddCylinder(vertices, branches, new Vector3(0f, 0f, 0f), 0.7f, 0.12f, 0.06f, 6);
        AddConeTier(vertices, leaves, 0.2f, 0.68f, 0.72f, 1.2f, 7);
        AddConeTier(vertices, leaves, 0.35f, 0.72f, 0.68f, 1.1f, 7, new Vector3(-0.45f, 0f, -0.12f));
        AddConeTier(vertices, leaves, 0.42f, 0.72f, 0.7f, 1.12f, 7, new Vector3(0.42f, 0f, 0.14f));
        AddConeTier(vertices, leaves, 0.58f, 0.65f, 0.63f, 1.15f, 7, new Vector3(-0.1f, 0f, 0.42f));
        AddConeTier(vertices, leaves, 0.58f, 0.65f, 0.65f, 1.15f, 7, new Vector3(0.18f, 0f, -0.42f));
        return CreateTwoSubmeshMesh("Forest_Bush", vertices, branches, leaves);
    }

    static Mesh CreateMushroomClusterMesh()
    {
        var vertices = new List<Vector3>();
        var stems = new List<int>();
        var caps = new List<int>();
        AddMushroom(vertices, stems, caps, new Vector3(-0.33f, 0f, -0.08f), 0.85f, 0.27f, 7);
        AddMushroom(vertices, stems, caps, new Vector3(0.28f, 0f, 0.11f), 1.2f, 0.34f, 7);
        AddMushroom(vertices, stems, caps, new Vector3(0.02f, 0f, -0.34f), 0.58f, 0.19f, 6);
        return CreateTwoSubmeshMesh("Forest_MushroomCluster", vertices, stems, caps);
    }

    static void AddMushroom(List<Vector3> vertices, List<int> stems, List<int> caps,
        Vector3 offset, float height, float capRadius, int sides)
    {
        AddCylinder(vertices, stems, offset, height * 0.72f, 0.075f * height, 0.055f * height, sides, 0.03f);
        float capBase = height * 0.66f;
        AddConeTier(vertices, caps, capBase, capRadius, capRadius * 0.82f, height * 0.42f, sides, offset);
    }

    static void AddCylinder(List<Vector3> vertices, List<int> triangles, Vector3 offset,
        float height, float bottomRadius, float topRadius, int sides, float yOffset = 0f)
    {
        int start = vertices.Count;
        for (int i = 0; i < sides; i++)
        {
            float a = Mathf.PI * 2f * i / sides;
            float x = Mathf.Cos(a), z = Mathf.Sin(a);
            vertices.Add(offset + new Vector3(x * bottomRadius, yOffset, z * bottomRadius));
            vertices.Add(offset + new Vector3(x * topRadius, height + yOffset, z * topRadius));
        }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            int b0 = start + i * 2;
            int t0 = b0 + 1;
            int b1 = start + j * 2;
            int t1 = b1 + 1;
            triangles.Add(b0); triangles.Add(t1); triangles.Add(b1);
            triangles.Add(b0); triangles.Add(t0); triangles.Add(t1);
        }
    }

    static void AddConeTier(List<Vector3> vertices, List<int> triangles,
        float baseY, float radius, float topRadius, float height, int sides, Vector3 offset = default(Vector3))
    {
        int start = vertices.Count;
        float middleY = baseY + height * 0.5f;
        for (int i = 0; i < sides; i++)
        {
            float a = Mathf.PI * 2f * i / sides;
            float c = Mathf.Cos(a), s = Mathf.Sin(a);
            vertices.Add(offset + new Vector3(c * radius, baseY, s * radius));
            vertices.Add(offset + new Vector3(c * topRadius, middleY, s * topRadius));
        }
        int apex = vertices.Count;
        vertices.Add(offset + new Vector3(0f, baseY + height, 0f));
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            int b0 = start + i * 2;
            int m0 = b0 + 1;
            int b1 = start + j * 2;
            int m1 = b1 + 1;
            triangles.Add(b0); triangles.Add(m0); triangles.Add(m1);
            triangles.Add(b0); triangles.Add(m1); triangles.Add(b1);
            triangles.Add(m0); triangles.Add(apex); triangles.Add(m1);
        }

        // Closed underside: foliage and mushroom caps remain visible from a
        // first-person camera looking upward, rather than disappearing due to
        // back-face culling. Separate cap vertices preserve a crisp low-poly look.
        int capCenter = vertices.Count;
        vertices.Add(offset + new Vector3(0f, baseY, 0f));
        int capRing = vertices.Count;
        for (int i = 0; i < sides; i++)
        {
            float a = Mathf.PI * 2f * i / sides;
            vertices.Add(offset + new Vector3(Mathf.Cos(a) * radius, baseY, Mathf.Sin(a) * radius));
        }
        for (int i = 0; i < sides; i++)
        {
            int current = capRing + i;
            int next = capRing + (i + 1) % sides;
            triangles.Add(capCenter); triangles.Add(current); triangles.Add(next);
        }
    }

    static Mesh CreateTwoSubmeshMesh(string name, List<Vector3> vertices, List<int> first, List<int> second)
    {
        var mesh = new Mesh { name = name };
        mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(first, 0);
        mesh.SetTriangles(second, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    void OnDestroy()
    {
        DestroyOwned(_spruceMesh);
        DestroyOwned(_pineMesh);
        DestroyOwned(_bushMesh);
        DestroyOwned(_mushroomMesh);
        DestroyOwned(_barkMaterial);
        DestroyOwned(_foliageMaterial);
        DestroyOwned(_bushMaterial);
        DestroyOwned(_mushroomStemMaterial);
        for (int i = 0; i < _mushroomCapMaterials.Length; i++) DestroyOwned(_mushroomCapMaterials[i]);
    }

    static void DestroyOwned(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}
