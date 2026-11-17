using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Blizzard: procedural snow particle storm + wind gusts that also drive
/// the fog-of-war cone flicker. Fully procedural (no assets needed),
/// everything configurable from the Inspector.
/// </summary>
[DisallowMultipleComponent]
public class BlizzardController : MonoBehaviour
{
    [Header("Intensity (0..1)")]
    [Range(0f, 1f)] public float intensity = 0.8f;

    [Header("Wind")]
    public Vector3 windDirection = new Vector3(1f, 0f, 0.25f);
    [Tooltip("Base horizontal snow speed at full intensity (m/s).")]
    public float windSpeed = 9f;
    [Tooltip("Slow back-and-forth change of wind strength.")]
    public float gustCycleSpeed = 0.35f;

    [Header("Spawn area (box around camera focus)")]
    public Vector3 areaSize = new Vector3(60f, 20f, 60f);
    public float areaHeight = 14f;

    [Header("Particles")]
    public float maxParticles = 4000;
    public float snowflakeSize = 0.06f;
    public Color snowColor = new Color(0.9f, 0.93f, 1f);
    [Tooltip("Stretch particles along velocity for streaky blizzard look.")]
    public float streakStretch = 4f;

    [Header("Fog coupling")]
    [Tooltip("Fog density at zero intensity (still misty).")]
    public float fogDensityMin = 0.02f;
    [Tooltip("Fog density at full intensity.")]
    public float fogDensityMax = 0.075f;

    ParticleSystem _ps;
    ParticleSystemRenderer _psr;
    float _noiseSeed;

    void Awake()
    {
        _noiseSeed = Random.value * 100f;

        var all = FindObjectsByType<BlizzardController>(FindObjectsSortMode.None);
        if (all.Length > 1)
        {
            var names = new System.Text.StringBuilder();
            foreach (var b in all) names.Append(b.gameObject.name).Append(" | ");
            Debug.LogError($"[ColdCar] {all.Length} BlizzardControllers in scene ({names}) - double snow! Keep one '--- ATMOSPHERE ---'.");
        }

        BuildParticles();
        if (snowCover) BuildSnowCover();
        Debug.Log("[ColdCar] Blizzard v10 initialized (fresh snow mesh + clipped particles)");
    }

    void BuildSnowCover()
    {
        // always rebuild: stale objects saved into the scene during play mode
        // would otherwise keep the old flat mesh/material forever
        var existing = GameObject.Find("Snow_Cover");
        if (existing != null) Destroy(existing);

        float groundY = snowAutoHeight ? FindGroundY() : snowBaseHeight;
        float snowY = groundY + snowSurfaceOffset;
        Debug.Log($"[ColdCar] Snow ground Y = {groundY:0.00}, snow layer at {snowY:0.00} (auto={snowAutoHeight})");

        _snow = new GameObject("Snow_Cover");
        // NOT a child of this transform: the storm follows the camera, snow must not
        _snow.transform.SetParent(null);
        _snow.transform.position = new Vector3(transform.position.x, snowY, transform.position.z);

        var mf = _snow.AddComponent<MeshFilter>();
        mf.sharedMesh = GenerateSnowMesh();

        var r = _snow.AddComponent<MeshRenderer>();
        Shader sh = Shader.Find("Hidden/ColdCar/SnowGround");
        var mat = new Material(sh);
        mat.SetTexture("_BaseMap", GenerateSnowTexture());
        mat.SetColor("_BaseColor", new Color(snowTint.r, snowTint.g, snowTint.b, snowOpacity));
        // NOTE: use _BaseMap explicitly - mainTextureScale targets _MainTex, which
        // this shader doesn't have, and the tiling silently failed before.
        mat.SetTextureScale("_BaseMap", new Vector2(snowTextureScale, snowTextureScale));
        r.sharedMaterial = mat;
        IndoorShelterZone.ApplyWorldZones(mat);
    }

