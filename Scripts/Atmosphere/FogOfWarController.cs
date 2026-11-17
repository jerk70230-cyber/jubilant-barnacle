using UnityEngine;
using System;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Experimental.Rendering;
using UnityEngine.InputSystem;

/// <summary>
/// Fog of war with TRUE FIRST-PERSON 3D vision logic.
/// The isometric camera is irrelevant to the fog: visibility is computed from
/// the CHARACTER'S EYES in world space, exactly like a first-person game:
///   - a view cone from the eyes along the facing direction (world-space angle
///     and range, softened at both edges),
///   - occluded by the dedicated wall colliders ('__Коллизии_стен') as real
///     3D segment-vs-box tests with the walls' actual height,
///   - a small always-visible radius around the character.
/// Every number below is exposed for manual tuning.
/// diagnosticMode: 0 off, 1 final visibility, 2 wall LOS, 3 indoor reveal, 4 wall raster mask.
/// </summary>
[DisallowMultipleComponent]
public class FogOfWarController : MonoBehaviour
{
    public static FogOfWarController Instance { get; private set; }

    [Header("References")]
    [Tooltip("Fallback eye anchor when no player character exists.")]
    public Transform car;
    public Camera viewCamera;
    [Tooltip("Optional first-person eye/camera transform. Used when no animated third-person character is present.")]
    public Transform visionTarget;
    [Tooltip("The vision cone follows the player character's eyes.")]
    public bool followCharacter = true;

    [Header("Eyes (first-person vision origin)")]
    [Tooltip("Eye height above the character's feet, meters.")]
    public float eyeHeight = 1.55f;

    [Header("View cone (world-space, from the eyes)")]
    [Tooltip("FULL cone angle in degrees (like a flashlight FOV). 110 = wide flashlight.")]
    [Range(10f, 360f)] public float viewConeAngleDeg = 110f;
    [Tooltip("Use the full camera forward vector, including pitch, instead of flattening vision to the floor plane.")]
    public bool use3DViewCone = true;
    [Tooltip("Angular width of the cone's soft edge, degrees.")]
    [Range(1f, 45f)] public float coneEdgeAngleDeg = 12f;
    [Tooltip("How far the eyes see, meters (first-person distance).")]
    public float viewRange = 14f;
    [Tooltip("Soft edge of the vision range, meters.")]
    [Range(0.2f, 6f)] public float viewRangeEdge = 1.5f;

    [Header("Point-blank vision (always visible around the character)")]
    public float nearRadius = 2.2f;
    [Range(0.1f, 4f)] public float nearFeather = 1.2f;

    [Header("Dirty windows / vision resistance")]
    [Range(0f, 1f)] public float windowVisibility = 1f;
    [Tooltip("Play-mode keys to test dirty windows: '[' decreases, ']' increases.")]
    public bool windowTestKeys = true;
    [Tooltip("Goggles/helmets: shrinks range and angle.")]
    [Range(0f, 1f)] public float visibilityResistance = 0f;

    [Header("Walls (the ONLY occluders, first-person LOS)")]
    public Transform wallOcclusionRoot;
    public bool blockVisionWithWalls = true;
    [Tooltip("Tolerance for a pixel lying ON a wall surface (keeps the visible wall face lit).")]
    [Range(0f, 0.3f)] public float wallSurfaceTolerance = 0.06f;

    [Header("Isometric building cutaway")]
    [Tooltip("Optional: hide the east wall face while inside a building. Disabled by default so all four walls remain visible indoors.")]
    public bool isometricWallCutaway = false;

    [Header("Look")]
    [Range(0f, 1f)] public float darkness = 1f;
    [Tooltip("Keep channels NEUTRAL and near-zero, or PS1 quantization tints the dark teal/blue.")]
    public Color nightColor = new Color(0.006f, 0.006f, 0.008f);
    [Tooltip("Gamma-lift of revealed surfaces: higher = dark objects show their color more.")]
    [Range(1f, 4f)] public float revealBrightness = 2.2f;
    [Tooltip("Flat lift of revealed surfaces.")]
    [Range(0f, 0.15f)] public float revealLift = 0.05f;

    [Header("Scene mood (applied on Start)")]
    public Color ambientColor = new Color(0.05f, 0.07f, 0.10f);
    public float fogDensity = 0.045f;
    public Color fogColor = new Color(0.55f, 0.60f, 0.68f);
    [Range(0f, 1f)] public float directionalLightDim = 0.15f;

    [Header("Diagnostics (0 off, 1 visibility, 2 wall LOS, 3 indoor reveal, 4 raster wall mask)")]
    public int diagnosticMode = 0;

