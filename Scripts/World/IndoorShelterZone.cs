using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Oriented shelter volume shared by the fog-of-war and weather systems.
/// The BoxCollider is the source of truth; no screen-space AABB is used.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class IndoorShelterZone : MonoBehaviour
{
    public const int MaxShaderZones = 32;
    private static readonly HashSet<IndoorShelterZone> ActiveZones = new HashSet<IndoorShelterZone>();
    private static bool _emptyRegistryRefreshAttempted;

    [SerializeField, HideInInspector]
    private bool generatedByTilemapBuilder;

    /// <summary>True only for zones owned by the automatic tilemap rebuild.</summary>
    public bool IsGeneratedByTilemapBuilder => generatedByTilemapBuilder ||
        name.StartsWith("Защищённое помещение ", StringComparison.Ordinal);

    /// <summary>Marks an automatically generated zone so the builder can safely replace it.</summary>
    public void MarkGeneratedByTilemapBuilder() => generatedByTilemapBuilder = true;

    [Header("Параметры защищённой зоны")]
    [SerializeField, Tooltip("Отключает влияние зоны на туман, снег и звук.")]
    private bool protectionEnabled = true;

    [Range(0f, 1f), Tooltip("Общая сила защиты от погодных эффектов.")]
    public float shelterFactor = 0.94f;

    [Range(0f, 1f), Tooltip("Доля тумана войны, которая рассеивается внутри здания. 0.65 = ослабление тумана на 65%.")]
    public float fogRevealFactor = 0.65f;

    [Min(0f), Tooltip("Ширина мягкого перехода ВНУТРЬ помещения от границы пола, в метрах. Снаружи footprint пол не подсвечивается.")]
    public float fogEdgeFeather = 0.75f;

    [Min(0f), Tooltip("Небольшое горизонтальное/нижнее расширение объёма только для FogOfWar, чтобы включить толщину стен и перепад пола. Не влияет на снег и звук.")]
    public float fogVolumePadding = 0.15f;

    [Min(0f), Tooltip("Дополнительная высота вверх над BoxCollider только для FogOfWar. Помогает ослаблять туман на верхних частях стен/крыше, не расширяя зону по полу.")]
    public float fogVerticalPadding = 1.0f;

    [Range(0f, 1f), Tooltip("Насколько сильно метель приглушается внутри помещения.")]
    public float acousticMuffleFactor = 0.92f;

    private BoxCollider _box;
    public bool ProtectionEnabled => protectionEnabled && isActiveAndEnabled;

    private static readonly int IndoorZoneCountID = Shader.PropertyToID("_IndoorZoneCount");
    private static readonly int IndoorWorldToLocalID = Shader.PropertyToID("_IndoorWorldToLocal");
    private static readonly int IndoorBoxCenterID = Shader.PropertyToID("_IndoorBoxCenter");
    private static readonly int IndoorBoxHalfSizeID = Shader.PropertyToID("_IndoorBoxHalfSize");
    private static readonly int IndoorFogRevealFactorID = Shader.PropertyToID("_IndoorFogRevealFactor");
    private static readonly int IndoorBoxScaleID = Shader.PropertyToID("_IndoorBoxScale");

    private static readonly Matrix4x4[] WorldToLocal = new Matrix4x4[MaxShaderZones];
    private static readonly Vector4[] BoxCenters = new Vector4[MaxShaderZones];
    private static readonly Vector4[] BoxHalfSizes = new Vector4[MaxShaderZones];
    private static readonly Vector4[] FogRevealFactors = new Vector4[MaxShaderZones];
    private static readonly Vector4[] BoxScales = new Vector4[MaxShaderZones];

    private void Awake() => EnsureCollider();
    private void Reset() => EnsureCollider();
    private void OnValidate() => EnsureCollider();

    private void OnEnable()
    {
        EnsureCollider();
        ActiveZones.Add(this);
        _emptyRegistryRefreshAttempted = false;
    }

    private void OnDisable()
    {
        ActiveZones.Remove(this);
        if (ActiveZones.Count == 0) _emptyRegistryRefreshAttempted = false;
    }

    private void OnDestroy()
    {
        ActiveZones.Remove(this);
        if (ActiveZones.Count == 0) _emptyRegistryRefreshAttempted = false;
    }

    // Static registries can be empty after entering Play Mode with domain reload
    // disabled. Refresh once on demand; OnEnable resets the flag for later additions.
    private static void EnsureActiveZoneRegistry()
    {
        ActiveZones.RemoveWhere(zone => zone == null || !zone.ProtectionEnabled);
        if (ActiveZones.Count > 0 || _emptyRegistryRefreshAttempted) return;
        _emptyRegistryRefreshAttempted = true;
        IndoorShelterZone[] found = UnityEngine.Object.FindObjectsByType<IndoorShelterZone>(FindObjectsSortMode.None);
        foreach (IndoorShelterZone zone in found)
        {
            if (zone != null && zone.ProtectionEnabled)
                ActiveZones.Add(zone);
        }
    }

    private void EnsureCollider()
    {
        if (_box == null) _box = GetComponent<BoxCollider>();
        if (_box != null) _box.isTrigger = true;
    }

    /// <summary>True when a world point is inside this oriented shelter volume.</summary>
    public bool ContainsWorldPoint(Vector3 worldPosition, float verticalPadding = 0f, float horizontalPadding = 0f)
    {
        EnsureCollider();
        if (!ProtectionEnabled || _box == null) return false;

        Vector3 local = transform.InverseTransformPoint(worldPosition) - _box.center;
        Vector3 half = _box.size * 0.5f;
        Vector3 scale = transform.lossyScale;
        float localVerticalPadding = Mathf.Max(0f, verticalPadding) /
                                     Mathf.Max(0.0001f, Mathf.Abs(scale.y));
        float localHorizontalPaddingX = Mathf.Max(0f, horizontalPadding) /
                                        Mathf.Max(0.0001f, Mathf.Abs(scale.x));
        float localHorizontalPaddingZ = Mathf.Max(0f, horizontalPadding) /
                                        Mathf.Max(0.0001f, Mathf.Abs(scale.z));
        half.x += localHorizontalPaddingX;
        half.y += localVerticalPadding;
        half.z += localHorizontalPaddingZ;
        return Mathf.Abs(local.x) <= half.x &&
               Mathf.Abs(local.y) <= half.y &&
               Mathf.Abs(local.z) <= half.z;
    }

    public static bool IsPositionInsideAnyZone(Vector3 worldPosition, float verticalPadding = 0f)
    {
        foreach (IndoorShelterZone zone in ActiveZones)
        {
            if (zone != null && zone.ContainsWorldPoint(worldPosition, verticalPadding))
                return true;
        }
        return false;
    }

    /// <summary>Find the active indoor zone containing the player's eye point.</summary>
    public static bool TryGetContainingZone(Vector3 worldPosition, float verticalPadding, out IndoorShelterZone found)
    {
        EnsureActiveZoneRegistry();
        found = null;
        float smallestVolume = float.PositiveInfinity;
        foreach (IndoorShelterZone zone in ActiveZones)
        {
            if (zone == null || !zone.ProtectionEnabled || !zone.ContainsWorldPoint(worldPosition, verticalPadding))
                continue;

            zone.EnsureCollider();
            if (zone._box == null) continue;
            Vector3 size = Vector3.Scale(zone._box.size, zone.transform.lossyScale);
            float volume = Mathf.Abs(size.x * size.y * size.z);
            if (volume < smallestVolume)
            {
                smallestVolume = volume;
                found = zone;
            }
        }
        return found != null;
    }

    /// <summary>Get local box data for the zone-scoped wall cutaway shader.</summary>
    public bool TryGetCutawayBox(out Matrix4x4 worldToLocal, out Vector3 center, out Vector3 halfSize)
    {
        EnsureCollider();
        if (!ProtectionEnabled || _box == null)
        {
            worldToLocal = Matrix4x4.identity;
            center = Vector3.zero;
            halfSize = Vector3.zero;
            return false;
        }

        worldToLocal = transform.worldToLocalMatrix;
        center = _box.center;
        halfSize = _box.size * 0.5f;
        return true;
    }

    /// <summary>Strongest acoustic shelter value at the listener position.</summary>
    public static float GetAcousticMuffleAt(Vector3 worldPosition)
    {
        float muffle = 0f;
        foreach (IndoorShelterZone zone in ActiveZones)
        {
            if (zone == null || !zone.ProtectionEnabled) continue;
            if (zone.ContainsWorldPoint(worldPosition, 0.2f))
                muffle = Mathf.Max(muffle, Mathf.Clamp01(zone.acousticMuffleFactor));
        }
        return muffle;
    }


    public Bounds WorldBounds
    {
        get
        {
            EnsureCollider();
            if (_box == null) return new Bounds(transform.position, Vector3.zero);

            Vector3 ext = _box.size * 0.5f;
            Bounds bounds = new Bounds(transform.TransformPoint(_box.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = _box.center + new Vector3(
                    (i & 1) != 0 ? ext.x : -ext.x,
                    (i & 2) != 0 ? ext.y : -ext.y,
                    (i & 4) != 0 ? ext.z : -ext.z);
                bounds.Encapsulate(transform.TransformPoint(corner));
            }
            return bounds;
        }
    }

    /// <summary>
    /// Send exact oriented box data to fog and snow materials.
    /// _IndoorFogRevealFactor.xy = reveal amount and edge feather width (metres).
    /// _IndoorBoxScale.xyz converts local X/Z edge distances back to world metres.
    /// </summary>
    public static int ApplyWorldZones(Material material)
    {
        return ApplyWorldZonesInternal(material, false);
    }

    /// <summary>Send oriented boxes plus fog reveal/feather data to the fog overlay.</summary>
    public static int ApplyFogWorldZones(Material material)
    {
        return ApplyWorldZonesInternal(material, true);
    }

    /// <summary>
    /// Fog variant with player-position gating: only zones CONTAINING the
    /// character body/root probe are pushed to the shader. The reveal remains
    /// active while the player is inside that building, not while merely nearby.
    /// </summary>
    public static int ApplyFogWorldZones(Material material, Vector3? eyePosition)
    {
        return ApplyWorldZonesInternal(material, true, eyePosition, null);
    }

    /// <summary>Fog variant accepting either the character root or eye anchor as indoor probes.</summary>
    public static int ApplyFogWorldZones(Material material, Vector3 bodyPosition, Vector3 eyePosition)
    {
        return ApplyWorldZonesInternal(material, true, bodyPosition, eyePosition);
    }

    private static int ApplyWorldZonesInternal(Material material, bool includeFogData,
        Vector3? primaryProbe = null, Vector3? secondaryProbe = null)
    {
        if (material == null) return 0;

        EnsureActiveZoneRegistry();
        int count = 0;
        foreach (IndoorShelterZone zone in ActiveZones)
        {
            if (zone == null || !zone.ProtectionEnabled) continue;
            // Fog gating: either actor body/root OR eye anchor may prove the
            // character is inside. A small XZ tolerance handles pivot offsets at
            // walls without revealing a neighbouring building from far away.
            if (primaryProbe.HasValue || secondaryProbe.HasValue)
            {
                bool primaryInside = primaryProbe.HasValue &&
                    zone.ContainsWorldPoint(primaryProbe.Value, 0.8f, 0.25f);
                bool secondaryInside = secondaryProbe.HasValue &&
                    zone.ContainsWorldPoint(secondaryProbe.Value, 0.8f, 0.25f);
                if (!primaryInside && !secondaryInside) continue;
            }
            zone.EnsureCollider();
            if (zone._box == null) continue;

            Vector3 scale = zone.transform.lossyScale;
            WorldToLocal[count] = zone.transform.worldToLocalMatrix;
            Vector3 center = zone._box.center;
            Vector3 half = zone._box.size * 0.5f;
            if (includeFogData)
            {
                float padding = Mathf.Max(0f, zone.fogVolumePadding);
                float verticalUp = Mathf.Max(0f, zone.fogVerticalPadding);
                float sx = Mathf.Max(0.0001f, Mathf.Abs(scale.x));
                float sy = Mathf.Max(0.0001f, Mathf.Abs(scale.y));
                float sz = Mathf.Max(0.0001f, Mathf.Abs(scale.z));
                half.x += padding / sx;
                half.y += (padding + verticalUp * 0.5f) / sy;
                half.z += padding / sz;
                // Extend only the upper side by fogVerticalPadding; keep the floor
                // footprint and lower boundary anchored to the actual collider.
                center.y += (verticalUp * 0.5f) / sy;
            }
            BoxCenters[count] = new Vector4(center.x, center.y, center.z, 0f);
            BoxHalfSizes[count] = new Vector4(half.x, half.y, half.z, 0f);
            // z = fogVolumePadding: the shader measures the edge distance from the
            // TRUE footprint boundary so the ramp runs through the wall thickness
            FogRevealFactors[count] = new Vector4(
                Mathf.Clamp01(zone.fogRevealFactor), Mathf.Max(0f, zone.fogEdgeFeather),
                includeFogData ? Mathf.Max(0f, zone.fogVolumePadding) : 0f, 0f);
            BoxScales[count] = new Vector4(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z), 0f);

            count++;
            if (count >= MaxShaderZones) break;
        }

        material.SetInt(IndoorZoneCountID, count);
        material.SetMatrixArray(IndoorWorldToLocalID, WorldToLocal);
        material.SetVectorArray(IndoorBoxCenterID, BoxCenters);
        material.SetVectorArray(IndoorBoxHalfSizeID, BoxHalfSizes);
        if (includeFogData)
        {
            material.SetVectorArray(IndoorFogRevealFactorID, FogRevealFactors);
            material.SetVectorArray(IndoorBoxScaleID, BoxScales);
        }
        return count;
    }
}