    Mesh GenerateSnowMesh()
    {
        const int N = 300; // grid segments: 0.47m per vertex at 140m - holds small drifts
        var verts = new Vector3[(N + 1) * (N + 1)];
        var uvs = new Vector2[verts.Length];
        var tris = new int[N * N * 6];

        float ox = Random.value * 1000f, oz = Random.value * 1000f;
        int i = 0;
        for (int z = 0; z <= N; z++)
        {
            for (int x = 0; x <= N; x++, i++)
            {
                float wx = (x / (float)N - 0.5f) * snowPlaneSize;
                float wz = (z / (float)N - 0.5f) * snowPlaneSize;
                verts[i] = new Vector3(wx, DriftHeight(wx + ox, wz + oz), wz);
                uvs[i] = new Vector2(x / (float)N, z / (float)N);
            }
        }

        int t = 0;
        for (int z = 0; z < N; z++)
        {
            for (int x = 0; x < N; x++)
            {
                int a = z * (N + 1) + x;
                tris[t++] = a; tris[t++] = a + N + 1; tris[t++] = a + 1;
                tris[t++] = a + 1; tris[t++] = a + N + 1; tris[t++] = a + N + 2;
            }
        }

        var m = new Mesh();
        // 90k+ vertices exceed the 16-bit index limit - without this the mesh
        // sprouts giant garbage triangles
        m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = verts;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateBounds();

        // bake fake sun into vertex colors: slopes facing the light are brighter,
        // slopes away are darker, drift valleys get ambient occlusion.
        // (the snow material is unlit, so this is what makes the relief readable)
        var normals = m.normals;
        var colors = new Color[verts.Length];
        Vector3 L = new Vector3(-0.35f, 0.85f, 0.25f).normalized;
        for (int vI = 0; vI < verts.Length; vI++)
        {
            float lit = Vector3.Dot(normals[vI], L) * 0.5f + 0.5f;         // 0..1
            float shade = Mathf.Lerp(0.35f, 1.15f, Mathf.Pow(lit, 1.4f));  // strong slope shading
            float h01 = Mathf.Clamp01(verts[vI].y / Mathf.Max(0.001f, snowDriftDepth));
            float ao = Mathf.Lerp(0.5f, 1.0f, h01);                        // valleys much darker
            float b = shade * ao;
            colors[vI] = new Color(b, b, b, 1f);
        }
        m.colors = colors;
        return m;
    }