    static readonly int EyePosID = Shader.PropertyToID("_EyePos");
    static readonly int ViewDirID = Shader.PropertyToID("_ViewDir");
    static readonly int Use3DViewConeID = Shader.PropertyToID("_Use3DViewCone");
    static readonly int ConeCosInID = Shader.PropertyToID("_ConeCosIn");
    static readonly int ConeCosOutID = Shader.PropertyToID("_ConeCosOut");
    static readonly int ConeRangeID = Shader.PropertyToID("_ConeRange");
    static readonly int ConeRangeEdgeID = Shader.PropertyToID("_ConeRangeEdge");
    static readonly int NearRadiusID = Shader.PropertyToID("_NearRadius");
    static readonly int NearFeatherID = Shader.PropertyToID("_NearFeather");
    static readonly int BlockVisionWithWallsID = Shader.PropertyToID("_BlockVisionWithWalls");
    static readonly int WallRootWorldToLocalID = Shader.PropertyToID("_WallRootWorldToLocal");
    static readonly int WallBoxCountID = Shader.PropertyToID("_WallBoxCount");
    static readonly int WallBoxCenterID = Shader.PropertyToID("_WallBoxCenter");
    static readonly int WallBoxHalfSizeID = Shader.PropertyToID("_WallBoxHalfSize");
    static readonly int WallSurfTolID = Shader.PropertyToID("_WallSurfTol");
    static readonly int GroundYID = Shader.PropertyToID("_GroundY");
    static readonly int DarknessID = Shader.PropertyToID("_Darkness");
    static readonly int DarkColorID = Shader.PropertyToID("_DarkColor");
    static readonly int RevealBrightnessID = Shader.PropertyToID("_RevealBrightness");
    static readonly int RevealLiftID = Shader.PropertyToID("_RevealLift");
    static readonly int DiagModeID = Shader.PropertyToID("_DiagMode");
    static readonly int FogWallRevealEnabledID = Shader.PropertyToID("_FogWallRevealEnabled");
    static readonly int FogWallZoneWorldToLocal0ID = Shader.PropertyToID("_FogWallZoneWorldToLocal0");
    static readonly int FogWallZoneWorldToLocal1ID = Shader.PropertyToID("_FogWallZoneWorldToLocal1");
    static readonly int FogWallZoneWorldToLocal2ID = Shader.PropertyToID("_FogWallZoneWorldToLocal2");
    static readonly int FogWallZoneCenterID = Shader.PropertyToID("_FogWallZoneCenter");
    static readonly int FogWallZoneHalfSizeID = Shader.PropertyToID("_FogWallZoneHalfSize");
    static readonly int FogWallZoneScaleID = Shader.PropertyToID("_FogWallZoneScale");
    static readonly int FogWallRevealFactorID = Shader.PropertyToID("_FogWallRevealFactor");
    static readonly int FogWallEdgeFeatherID = Shader.PropertyToID("_FogWallEdgeFeather");
    static readonly int FogWallRevealMaskID = Shader.PropertyToID("_FogWallRevealMask");
    static readonly int CameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");

    static readonly int CutawayEnabledID = Shader.PropertyToID("_CutawayEnabled");
    static readonly int CutawayPlayerPositionID = Shader.PropertyToID("_CutawayPlayerPosition");
    static readonly int CutawayCameraPositionID = Shader.PropertyToID("_CutawayCameraPosition");
    static readonly int CutawayWorldToLocal0ID = Shader.PropertyToID("_CutawayWorldToLocal0");
    static readonly int CutawayWorldToLocal1ID = Shader.PropertyToID("_CutawayWorldToLocal1");
    static readonly int CutawayWorldToLocal2ID = Shader.PropertyToID("_CutawayWorldToLocal2");
    static readonly int CutawayZoneCenterID = Shader.PropertyToID("_CutawayZoneCenter");
    static readonly int CutawayZoneHalfSizeID = Shader.PropertyToID("_CutawayZoneHalfSize");
    static readonly int CutawayDistanceID = Shader.PropertyToID("_CutawayDistance");

    const int MaxWallBoxes = 64;
    const int WallOcclusionLayer = 31;
    const float WallCutawayDistance = 0.04f;

    Renderer[] _wallCutawayRenderers;
    IndoorShelterZone[] _wallCutawayZones;
    Transform _wallCutawaySearchRoot;
    MaterialPropertyBlock _wallCutawayBlock;
    string _lastWallRevealZoneLog = "<uninitialized>";

    Material _mat;
    FogPass _pass;
    float _baseDirLightIntensity = -1f;

    void Awake()
    {
        // UnityEngine.Object-backed objects must not be created in a MonoBehaviour field initializer.
        _wallCutawayBlock = new MaterialPropertyBlock();
        Instance = this;
        ResolveCar();
        ResolveWallOcclusionSource();
        if (viewCamera == null) viewCamera = Camera.main;
        ApplyDarkness();
    }

