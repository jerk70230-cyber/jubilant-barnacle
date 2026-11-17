using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.InputSystem;
using System.Text;

/// <summary>
/// Диагностический HUD для чёрного экрана в ProceduralFPS_MVP.
/// Просто повесь на любой объект в сцене (лучше на Player).
/// Показывает живое состояние камеры, света, тумана и мира в Play-режиме.
///
/// Клавиши (только в Play):
///   F6  — показать/скрыть сам HUD
///   F10 — "проба": спавнит перед камерой ЖЁЛТЫЙ unlit-куб и БЕЛЫЙ lit-шар с точечным светом
///   F11 — выкл/вкл пост-процессинг камеры (плюс HDR output) — проверить объём
///   F12 — полный дамп состояния в консоль + в буфер обмена
///
/// Как читать пробу F10:
///   вижу жёлтый куб        → рендер-пайплайн рисует геометрию, проблема в материалах/свете комнаты
///   вижу белый шар         → освещение в пайплайне работает, проблема в настройках комнаты/тумана
///   не вижу ничего         → камера смотрит в пустоту / геометрия далеко / пайплайн сломан
/// </summary>
[DisallowMultipleComponent]
public sealed class FpsRenderDebugHud : MonoBehaviour
{
    [Header("HUD")]
    public bool showHud = true;
    public Key toggleHudKey = Key.F6;

    [Header("Пробы")]
    public Key spawnProbeKey = Key.F10;
    public Key togglePostKey = Key.F11;
    public Key dumpStateKey = Key.F12;

    [Header("Настройки пробы")]
    public float probeDistance = 2.5f;
    public float probeLightIntensity = 5f;