    Texture2D GenerateSoftDotTexture()
    {
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.ARGB32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        float c = (S - 1) * 0.5f;
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) / c;
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a); // smooth falloff
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        return tex;
    }

    float DriftHeight(float x, float z)
    {
        // small snowdrifts in waves: main mounds ~3m + secondary bumps ~1.4m + ripples
        float f = snowDriftFrequency;
        float w1 = Mathf.PerlinNoise(x * f, z * f);
        float w2 = Mathf.PerlinNoise(x * f * 2.2f + 13.1f, z * f * 2.2f - 7.7f);
        float w3 = Mathf.PerlinNoise(x * f * 4.5f + 31.7f, z * f * 4.5f - 3.3f);
        float h = w1 * 0.5f + w2 * 0.32f + w3 * 0.18f;
        h = Mathf.Pow(Mathf.Clamp01(h), 1.3f); // valleys low, crests rise
        return h * snowDriftDepth;
    }

    Texture2D GenerateSnowTexture()
    {
        const int S = 512;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };

        float ox = Random.value * 1000f, oz = Random.value * 1000f;
        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float u = x / (float)S, v = y / (float)S;

                // medium drifts
                float n = Mathf.PerlinNoise(ox + u * 8f, oz + v * 8f) * 0.6f
                        + Mathf.PerlinNoise(ox + u * 24f, oz + v * 24f) * 0.25f
                        + Mathf.PerlinNoise(ox + u * 70f, oz + v * 70f) * 0.15f;
                float d = Mathf.Clamp01((n - 0.5f) * (1f + snowPatchiness * 3f) + 0.5f);

                // fine grain so the snow reads as texture, not flat white
                float grain = Mathf.PerlinNoise(ox + u * 160f, oz + v * 160f) * 0.12f - 0.06f;

                float bright = Mathf.Lerp(0.70f, 1.0f, d) + grain;

                // dips get a cold blue shade, bumps stay white
                float blueMix = 1f - d;
                float r = bright * Mathf.Lerp(1f, 0.76f, blueMix);
                float g = bright * Mathf.Lerp(1f, 0.85f, blueMix);
                float b = Mathf.Min(1f, bright * Mathf.Lerp(1f, 1.04f, blueMix));

                // sparse sparkle dots (stable, not flickering static)
                if (Mathf.PerlinNoise(ox + u * 90f, oz + v * 90f) > 0.93f) { r = g = b = 1f; }

                tex.SetPixel(x, y, new Color(r, g, b, 1f));
            }
        }
        tex.Apply();
        return tex;
    }

    void OnDestroy()
    {
        if (_snow != null) Destroy(_snow);
    }

    void BuildParticles()
    {
        // always rebuild: stale objects saved into the scene during play mode
        // would otherwise keep the old unclipped material forever
        var existing = transform.Find("Blizzard_Snow");
        if (existing != null) Destroy(existing.gameObject);

        var go = new GameObject("Blizzard_Snow");
        go.transform.SetParent(transform, false);
        _ps = go.AddComponent<ParticleSystem>();

        var main = _ps.main;
        main.loop = true;
        main.startLifetime = 4f;
        main.startSpeed = 0f;
        main.startSize = snowflakeSize;
        main.startColor = snowColor;
        main.maxParticles = (int)maxParticles;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0.02f;

        var em = _ps.emission;
        em.rateOverTime = 0f; // burst-driven, rate set in Update by intensity

        var shape = _ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = areaSize;
        shape.position = new Vector3(0f, areaHeight, 0f);

        var vel = _ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        // all components MUST be in the same (constant) mode
        vel.x = new ParticleSystem.MinMaxCurve(1f);
        vel.y = new ParticleSystem.MinMaxCurve(-0.35f);
        vel.z = new ParticleSystem.MinMaxCurve(0f);

        var noise = _ps.noise;
        noise.enabled = true;
        noise.strength = 0.6f;
        noise.frequency = 0.25f;
        noise.scrollSpeed = 0.8f;

        var rend = _ps.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Stretch;
        rend.lengthScale = streakStretch;
        rend.velocityScale = 0.15f;
        // custom shader: particles dissolve inside fog-exclusion zones (cabin stays clean)
        Shader sh = Shader.Find("Hidden/ColdCar/ParticleSnow");
        var mat = new Material(sh);
        mat.SetTexture("_BaseMap", GenerateSoftDotTexture());
        mat.SetColor("_BaseColor", snowColor);
        rend.material = mat;

        _psr = rend;
    }

    [Header("Positioning")]
    [Tooltip("Keep the storm centered on the camera's view point (works in any scene).")]
    public bool followCamera = true;

    [Header("Snow cover (ground layer)")]
    [Tooltip("Fresh snow layer - a displaced mesh with drifts and a procedural snow texture.")]
    public bool snowCover = true;
    [Tooltip("Find the ground automatically from the car's lowest point (wheels touch the road).")]
    public bool snowAutoHeight = true;
    [Tooltip("Manual base world Y of the snow layer, used when auto height finds no car.")]
    public float snowBaseHeight = 0.05f;
    [Tooltip("How high above the found ground the snow sits. Bury wheels a bit - it's a blizzard.")]
    public float snowSurfaceOffset = 0.15f;
    public float snowPlaneSize = 140f;
    [Tooltip("Depth of the drifts: crest height on top of the base. ~0.5 = knee-deep.")]
    public float snowDriftDepth = 0.55f;
    [Tooltip("Drift frequency: waves per meter. 0.3 = small snowdrifts every ~3m.")]
    public float snowDriftFrequency = 0.3f;
    [Range(0f, 1f)] public float snowOpacity = 0.94f;
    public Color snowTint = new Color(0.93f, 0.95f, 1f);
    [Tooltip("Texture tiling over the ground. Smaller = bigger, more readable features.")]
    public float snowTextureScale = 7f;
    [Tooltip("Contrast of the snow noise (0 = flat, 1 = patchy and readable).")]
    [Range(0f, 1f)] public float snowPatchiness = 0.5f;

    GameObject _snow;

    /// <summary>
    /// Ground Y: dense grid of downward raycasts over the play area + the car's
    /// lowest point. Takes a HIGH percentile (not the max, to skip car roofs)
    /// so the snow sits ABOVE every road/ground surface and never dips below it.
    /// </summary>
    float FindGroundY()
    {
        // PRIMARY: the tilemap floor - it is the surface the character walks on.
        // Percentile raycasts failed here: a big roof/shelter mesh covered >10%
        // of the sample points and dragged the snow 56 meters up.
        foreach (var map in FindObjectsByType<SnowTilemapData>(FindObjectsSortMode.None))
        {
            if (map == null || !map.gameObject.scene.IsValid()) continue;
            float y = map.transform.position.y + map.FloorHeight;
            Debug.Log($"[ColdCar] ground from tilemap '{map.name}': {y:0.00}");
            return y;
        }

        // FALLBACK: percentile raycasts + car bottom (scenes without a tilemap)
        var heights = new List<float>();
        Vector3 origin = transform.position;
        const float half = 28f;
        const int steps = 12;
        for (int ix = 0; ix <= steps; ix++)
        {
            for (int iz = 0; iz <= steps; iz++)
            {
                float fx = Mathf.Lerp(-half, half, ix / (float)steps);
                float fz = Mathf.Lerp(-half, half, iz / (float)steps);
                if (Physics.Raycast(new Vector3(origin.x + fx, 60f, origin.z + fz), Vector3.down,
                        out var hit, 120f, ~0, QueryTriggerInteraction.Ignore))
                    heights.Add(hit.point.y);
            }
        }

        GameObject carRoot = null;
        float bottom = float.PositiveInfinity;
        foreach (var rend in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var root = rend.transform.root;
            if (!root.name.ToLowerInvariant().Contains("car")) continue;
            if (carRoot == null) carRoot = root.gameObject;
            if (root.gameObject == carRoot)
                bottom = Mathf.Min(bottom, rend.bounds.min.y);
        }
        if (!float.IsInfinity(bottom)) heights.Add(bottom);

        if (heights.Count == 0) return snowBaseHeight;

        heights.Sort();
        int idx = Mathf.Min(heights.Count - 1, (int)(heights.Count * 0.9f));
        return heights[idx];
    }

    void Update()
    {
        if (_ps == null) return;

        if (followCamera && _ps != null)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 focus = cam.transform.position + cam.transform.forward * areaSize.z * 0.15f;
                // move ONLY the particle child - the root transform is shared with
                // FogOfWarController and must never drift around the scene
                _ps.transform.position = new Vector3(focus.x, focus.y - areaHeight, focus.z);
            }
        }

        float gust = 0.75f + 0.25f * Mathf.PerlinNoise(Time.time * gustCycleSpeed + _noiseSeed, 0f);
        float inten = Mathf.Clamp01(intensity) * gust;

        var main = _ps.main;
        main.maxParticles = (int)maxParticles;
        main.startSize = snowflakeSize;
        main.startColor = snowColor;

        var em = _ps.emission;
        em.rateOverTime = Mathf.Lerp(0f, maxParticles / 6f, inten);

        Vector3 wind = windDirection.normalized * (windSpeed * inten);
        var vel = _ps.velocityOverLifetime;
        vel.x = wind.x;
        vel.z = wind.z;

        if (_snow != null)
        {
            var snowRenderer = _snow.GetComponent<MeshRenderer>();
            if (snowRenderer != null) IndoorShelterZone.ApplyWorldZones(snowRenderer.sharedMaterial);
        }

        if (_psr != null)
        {
            IndoorShelterZone.ApplyWorldZones(_psr.material);
            _psr.lengthScale = streakStretch * (0.4f + inten);
            if (_psr.material.HasProperty("_BaseColor")) _psr.material.SetColor("_BaseColor", snowColor);
        }

        var shape = _ps.shape;
        shape.scale = areaSize;
        shape.position = new Vector3(0f, areaHeight, 0f);

        // couple fog density to blizzard intensity
        if (FogOfWarController.Instance == null || FogOfWarController.Instance.enabled == false)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
        }
        if (RenderSettings.fogMode == FogMode.Exponential)
            RenderSettings.fogDensity = Mathf.Lerp(fogDensityMin, fogDensityMax, inten);
    }

    public void SetIntensity(float v) { intensity = Mathf.Clamp01(v); }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.6f, 0.8f, 1f, 0.35f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * areaHeight, areaSize);
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, windDirection.normalized * 4f);
    }
}
