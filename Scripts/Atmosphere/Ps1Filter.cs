using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.InputSystem;

/// <summary>
/// PS1-style pixelated post filter (Mike Klubnika style): low internal
/// resolution, color quantization, Bayer dithering, scanlines, noise.
/// Implemented as a URP RenderGraph blit pass enqueued from
/// beginCameraRendering - no Renderer Feature or scene changes required.
/// Just put it on the camera. Toggle in play mode: F9.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
[ExecuteAlways]
public class Ps1Filter : MonoBehaviour
{
    [Header("Enable")]
    public bool filterEnabled = true;
    [Tooltip("Runtime toggle key.")]
    public Key toggleKey = Key.F9;

    [Header("Resolution")]
    [Tooltip("Vertical resolution of the internal buffer. 240 = real PS1 vibe.")]
    [Range(72, 1080)] public int pixelHeight = 240;

    [Header("PS1 look (Buckshot Roulette style: clean chunky pixels)")]
    [Tooltip("Color levels: 5 = 32 shades per channel, clean look. 2-3 = heavy retro banding.")]
    [Range(1f, 8f)] public float quantizeLevels = 5f;
    [Tooltip("Keep low (0..0.3) for the clean Buckshot look; high values = gritty noise.")]
    [Range(0f, 1f)] public float dither = 0.18f;
    [Range(0f, 1f)] public float scanlines = 0.06f;
    [Range(0f, 1f)] public float noise = 0.012f;
    [Tooltip("Edge smoothing after pixelation. 0 keeps hard PS1 pixels; higher values soften only high-contrast pixel boundaries.")]
    [Range(0f, 1f)] public float edgeSmoothing = 0.32f;

    [Header("Noise in DARK areas (fog of war etc.)")]
    [Tooltip("How much of the noise/dither survives in dark areas. 0 = pitch black is perfectly clean, 1 = same noise as everywhere.")]
    [Range(0f, 1f)] public float darkNoiseKeep = 0.15f;
    [Tooltip("Luminance below which a pixel counts as 'dark' and gets the reduced noise.")]
    [Range(0f, 0.5f)] public float darkNoiseLuminance = 0.22f;

    static readonly int QuantizeID = Shader.PropertyToID("_Quantize");
    static readonly int DitherID = Shader.PropertyToID("_Dither");
    static readonly int ScanlineID = Shader.PropertyToID("_Scanline");
    static readonly int NoiseID = Shader.PropertyToID("_Noise");
    static readonly int ResID = Shader.PropertyToID("_Res");
    static readonly int TimeNID = Shader.PropertyToID("_TimeN");
    static readonly int DarkNoiseMulID = Shader.PropertyToID("_DarkNoiseMul");
    static readonly int DarkLumID = Shader.PropertyToID("_DarkLum");
    static readonly int EdgeSmoothingID = Shader.PropertyToID("_EdgeSmoothing");

    Material _mat;
    Ps1Pass _pass;
    Camera _cam;

    void OnEnable()
    {
        _cam = GetComponent<Camera>();
        Shader sh = Shader.Find("Hidden/ColdCar/PS1Post");
        if (sh == null)
        {
            Debug.LogError("[Ps1Filter] Shader 'Hidden/ColdCar/PS1Post' not found.");
            enabled = false;
            return;
        }
        _mat = new Material(sh);
        _pass = new Ps1Pass(_mat, this);
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        if (_mat != null)
        {
            // ExecuteAlways: OnDisable срабатывает и в редакторе, где
            // Destroy запрещён — там только DestroyImmediate.
            if (Application.isPlaying) Destroy(_mat);
            else DestroyImmediate(_mat);
        }
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[toggleKey] != null && kb[toggleKey].wasPressedThisFrame)
            filterEnabled = !filterEnabled;
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!filterEnabled || camera != _cam || _mat == null) return;

        _mat.SetFloat(QuantizeID, quantizeLevels);
        _mat.SetFloat(DitherID, dither);
        _mat.SetFloat(ScanlineID, scanlines);
        _mat.SetFloat(NoiseID, noise);
        _mat.SetFloat(TimeNID, Time.time % 10f);
        _mat.SetFloat(DarkNoiseMulID, darkNoiseKeep);
        _mat.SetFloat(DarkLumID, darkNoiseLuminance);
        _mat.SetFloat(EdgeSmoothingID, edgeSmoothing);
        _mat.SetVector(ResID, Vector4.zero); // filled inside the pass per-camera

        camera.GetUniversalAdditionalCameraData().scriptableRenderer.EnqueuePass(_pass);
    }
}

/// <summary>
/// RenderGraph pass: downsample camera color into a low-res texture, then
/// blit it back with point sampling + quantization/dither/scanlines.
/// </summary>
class Ps1Pass : ScriptableRenderPass
{
    readonly Material _mat;
    readonly Ps1Filter _cfg;
    static readonly int ResID = Shader.PropertyToID("_Res");

    public Ps1Pass(Material mat, Ps1Filter cfg)
    {
        _mat = mat;
        _cfg = cfg;
        // 660 = just after AfterRenderingPostProcessing (650), so the PS1
        // quantization runs on the final image (after the fog-of-war overlay)
        renderPassEvent = (RenderPassEvent)660;
        // guarantees URP renders the camera into an intermediate cameraColor
        // texture we are allowed to sample and re-write
        requiresIntermediateTexture = true;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var resData = frameData.Get<UniversalResourceData>();
        var camData = frameData.Get<UniversalCameraData>();

        if (camData.camera.cameraType != CameraType.Game) return;

        var src = resData.cameraColor;
        if (!src.IsValid()) return;

        var desc = camData.cameraTargetDescriptor;
        desc.msaaSamples = 1;
        desc.depthBufferBits = 0;
        desc.useMipMap = false;

        int h = Mathf.Clamp(_cfg.pixelHeight, 72, 1080);
        float aspect = (float)desc.width / desc.height;
        desc.width = Mathf.Max(4, Mathf.RoundToInt(h * aspect));
        desc.height = h;

        TextureHandle lowRes = renderGraph.CreateTexture(new TextureDesc(desc) { name = "PS1_LowRes" });

        // 1) camera color -> low res downsample
        renderGraph.AddBlitPass(src, lowRes, Vector2.one, Vector2.zero,
            filterMode: RenderGraphUtils.BlitFilterMode.ClampBilinear, passName: "PS1 Downsample");

        // 2) low res -> camera color with the PS1 material (point sample + effects)
        _mat.SetVector(ResID, new Vector4(desc.width, desc.height, 1f / desc.width, 1f / desc.height));
        renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(lowRes, src, _mat, 0), "PS1 Post");
    }
}