    string _probeStatus = "";
    Vector3 _probeSpawnPos;
    int _lastVisibleCount;
    float _lastClosestSurface = -1f;
    string _lastRayHit = "-";
    Transform _probeRoot;

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb[toggleHudKey] != null && kb[toggleHudKey].wasPressedThisFrame)
            showHud = !showHud;
        if (kb[spawnProbeKey] != null && kb[spawnProbeKey].wasPressedThisFrame)
            SpawnProbe();
        if (kb[togglePostKey] != null && kb[togglePostKey].wasPressedThisFrame)
            TogglePost();
        if (kb[dumpStateKey] != null && kb[dumpStateKey].wasPressedThisFrame)
            DumpState();
    }

    // ---------------------------------------------------------------- probe

    void SpawnProbe()
    {
        Camera cam = Camera.main;
        if (cam == null) { _probeStatus = "нет Camera.main"; return; }

        if (_probeRoot != null) Destroy(_probeRoot.gameObject);
        _probeRoot = new GameObject("RenderProbe").transform;
        _probeSpawnPos = cam.transform.position + cam.transform.forward * probeDistance + Vector3.up * 0.3f;

        // ЖЁЛТЫЙ КУБ — unlit: проверяет ЧИСТЫЙ рендер (без света).
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(_probeRoot);
        cube.transform.position = _probeSpawnPos;
        cube.transform.localScale = Vector3.one * 0.5f;
        var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        var cubeMat = new Material(unlitShader != null ? unlitShader : Shader.Find("Unlit/Color"));
        if (cubeMat.HasProperty("_BaseColor")) cubeMat.SetColor("_BaseColor", Color.yellow);
        else if (cubeMat.HasProperty("_Color")) cubeMat.SetColor("_Color", Color.yellow);
        cube.GetComponent<MeshRenderer>().sharedMaterial = cubeMat;

        // БЕЛЫЙ ШАР — lit: проверяет, что ЛАМПЫ вообще освещают геометрию.
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.transform.SetParent(_probeRoot);
        sphere.transform.position = _probeSpawnPos + Vector3.right * 1.0f;
        sphere.transform.localScale = Vector3.one * 0.45f;
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (litShader != null)
        {
            var sphereMat = new Material(litShader);
            sphereMat.SetColor("_BaseColor", Color.white);
            sphere.GetComponent<MeshRenderer>().sharedMaterial = sphereMat;
        }

        var probeLightGo = new GameObject("RenderProbeLight");
        probeLightGo.transform.SetParent(_probeRoot);
        probeLightGo.transform.position = _probeSpawnPos + new Vector3(0.5f, 1.2f, -0.6f);
        var probeLight = probeLightGo.AddComponent<Light>();
        probeLight.type = LightType.Point;
        probeLight.color = Color.white;
        probeLight.intensity = probeLightIntensity;
        probeLight.range = 15f;
        probeLight.shadows = LightShadows.None;

        _probeStatus = $"проба заспавнена в {_probeSpawnPos.ToString("F1")} (перед камерой)";
        Debug.Log($"[RenderDebug] Проба: куб {_probeSpawnPos:F1}, шар {(_probeSpawnPos + Vector3.right):F1}, свет над ними. Посмотри перед собой.", this);
    }

    void TogglePost()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = !data.renderPostProcessing;
        if (!data.renderPostProcessing) data.allowHDROutput = false;
        _probeStatus = $"пост-процессинг камеры: {(data.renderPostProcessing ? "ВКЛ" : "ВЫКЛ")} (HDR output: {(data.allowHDROutput ? "ВКЛ" : "ВЫКЛ")})";
    }

    // ---------------------------------------------------------------- dump

    string BuildStateDump()
    {
        var sb = new StringBuilder();
        Camera cam = Camera.main;

        sb.AppendLine("=== FPS RENDER DEBUG DUMP ===");

        if (cam == null)
        {
            sb.AppendLine("Camera.main = NULL! (камера не помечена MainCamera или выключена)");
        }
        else
        {
            var data = cam.GetUniversalAdditionalCameraData();
            sb.AppendLine($"[Камера] pos={cam.transform.position:F2} forward={cam.transform.forward:F2} near={cam.nearClipPlane} far={cam.farClipPlane}");
            sb.AppendLine($"[Камера] cullingMask={cam.cullingMask} post={data.renderPostProcessing} hdrOut={data.allowHDROutput} hdr={cam.allowHDR} renderer={data.scriptableRenderer?.GetType().Name}");
            sb.AppendLine($"[Камера] depthTexture={data.requiresDepthTexture} opaqueTexture={data.requiresColorTexture}");

            // что перед камерой физически есть
            if (Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 50f))
                _lastRayHit = $"{hit.collider.name} на {hit.distance:F2} м";
            else
                _lastRayHit = "ничего в пределах 50 м";
            sb.AppendLine($"[Рейкаст вперёд] {_lastRayHit}");
        }

        var flash = GameObject.Find("First Person Flashlight");
        if (flash != null && flash.TryGetComponent(out Light fl))
        {
            sb.AppendLine($"[Фонарь] enabled={fl.enabled} intensity={fl.intensity} range={fl.range} spot={fl.spotAngle} shadows={fl.shadows}");
            sb.AppendLine($"[Фонарь] pos={fl.transform.position:F2} fwd={fl.transform.forward:F2}");
        }
        else sb.AppendLine("[Фонарь] НЕ НАЙДЕН");

        // мир: сколько рендереров, видимы ли они камере
        var worldRoot = GameObject.Find("Procedural World");
        int total = 0, visible = 0, enabledR = 0;
        float closest = -1f;
        if (worldRoot != null && cam != null)
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            foreach (var r in worldRoot.GetComponentsInChildren<MeshRenderer>())
            {
                total++;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                enabledR++;
                if (GeometryUtility.TestPlanesAABB(planes, r.bounds)) visible++;
                float d = Vector3.Distance(cam.transform.position, r.bounds.ClosestPoint(cam.transform.position));
                if (closest < 0f || d < closest) closest = d;
            }
        }
        _lastVisibleCount = visible;
        _lastClosestSurface = closest;
        sb.AppendLine($"[Мир] root={(worldRoot != null ? "есть" : "НЕТ")} renderers={total} enabled={enabledR} в-фрустуме={visible} ближайшая-поверхность={(closest < 0 ? "-" : closest.ToString("F2") + " м")}");

        // материалы комнаты: какой шейдер, какой цвет, живая ли текстура
        if (worldRoot != null)
        {
            foreach (var r in worldRoot.GetComponentsInChildren<MeshRenderer>())
            {
                string n = r.gameObject.name;
                if (n != "WallSurfaces" && n != "FloorSurfaces" && n != "CeilingSurfaces") continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) { sb.AppendLine($"[Материал {n}] NULL-материал!"); continue; }
                    string texInfo = "нет текстуры";
                    if (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") is Texture2D bt)
                    {
                        try { texInfo = $"'{bt.name}' центр-пиксель={bt.GetPixel(bt.width / 2, bt.height / 2)}"; }
                        catch { texInfo = $"'{bt.name}' (не читается из скрипта, имя живое)"; }
                    }
                    string tint = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-";
                    sb.AppendLine($"[Материал {n}] '{m.name}' shader='{(m.shader != null ? m.shader.name : "NULL")}' tint={tint} map={texInfo}");
                }
            }
        }

        var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (var l in lights)
            sb.AppendLine($"[Свет] '{l.name}' type={l.type} enabled={l.enabled} intensity={l.intensity} range={l.range:F1} shadows={l.shadows}");

        sb.AppendLine($"[Ambient] flat={RenderSettings.ambientLight} intensity={RenderSettings.ambientIntensity}");
        sb.AppendLine($"[Fog] on={RenderSettings.fog} mode={RenderSettings.fogMode} density={RenderSettings.fogDensity} color={RenderSettings.fogColor}");

        var fog = FindFirstObjectByType<FogOfWarController>();
        if (fog != null)
            sb.AppendLine($"[Туман] enabled={fog.enabled} darkness={fog.darkness} range={fog.viewRange:F2} angle={fog.viewConeAngleDeg} diagMode={fog.diagnosticMode} visionTarget={(fog.visionTarget != null ? fog.visionTarget.name : "NULL")}");
        else sb.AppendLine("[Туман] НЕ НАЙДЕН");

        var ps1 = cam != null ? cam.GetComponent<Ps1Filter>() : null;
        if (ps1 != null) sb.AppendLine($"[PS1] enabled={ps1.enabled} filterEnabled={ps1.filterEnabled}");

        var vol = FindFirstObjectByType<Volume>();
        if (vol != null) sb.AppendLine($"[Volume] '{vol.name}' enabled={vol.enabled} weight={vol.weight} profile={(vol.sharedProfile != null ? vol.sharedProfile.name : "NULL")}");

        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp != null)
            sb.AppendLine($"[URP] asset='{urp.name}' msaa={urp.msaaSampleCount} hdr={urp.supportsHDR} shadowDist={urp.shadowDistance}");

        sb.AppendLine($"[Проба] {_probeStatus}");
        return sb.ToString();
    }

    void DumpState()
    {
        string dump = BuildStateDump();
        GUIUtility.systemCopyBuffer = dump;
        Debug.Log(dump, this);
        _probeStatus = "дамп скопирован в буфер обмена (Ctrl+V мне в чат)";
    }

    // ---------------------------------------------------------------- hud

    void OnGUI()
    {
        if (!showHud) return;

        string dump = BuildStateDump();
        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 13,
            alignment = TextAnchor.UpperLeft,
            wordWrap = false,
            richText = true
        };
        style.normal.textColor = Color.white;

        GUI.Box(new Rect(10, 10, 660, 470), "");
        GUI.Label(new Rect(20, 16, 640, 450),
            "<b>FPS RENDER DEBUG</b>  (F6 — скрыть)\n" +
            "<color=#ffe820>F10 — проба (куб+шар)</color>  <color=#ffe820>F11 — пост вкл/выкл</color>  <color=#ffe820>F12 — дамп в буфер</color>\n\n" +
            dump +
            $"\n<i>{_probeStatus}</i>", style);
    }
}