    void ResolveCar()
    {
        if (car != null) return;
        var best = ColdCarBootstrap.FindHeroCar(viewCamera);
        if (best != null)
        {
            car = best.transform;
            Debug.Log($"[ColdCar] FogOfWar eye anchor auto-assigned to '{car.name}'");
        }
    }

    void ValidateSingleInstance()
    {
        var all = FindObjectsByType<FogOfWarController>(FindObjectsSortMode.None);
        if (all.Length <= 1) return;
        FogOfWarController keeper = all[0];
        foreach (var c in all)
            if (c.GetInstanceID() < keeper.GetInstanceID()) keeper = c;
        if (this == keeper)
        {
            var names = new System.Text.StringBuilder();
            foreach (var c in all)
                if (c != this) names.Append(c.gameObject.name).Append(" | ");
            Debug.LogError($"[ColdCar] Duplicate atmosphere objects deactivated: {names}");
            foreach (var c in all)
                if (c != keeper) c.gameObject.SetActive(false);
        }
    }

    void ApplyDarkness()
    {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = ambientColor;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = fogDensity;
        RenderSettings.fogColor = fogColor;
        if (directionalLightDim < 1f)
        {
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type == LightType.Directional)
                    l.intensity *= directionalLightDim;
            }
        }
    }

    void ResolveWallOcclusionSource()
    {
        if (wallOcclusionRoot == null)
        {
            Transform wallRoot = GameObject.Find("__Коллизии_стен")?.transform;
            if (wallRoot == null)
            {
                foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                    if (t.name.Contains("Коллизии_стен")) { wallRoot = t; break; }
            }
            if (wallRoot != null) wallOcclusionRoot = wallRoot;
        }

        _wallBoxes = wallOcclusionRoot != null
            ? wallOcclusionRoot.GetComponents<BoxCollider>()
            : System.Array.Empty<BoxCollider>();

        // Walls are PHYSICAL: solid boxes block the CharacterController.
        foreach (var box in _wallBoxes)
            if (box != null && box.isTrigger) box.isTrigger = false;

        if (_wallBoxes.Length == 0 && !_wallSourceWarningLogged)
        {
            _wallSourceWarningLogged = true;
            Debug.LogWarning("[ColdCar] Wall LOS has no BoxColliders. Assign '__Коллизии_стен' to Wall Occlusion Root.", this);
        }
        else if (_wallBoxes.Length > 0)
        {
            Debug.Log($"[ColdCar] Wall LOS source '{wallOcclusionRoot.name}': {_wallBoxes.Length} wall colliders (solid).", this);
        }
    }

    BoxCollider[] _wallBoxes;
    bool _wallSourceWarningLogged;

    /// <summary>Refreshes generated wall colliders and renderers after a procedural room is built or unloaded.</summary>
    public void RefreshProceduralBindings()
    {
        ResolveWallOcclusionSource();
        _wallCutawayRenderers = null;
        _wallCutawayZones = null;
        _wallCutawaySearchRoot = null;
        ResolveWallCutawayRenderers();
    }

    void ResolveWallCutawayRenderers()
    {
        Transform searchRoot = wallOcclusionRoot != null ? wallOcclusionRoot.parent : null;
        if (searchRoot == null)
        {
            _wallCutawayRenderers = Array.Empty<Renderer>();
            _wallCutawayZones = Array.Empty<IndoorShelterZone>();
            _wallCutawaySearchRoot = null;
            return;
        }
        if (_wallCutawayRenderers != null && _wallCutawaySearchRoot == searchRoot && _wallCutawayZones != null)
            return;

        _wallCutawaySearchRoot = searchRoot;
        _wallCutawayZones = FindObjectsByType<IndoorShelterZone>(FindObjectsSortMode.None);
        var allRenderers = searchRoot.GetComponentsInChildren<Renderer>(true);
        var wallRenderers = new System.Collections.Generic.List<Renderer>();
        foreach (Renderer renderer in allRenderers)
        {
            if (renderer == null) continue;
            bool hasWallMaterial = false;
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null && material.name.StartsWith("WallMat_", StringComparison.OrdinalIgnoreCase))
                {
                    hasWallMaterial = true;
                    break;
                }
            }
            if (hasWallMaterial) wallRenderers.Add(renderer);
        }
        _wallCutawayRenderers = wallRenderers.ToArray();
        Debug.Log($"[ColdCar] Indoor wall cutaway: found {_wallCutawayRenderers.Length} wall renderers under '{searchRoot.name}'.", this);
    }

    void ApplyIndoorWallCutaway(Vector3 eyePosition, Vector3 cameraPosition)
    {
        ResolveWallCutawayRenderers();
        if (_wallCutawayRenderers == null || _wallCutawayRenderers.Length == 0) return;

        foreach (Renderer renderer in _wallCutawayRenderers)
        {
            if (renderer == null) continue;

            // The generated renderer may contain walls from the whole tilemap,
            // so choose the zone containing the PLAYER, not merely any zone that
            // intersects the renderer's large combined bounds.
            IndoorShelterZone matchingZone = null;
            float bestDistanceSq = float.PositiveInfinity;
            if (_wallCutawayZones != null)
            {
                foreach (IndoorShelterZone candidate in _wallCutawayZones)
                {
                    if (candidate == null || !candidate.ProtectionEnabled) continue;
                    if (!candidate.ContainsWorldPoint(eyePosition, 0.6f)) continue;
                    float distanceSq = (candidate.WorldBounds.center - eyePosition).sqrMagnitude;
                    if (distanceSq < bestDistanceSq)
                    {
                        bestDistanceSq = distanceSq;
                        matchingZone = candidate;
                    }
                }
            }

            Matrix4x4 worldToLocal = Matrix4x4.identity;
            Vector3 zoneCenter = Vector3.zero;
            Vector3 zoneHalfSize = Vector3.zero;
            bool hasMatchingZone = matchingZone != null &&
                matchingZone.TryGetCutawayBox(out worldToLocal, out zoneCenter, out zoneHalfSize);
            bool playerInsideMatchingZone = hasMatchingZone &&
                matchingZone.ContainsWorldPoint(eyePosition, 0.6f);

            renderer.GetPropertyBlock(_wallCutawayBlock);

            // Preserve optional cutaway behaviour, but feed the wall-mask pass
            // the exact active shelter volume through this renderer's MPB.
            bool cutawayEnabledForRenderer = isometricWallCutaway &&
                hasMatchingZone && matchingZone.ContainsWorldPoint(eyePosition, 0f);
            _wallCutawayBlock.SetFloat(CutawayEnabledID, cutawayEnabledForRenderer ? 1f : 0f);
            _wallCutawayBlock.SetFloat(FogWallRevealEnabledID, playerInsideMatchingZone ? 1f : 0f);

            if (hasMatchingZone)
            {
                Vector3 scale = matchingZone.transform.lossyScale;
                float sx = Mathf.Max(0.0001f, Mathf.Abs(scale.x));
                float sy = Mathf.Max(0.0001f, Mathf.Abs(scale.y));
                float sz = Mathf.Max(0.0001f, Mathf.Abs(scale.z));

                // Keep X/Z on the true footprint perimeter so only the actual
                // boundary wall mesh is selected. Expand Y to the configured fog
                // ceiling: the visible wall mesh is 3 m tall while its physical
                // LOS colliders / shelter box are only 2 m tall in this scene.
                Vector3 revealCenter = zoneCenter;
                Vector3 revealHalfSize = zoneHalfSize;
                float verticalUp = Mathf.Max(0f, matchingZone.fogVerticalPadding);
                float horizontalPadding = Mathf.Max(0f, matchingZone.fogVolumePadding);
                revealCenter.y += (verticalUp * 0.5f) / sy;
                revealHalfSize.y += (horizontalPadding + verticalUp * 0.5f) / sy;

                // Store the matrix as three explicit vector properties. These are
                // ordinary ShaderLab properties, so MaterialPropertyBlock reliably
                // overrides them for the combined wall renderer on every camera.
                _wallCutawayBlock.SetVector(FogWallZoneWorldToLocal0ID, worldToLocal.GetRow(0));
                _wallCutawayBlock.SetVector(FogWallZoneWorldToLocal1ID, worldToLocal.GetRow(1));
                _wallCutawayBlock.SetVector(FogWallZoneWorldToLocal2ID, worldToLocal.GetRow(2));
                _wallCutawayBlock.SetVector(FogWallZoneCenterID, revealCenter);
                _wallCutawayBlock.SetVector(FogWallZoneHalfSizeID, revealHalfSize);
                _wallCutawayBlock.SetVector(FogWallZoneScaleID,
                    new Vector4(sx, sy, sz, 0f));
                _wallCutawayBlock.SetFloat(FogWallRevealFactorID, matchingZone.fogRevealFactor);
                _wallCutawayBlock.SetFloat(FogWallEdgeFeatherID, matchingZone.fogEdgeFeather);
            }
            else
            {
                _wallCutawayBlock.SetFloat(FogWallRevealFactorID, 0f);
                _wallCutawayBlock.SetFloat(FogWallEdgeFeatherID, 0f);
            }

            if (cutawayEnabledForRenderer)
            {
                _wallCutawayBlock.SetVector(CutawayPlayerPositionID, eyePosition);
                _wallCutawayBlock.SetVector(CutawayCameraPositionID, cameraPosition);
                _wallCutawayBlock.SetVector(CutawayWorldToLocal0ID, worldToLocal.GetRow(0));
                _wallCutawayBlock.SetVector(CutawayWorldToLocal1ID, worldToLocal.GetRow(1));
                _wallCutawayBlock.SetVector(CutawayWorldToLocal2ID, worldToLocal.GetRow(2));
                _wallCutawayBlock.SetVector(CutawayZoneCenterID, zoneCenter);
                _wallCutawayBlock.SetVector(CutawayZoneHalfSizeID, zoneHalfSize);
                _wallCutawayBlock.SetFloat(CutawayDistanceID, WallCutawayDistance);
            }

            renderer.SetPropertyBlock(_wallCutawayBlock);

            string activeZoneName = playerInsideMatchingZone ? matchingZone.name : "<outside>";
            if (!string.Equals(_lastWallRevealZoneLog, activeZoneName, StringComparison.Ordinal))
            {
                _lastWallRevealZoneLog = activeZoneName;
                if (playerInsideMatchingZone)
                    Debug.Log($"[ColdCar] Indoor wall reveal active: zone='{matchingZone.name}', renderer='{renderer.name}', factor={matchingZone.fogRevealFactor:0.00}, verticalPadding={matchingZone.fogVerticalPadding:0.00} m.", this);
                else
                    Debug.Log("[ColdCar] Indoor wall reveal inactive: player eye is outside all shelter zones.", this);
            }
        }
    }

    void ResetIndoorWallCutaway()
    {
        if (_wallCutawayRenderers == null) return;
        foreach (Renderer renderer in _wallCutawayRenderers)
        {
            if (renderer == null) continue;
            renderer.GetPropertyBlock(_wallCutawayBlock);
            _wallCutawayBlock.SetFloat(CutawayEnabledID, 0f);
            _wallCutawayBlock.SetFloat(FogWallRevealEnabledID, 0f);
            renderer.SetPropertyBlock(_wallCutawayBlock);
        }
    }

    void OnEnable()
    {
        Debug.Log("[ColdCar] FogOfWar v25 initialized (explicit camera depth binding + orthographic-safe wall raster reveal)");
        ValidateSingleInstance();
        ResolveWallOcclusionSource();
        ResolveWallCutawayRenderers();

        Shader sh = Shader.Find("Hidden/ColdCar/FogOfWarOverlay");
        if (sh == null)
        {
            Debug.LogError("[FogOfWar] Overlay shader missing - fog cannot run.");
            return;
        }
        _mat = new Material(sh);
        _pass = new FogPass(_mat, this);
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        ResetIndoorWallCutaway();
        if (_mat != null) Destroy(_mat);
    }

    void OnDestroy()
    {
        RenderSettings.fog = false;
    }

    void Update()
    {
        if (viewCamera == null && Camera.main != null) viewCamera = Camera.main;
        ResolveCar();

        var kb = Keyboard.current;
        if (kb != null && windowTestKeys)
        {
            if (kb.leftBracketKey.wasPressedThisFrame)
                windowVisibility = Mathf.Clamp01(windowVisibility - 0.1f);
            if (kb.rightBracketKey.wasPressedThisFrame)
                windowVisibility = Mathf.Clamp01(windowVisibility + 0.1f);
        }
    }

    void Start()
    {
        string eyeOrigin = followCharacter && visionTarget != null
            ? "first-person camera" : followCharacter && PlayerCharacterController.Instance != null && PlayerCharacterController.Instance.character != null
                ? "player eyes" : car != null ? car.name : "NONE";
        Debug.Log($"[ColdCar] FogOfWar eye origin: '{eyeOrigin}'. Wall colliders: {(_wallBoxes?.Length ?? 0)}.", this);
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (_mat == null || _pass == null) return;
        if (camera.cameraType != CameraType.Game) return;

        var pc = PlayerCharacterController.Instance;
        bool useFirstPersonTarget = followCharacter && visionTarget != null;
        bool useChar = followCharacter && !useFirstPersonTarget && pc != null && pc.character != null;
        Vector3 eye = useChar ? pc.HeadPoint
            : useFirstPersonTarget ? visionTarget.position
            : (car != null ? car.position + Vector3.up * eyeHeight : camera.transform.position);
        ApplyIndoorWallCutaway(eye, camera.transform.position);

        camera.depthTextureMode |= DepthTextureMode.Depth;
        camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_pass);
    }

    /// <summary>Call from gameplay: 0 = windows fully dirty, 1 = clear. Shrinks vision range and angle.</summary>
    public void SetWindowVisibility(float v) => windowVisibility = Mathf.Clamp01(v);

    /// <summary>Call from gameplay when equipping goggles/helmets: 0 = clear sight, 1 = blind.</summary>
    public void SetVisibilityResistance(float v) => visibilityResistance = Mathf.Clamp01(v);

    /// <summary>1 - resistance. Multiplies vision range and angle.</summary>
    public float VisibilityFactor => 1f - Mathf.Clamp01(visibilityResistance);

    // ------------------------------------------------------------------

    class FogPass : ScriptableRenderPass
    {
        readonly Material _mat;
        readonly FogOfWarController _cfg;
        readonly Vector4[] _wallBoxCenters = new Vector4[MaxWallBoxes];
        readonly Vector4[] _wallBoxHalfSizes = new Vector4[MaxWallBoxes];
        int _lastIndoorZoneCount = -1;
        bool _wallMaskStatusLogged;
        static readonly ShaderTagId WallRevealMaskTag = new ShaderTagId("FogWallRevealMask");

        sealed class WallMaskData { public RendererListHandle rendererList; }
        sealed class OverlayData { public TextureHandle source; public Material material; }

        public FogPass(Material mat, FogOfWarController cfg)
        {
            _mat = mat;
            _cfg = cfg;
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            ConfigureInput(ScriptableRenderPassInput.Depth);
            requiresIntermediateTexture = true;
        }

        static bool _failedLogged;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            try { RecordInternal(renderGraph, frameData); }
            catch (System.Exception e)
            {
                if (!_failedLogged)
                {
                    _failedLogged = true;
                    Debug.LogError($"[ColdCar] FogOfWar overlay failed and is DISABLED: {e}");
                }
            }
        }

        void RecordInternal(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resData = frameData.Get<UniversalResourceData>();
            var camData = frameData.Get<UniversalCameraData>();
            var camera = camData.camera;
            if (camera.cameraType != CameraType.Game) return;

            var src = resData.cameraColor;
            if (!src.IsValid()) return;

            // FogOfWarOverlay.shader samples _CameraDepthTexture to reconstruct
            // the actual 3D surface position for every pixel. In RenderGraph this
            // resource must be fetched and declared as a pass dependency; otherwise
            // the graph can cull/omit it and the shader falls back to a ground-plane
            // projection (which reveals floors but cannot correctly reveal walls).
            // cameraDepthTexture is the sampleable copy used by the full-screen
            // overlay. cameraDepth is the real hardware depth attachment used for
            // exact Z testing while the wall mesh is rasterized. They are not
            // interchangeable: UseTexture(cameraDepthTexture) alone does NOT bind
            // it to the shader's _CameraDepthTexture sampler or provide a Z buffer.
            TextureHandle cameraDepthTexture = resData.cameraDepthTexture;
            if (!cameraDepthTexture.IsValid())
            {
                if (!_failedLogged)
                {
                    _failedLogged = true;
                    Debug.LogError("[ColdCar] FogOfWar: URP cameraDepthTexture is invalid. The fog pass requires depth; check URP Depth Texture / Depth Prepass settings.");
                }
                return;
            }

            TextureHandle cameraDepthAttachment = resData.cameraDepth;
            if (!cameraDepthAttachment.IsValid())
                cameraDepthAttachment = resData.activeDepthTexture;
            if (!cameraDepthAttachment.IsValid())
            {
                if (!_failedLogged)
                {
                    _failedLogged = true;
                    Debug.LogError("[ColdCar] FogOfWar: no camera depth attachment is available for the indoor wall mask.");
                }
                return;
            }

            // Rasterize the REAL wall mesh into a mask using this camera's own
            // projection. This is deliberately projection-agnostic: Unity draws
            // the same mesh with the same orthographic/perspective matrices as the
            // main camera. No screen-depth normal reconstruction or collider ray
            // approximation is involved.
            TextureDesc wallMaskDesc = src.GetDescriptor(renderGraph);
            wallMaskDesc.name = "Fog_IndoorWallRevealMask";
            wallMaskDesc.colorFormat = GraphicsFormat.R8_UNorm;
            wallMaskDesc.depthBufferBits = DepthBits.None;
            wallMaskDesc.msaaSamples = MSAASamples.None;
            wallMaskDesc.useMipMap = false;
            wallMaskDesc.filterMode = FilterMode.Point;
            wallMaskDesc.clearBuffer = true;
            wallMaskDesc.clearColor = Color.clear;
            TextureHandle wallRevealMask = renderGraph.CreateTexture(wallMaskDesc);

            // The mask needs two distinct resources:
            // 1) cameraDepthAttachment for fixed-function ZTest on the wall mesh;
            // 2) cameraDepthTexture as a sampleable copy for the full-screen overlay.
            // Do not substitute one for the other. The read-only depth attachment
            // makes the mask match the exact orthographic/perspective camera raster.
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            var wallListDesc = new RendererListDesc(WallRevealMaskTag, renderingData.cullResults, camera)
            {
                renderQueueRange = RenderQueueRange.opaque,
                sortingCriteria = SortingCriteria.CommonOpaque,
                layerMask = 1 << WallOcclusionLayer
            };
            RendererListHandle wallRendererList = renderGraph.CreateRendererList(wallListDesc);
            if (!_wallMaskStatusLogged)
            {
                _wallMaskStatusLogged = true;
                int trackedWallRenderers = _cfg._wallCutawayRenderers != null
                    ? _cfg._wallCutawayRenderers.Length : 0;
                Debug.Log($"[ColdCar] Indoor wall raster mask: orthographic={camera.orthographic}, layer={WallOcclusionLayer}, shaderPass=FogWallRevealMask, trackedRenderers={trackedWallRenderers}, depthAttachment=True, sampledDepth=True.");
            }

            using (var wallMaskBuilder = renderGraph.AddRasterRenderPass<WallMaskData>(
                       "FogOfWar Indoor Wall Mask", out var wallMaskData))
            {
                wallMaskData.rendererList = wallRendererList;
                wallMaskBuilder.UseRendererList(wallRendererList);
                // Bind the actual camera Z buffer read-only. The mask pass uses
                // fixed-function ZTest LEqual, so hidden wall fragments are rejected
                // by the same GPU depth test as the main camera draw.
                wallMaskBuilder.SetRenderAttachmentDepth(cameraDepthAttachment, AccessFlags.Read);
                wallMaskBuilder.SetRenderAttachment(wallRevealMask, 0, AccessFlags.Write);

                // Publish the sampleable depth COPY for the later full-screen overlay.
                // UseGlobalTexture in that pass creates the RenderGraph dependency and
                // binds the HLSL _CameraDepthTexture global to this exact resource.
                wallMaskBuilder.UseTexture(cameraDepthTexture, AccessFlags.Read);
                wallMaskBuilder.SetGlobalTextureAfterPass(cameraDepthTexture, CameraDepthTextureID);
                wallMaskBuilder.SetGlobalTextureAfterPass(wallRevealMask, FogWallRevealMaskID);
                wallMaskBuilder.SetRenderFunc((WallMaskData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.ClearRenderTarget(false, true, Color.clear);
                    ctx.cmd.DrawRendererList(data.rendererList);
                });
            }

            var desc = camData.cameraTargetDescriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            desc.useMipMap = false;
            TextureHandle temp = renderGraph.CreateTexture(new TextureDesc(desc) { name = "Fog_Temp" });

            var pc = PlayerCharacterController.Instance;
            // An explicitly assigned camera target wins over any legacy third-person singleton.
            bool useFirstPersonTarget = _cfg.followCharacter && _cfg.visionTarget != null;
            bool useChar = _cfg.followCharacter && !useFirstPersonTarget && pc != null && pc.character != null;
            Vector3 eye = useChar ? pc.HeadPoint
                : useFirstPersonTarget ? _cfg.visionTarget.position
                : (_cfg.car != null ? _cfg.car.position + Vector3.up * _cfg.eyeHeight : camera.transform.position);
            Vector3 viewDir = useChar ? pc.FacingDir
                : useFirstPersonTarget ? _cfg.visionTarget.forward
                : (_cfg.car != null ? _cfg.car.forward : camera.transform.forward);
            if (!_cfg.use3DViewCone) viewDir.y = 0f;
            viewDir = viewDir.sqrMagnitude > 1e-6f ? viewDir.normalized : Vector3.forward;

            // vision range/angle with the dirty-window and resistance multipliers
            float vis = Mathf.Clamp01(_cfg.windowVisibility);
            float visF = _cfg.VisibilityFactor;
            float range = _cfg.viewRange * Mathf.Lerp(0.4f, 1f, vis) * visF;
            float angleDeg = _cfg.viewConeAngleDeg * Mathf.Lerp(0.6f, 1f, vis) * Mathf.Lerp(0.6f, 1f, visF);
            float halfRad = angleDeg * 0.5f * Mathf.Deg2Rad;
            float edgeRad = _cfg.coneEdgeAngleDeg * Mathf.Deg2Rad;
            float cosIn = Mathf.Cos(Mathf.Max(0f, halfRad - edgeRad));
            float cosOut = Mathf.Cos(Mathf.Min(Mathf.PI, halfRad + edgeRad));

            // ground level for the no-depth fallback (tilemap floor when present)
            float groundY = _cfg.car != null ? _cfg.car.position.y :
                (_cfg.visionTarget != null ? _cfg.visionTarget.position.y - _cfg.eyeHeight : 0f);
            foreach (var map in FindObjectsByType<SnowTilemapData>(FindObjectsSortMode.None))
            {
                if (map != null && map.gameObject.scene.IsValid())
                {
                    groundY = map.transform.position.y + map.FloorHeight;
                    break;
                }
            }

            // wall boxes (direct components of the dedicated root only)
            int wallBoxCount = 0;
            System.Array.Clear(_wallBoxCenters, 0, _wallBoxCenters.Length);
            System.Array.Clear(_wallBoxHalfSizes, 0, _wallBoxHalfSizes.Length);
            if (_cfg.wallOcclusionRoot != null)
            {
                _mat.SetMatrix(WallRootWorldToLocalID, _cfg.wallOcclusionRoot.worldToLocalMatrix);
                foreach (var box in _cfg._wallBoxes)
                {
                    if (box == null || !box.enabled || !box.gameObject.activeInHierarchy) continue;
                    if (box.transform != _cfg.wallOcclusionRoot) continue;
                    if (wallBoxCount >= MaxWallBoxes) break;
                    Vector3 half = box.size * 0.5f;
                    _wallBoxCenters[wallBoxCount] = new Vector4(box.center.x, box.center.y, box.center.z, 0f);
                    _wallBoxHalfSizes[wallBoxCount] = new Vector4(half.x, half.y, half.z, 0f);
                    wallBoxCount++;
                }
            }
            _mat.SetInt(WallBoxCountID, wallBoxCount);
            _mat.SetVectorArray(WallBoxCenterID, _wallBoxCenters);
            _mat.SetVectorArray(WallBoxHalfSizeID, _wallBoxHalfSizes);

            // Indoor reveal is enabled only for the shelter containing the
            // player's eyes. This keeps the 65% reveal strictly indoor and
            // prevents nearby buildings from being pre-revealed from outside.
            int indoorZoneCount = IndoorShelterZone.ApplyFogWorldZones(_mat, eye);
            if (indoorZoneCount != _lastIndoorZoneCount)
            {
                Debug.Log($"[ColdCar] FogOfWar: registered indoor reveal zones = {indoorZoneCount}.");
                _lastIndoorZoneCount = indoorZoneCount;
            }

            _mat.SetVector(EyePosID, new Vector4(eye.x, eye.y, eye.z, 0f));
            _mat.SetVector(ViewDirID, new Vector4(viewDir.x, viewDir.y, viewDir.z, 0f));
            _mat.SetFloat(Use3DViewConeID, _cfg.use3DViewCone ? 1f : 0f);
            _mat.SetFloat(ConeCosInID, cosIn);
            _mat.SetFloat(ConeCosOutID, cosOut);
            _mat.SetFloat(ConeRangeID, range);
            _mat.SetFloat(ConeRangeEdgeID, _cfg.viewRangeEdge);
            _mat.SetFloat(NearRadiusID, _cfg.nearRadius);
            _mat.SetFloat(NearFeatherID, _cfg.nearFeather);
            _mat.SetFloat(BlockVisionWithWallsID, _cfg.blockVisionWithWalls ? 1f : 0f);
            _mat.SetFloat(WallSurfTolID, Mathf.Clamp(_cfg.wallSurfaceTolerance, 0f, 0.3f));
            _mat.SetFloat(GroundYID, groundY);
            _mat.SetFloat(DarknessID, _cfg.darkness);
            _mat.SetColor(DarkColorID, _cfg.nightColor);
            _mat.SetFloat(RevealBrightnessID, _cfg.revealBrightness);
            _mat.SetFloat(RevealLiftID, _cfg.revealLift);
            _mat.SetFloat(DiagModeID, Mathf.Clamp(_cfg.diagnosticMode, 0, 4));

            // copy scene -> temp, then fog temp -> scene (proven v19 structure)
            renderGraph.AddBlitPass(src, temp, Vector2.one, Vector2.zero,
                filterMode: RenderGraphUtils.BlitFilterMode.ClampBilinear, passName: "FogOfWar Copy");

            using (var overlayBuilder = renderGraph.AddRasterRenderPass<OverlayData>(
                       "FogOfWar Overlay", out var overlayData))
            {
                overlayData.source = temp;
                overlayData.material = _mat;
                overlayBuilder.UseTexture(temp, AccessFlags.Read);
                overlayBuilder.UseGlobalTexture(CameraDepthTextureID, AccessFlags.Read);
                overlayBuilder.UseGlobalTexture(FogWallRevealMaskID, AccessFlags.Read);
                overlayBuilder.SetRenderAttachment(src, 0, AccessFlags.Write);
                overlayBuilder.SetRenderFunc((OverlayData d, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), d.material, 0);
                });
            }
        }
    }
}
