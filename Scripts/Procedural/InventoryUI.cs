using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// TAB inventory. The backdrop is a LIVE low-resolution camera render that is
/// blurred into a screen-space RawImage. The world is never frozen and the
/// image continues updating while the inventory is open.
/// </summary>
[DisallowMultipleComponent]
public sealed class InventoryUI : MonoBehaviour
{
    ProceduralFirstPersonController _owner;
    ProceduralFPSVisualSettings _settings;
    CanvasGroup _group;
    RectTransform _panel;
    RawImage _blurImage;
    Image _dimImage;
    Material _blurMaterial;
    RenderTexture _cameraTexture;
    RenderTexture _blurTexture;
    Camera _captureCamera;
    Camera _blurCamera;
    bool _liveBlurActive;
    Coroutine _showRoutine;
    float _panelWidth;
    float _leftInset;
    bool _open;
    bool _openingPending;
    float _slide;

    public bool IsOpen => _open;

    void Start()
    {
        _owner = GetComponentInParent<ProceduralFirstPersonController>();
        _settings = FindFirstObjectByType<ProceduralFPSVisualSettings>();
        Build();
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.tabKey.wasPressedThisFrame)
            SetOpen(!_open);

        if (_panel == null || _group == null) return;

        if (!_openingPending)
        {
            float speed = _settings != null ? _settings.inventorySlideSpeed : 8f;
            _slide = Mathf.MoveTowards(_slide, _open ? 1f : 0f, Time.unscaledDeltaTime * speed);
        }

        float eased = _slide * _slide * (3f - 2f * _slide);
        _panel.anchoredPosition = new Vector2(
            Mathf.Lerp(-_panelWidth - 90f, _leftInset, eased), 0f);
        _group.alpha = _slide;
        _group.blocksRaycasts = _slide > 0.95f;
        _group.interactable = _slide > 0.95f;

        // Restore camera rendering when the close animation has fully finished.
        if (!_open && !_openingPending && _slide <= 0.001f && _blurTexture != null)
            ReleaseBlurResources();
    }

    void SetOpen(bool open)
    {
        if (_open == open) return;
        _open = open;
        if (_owner != null) _owner.OnInventoryToggled(open);

        if (open)
        {
            if (_showRoutine != null) StopCoroutine(_showRoutine);
            ReleaseBlurResources();
            _slide = 0f;
            _group.alpha = 0f;
            _openingPending = true;
            BeginLiveBlurBackdrop();
            _showRoutine = StartCoroutine(WaitForFirstLiveFrame());
        }
        else
        {
            _openingPending = false;
            if (_showRoutine != null)
            {
                StopCoroutine(_showRoutine);
                _showRoutine = null;
            }
            // Keep the last blurred frame during the short close animation.
            // The primary gameplay camera was never redirected or disabled.
            StopLiveBlurCapture();
        }
    }

    IEnumerator WaitForFirstLiveFrame()
    {
        // The camera has rendered into the low-res target and the end-camera
        // callback has filled the blur texture by this point.
        yield return new WaitForEndOfFrame();
        _showRoutine = null;
        _openingPending = false;
    }

    void BeginLiveBlurBackdrop()
    {
        // Keep the real gameplay camera rendering to Display 1.
        // The blurred backdrop gets its own hidden low-resolution camera.
        _captureCamera = _owner != null && _owner.playerCamera != null
            ? _owner.playerCamera
            : Camera.main;
        if (_captureCamera == null)
        {
            Debug.LogWarning("[InventoryUI] No player camera found; inventory will open without a live blurred backdrop.", this);
            return;
        }

        int downsample = _settings != null ? Mathf.Clamp(_settings.inventoryBlurDownsample, 2, 10) : 5;
        int width = Mathf.Max(256, Screen.width / downsample);
        int height = Mathf.Max(144, Screen.height / downsample);

        _cameraTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "Inventory_LiveCamera_LowRes",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        _cameraTexture.Create();

        _blurTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "Inventory_LiveBlurredBackdrop",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false
        };
        _blurTexture.Create();

        Shader blurShader = Resources.Load<Shader>("InventoryBackdropBlur");
        if (blurShader == null) blurShader = Shader.Find("Hidden/ColdCar/InventoryBackdropBlur");
        if (blurShader != null)
        {
            if (_blurMaterial == null)
                _blurMaterial = new Material(blurShader) { hideFlags = HideFlags.HideAndDontSave };
            float radius = _settings != null ? _settings.inventoryBackdropBlur : 2.5f;
            float contrast = _settings != null ? _settings.inventoryBackdropContrast : 0.92f;
            _blurMaterial.SetFloat("_BlurRadius", radius);
            _blurMaterial.SetFloat("_BackdropContrast", contrast);
        }
        else
        {
            Debug.LogWarning("[InventoryUI] InventoryBackdropBlur shader was not found; live backdrop will use downsample blur only.", this);
        }

        CreateBlurCamera();

        _blurImage.texture = _blurTexture;
        _blurImage.enabled = true;
        _dimImage.enabled = true;

        RenderPipelineManager.endCameraRendering += OnCameraRendered;
        _liveBlurActive = true;
    }

    void CreateBlurCamera()
    {
        if (_captureCamera == null || _cameraTexture == null) return;

        GameObject go = new GameObject("Inventory_Blur_Camera")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        go.transform.SetParent(_captureCamera.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        go.tag = "Untagged";

        _blurCamera = go.AddComponent<Camera>();
        _blurCamera.CopyFrom(_captureCamera);
        _blurCamera.enabled = true;
        _blurCamera.tag = "Untagged";
        _blurCamera.targetTexture = _cameraTexture;
        _blurCamera.allowMSAA = false;
        _blurCamera.depth = _captureCamera.depth - 1f;
        _blurCamera.stereoTargetEye = StereoTargetEyeMask.None;

        // A URP camera must have UniversalAdditionalCameraData. Copy only the
        // rendering options needed for a matching scene image; do not copy the
        // original camera stack or any player-camera scripts/listeners.
        UniversalAdditionalCameraData sourceData = _captureCamera.GetUniversalAdditionalCameraData();
        UniversalAdditionalCameraData blurData = _blurCamera.GetUniversalAdditionalCameraData();
        blurData.renderType = CameraRenderType.Base;
        blurData.renderPostProcessing = sourceData.renderPostProcessing;
        blurData.renderShadows = false; // the backdrop is blurred; avoid rendering the shadow map twice
        blurData.antialiasing = sourceData.antialiasing;
        blurData.antialiasingQuality = sourceData.antialiasingQuality;
        blurData.volumeLayerMask = sourceData.volumeLayerMask;
        blurData.volumeTrigger = sourceData.volumeTrigger != null
            ? sourceData.volumeTrigger
            : _captureCamera.transform;
        blurData.requiresColorTexture = sourceData.requiresColorTexture;
        blurData.requiresDepthTexture = sourceData.requiresDepthTexture;

        SyncBlurCameraSettings();
    }

    void LateUpdate()
    {
        if (_liveBlurActive)
            SyncBlurCameraSettings();
    }

    void SyncBlurCameraSettings()
    {
        if (_captureCamera == null || _blurCamera == null) return;

        // The helper camera is parented to the player camera, so its position
        // and rotation follow automatically. Keep its projection/culling in sync.
        _blurCamera.fieldOfView = _captureCamera.fieldOfView;
        _blurCamera.orthographic = _captureCamera.orthographic;
        _blurCamera.orthographicSize = _captureCamera.orthographicSize;
        _blurCamera.nearClipPlane = _captureCamera.nearClipPlane;
        _blurCamera.farClipPlane = _captureCamera.farClipPlane;
        _blurCamera.cullingMask = _captureCamera.cullingMask;
        _blurCamera.rect = _captureCamera.rect;
        _blurCamera.clearFlags = _captureCamera.clearFlags;
        _blurCamera.backgroundColor = _captureCamera.backgroundColor;
        _blurCamera.useOcclusionCulling = _captureCamera.useOcclusionCulling;
        _blurCamera.allowHDR = _captureCamera.allowHDR;
    }

    void OnCameraRendered(ScriptableRenderContext context, Camera camera)
    {
        if (!_liveBlurActive || camera != _blurCamera || _cameraTexture == null || _blurTexture == null)
            return;

        if (_blurMaterial != null)
            Graphics.Blit(_cameraTexture, _blurTexture, _blurMaterial);
        else
            Graphics.Blit(_cameraTexture, _blurTexture);
    }

    void StopLiveBlurCapture()
    {
        if (_liveBlurActive)
            RenderPipelineManager.endCameraRendering -= OnCameraRendered;
        _liveBlurActive = false;

        if (_blurCamera != null)
        {
            GameObject cameraObject = _blurCamera.gameObject;
            _blurCamera.targetTexture = null;
            _blurCamera.enabled = false;
            _blurCamera = null;
            if (Application.isPlaying) Destroy(cameraObject);
            else DestroyImmediate(cameraObject);
        }

        _captureCamera = null;
    }

    void Build()
    {
        int cols = _settings != null ? _settings.inventoryColumns : 5;
        int rows = _settings != null ? _settings.inventoryRows : 8;
        float cell = _settings != null ? _settings.inventoryCellSize : 78f;
        float gap = _settings != null ? _settings.inventoryCellGap : 5f;
        float pad = 18f;
        float panelOpacity = _settings != null ? _settings.inventoryPanelOpacity : 0.76f;
        float cellOpacity = _settings != null ? _settings.inventoryCellOpacity : 0.78f;
        float dimOpacity = _settings != null ? _settings.inventoryBackdropDim : 0.22f;
        Color panelColor = _settings != null ? _settings.inventoryPanelColor : new Color(0.03f, 0.03f, 0.035f, 0.94f);
        Color cellColor = _settings != null ? _settings.inventoryCellColor : new Color(0.10f, 0.10f, 0.11f, 1f);
        _leftInset = _settings != null ? _settings.inventoryLeftInset : 38f;

        GameObject canvasGo = new GameObject("InventoryCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        Canvas canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;
        CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _group = canvasGo.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        GameObject blurGo = new GameObject("LiveBlurredGameView", typeof(RectTransform), typeof(RawImage));
        blurGo.transform.SetParent(canvasGo.transform, false);
        RectTransform blurRect = blurGo.GetComponent<RectTransform>();
        StretchToParent(blurRect);
        _blurImage = blurGo.GetComponent<RawImage>();
        _blurImage.color = Color.white;
        _blurImage.raycastTarget = false;
        _blurImage.enabled = false;

        GameObject dimGo = new GameObject("BackdropDim", typeof(RectTransform), typeof(Image));
        dimGo.transform.SetParent(canvasGo.transform, false);
        RectTransform dimRect = dimGo.GetComponent<RectTransform>();
        StretchToParent(dimRect);
        _dimImage = dimGo.GetComponent<Image>();
        _dimImage.color = new Color(0f, 0f, 0f, dimOpacity);
        _dimImage.raycastTarget = false;
        _dimImage.enabled = false;

        _panelWidth = cols * cell + (cols - 1) * gap + pad * 2f;
        float panelHeight = rows * cell + (rows - 1) * gap + pad * 2f;
        GameObject panelGo = new GameObject("Panel", typeof(RawImage));
        _panel = panelGo.GetComponent<RectTransform>();
        _panel.SetParent(canvasGo.transform, false);
        _panel.anchorMin = new Vector2(0f, 0.5f);
        _panel.anchorMax = new Vector2(0f, 0.5f);
        _panel.pivot = new Vector2(0f, 0.5f);
        _panel.sizeDelta = new Vector2(_panelWidth, panelHeight);
        _panel.anchoredPosition = new Vector2(-_panelWidth - 90f, 0f);
        RawImage panelImage = panelGo.GetComponent<RawImage>();
        panelImage.texture = Texture2D.whiteTexture;
        panelColor.a *= panelOpacity;
        panelImage.color = panelColor;
        panelImage.raycastTarget = false;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                GameObject cellGo = new GameObject($"Cell_{r}_{c}", typeof(RawImage));
                RectTransform rect = cellGo.GetComponent<RectTransform>();
                rect.SetParent(_panel, false);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(cell, cell);
                rect.anchoredPosition = new Vector2(pad + c * (cell + gap), -(pad + r * (cell + gap)));
                RawImage image = cellGo.GetComponent<RawImage>();
                image.texture = Texture2D.whiteTexture;
                Color tint = cellColor;
                // Keep a visible contrast between the slots and their backing
                // panel even when older serialized settings contain very dark values.
                tint.r = Mathf.Max(tint.r, 0.13f);
                tint.g = Mathf.Max(tint.g, 0.13f);
                tint.b = Mathf.Max(tint.b, 0.14f);
                tint.a *= Mathf.Max(cellOpacity, 0.82f);
                image.color = tint;
                image.raycastTarget = false;
            }
        }
    }

    static void StretchToParent(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
    }

    void ReleaseBlurResources()
    {
        StopLiveBlurCapture();
        if (_blurImage != null)
        {
            _blurImage.texture = null;
            _blurImage.enabled = false;
        }
        if (_dimImage != null) _dimImage.enabled = false;

        if (_cameraTexture != null)
        {
            _cameraTexture.Release();
            Destroy(_cameraTexture);
            _cameraTexture = null;
        }
        if (_blurTexture != null)
        {
            _blurTexture.Release();
            Destroy(_blurTexture);
            _blurTexture = null;
        }
    }

    void OnDestroy()
    {
        if (_showRoutine != null) StopCoroutine(_showRoutine);
        ReleaseBlurResources();
        if (_blurMaterial != null) Destroy(_blurMaterial);
    }
}
