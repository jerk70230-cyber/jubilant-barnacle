using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// One-button setup, usable from the editor menu AND automatically on Play.
/// Creates/repairs the atmosphere rig for whatever scene is open:
/// atmosphere object, fog-of-war + blizzard + interaction + cursor,
/// PS1 filter on the camera, clickable car parts, HideInGame on 'Sphere'.
/// If everything is already wired - does nothing.
/// </summary>
public static class ColdCarBootstrap
{
    /// <summary>Runs automatically on Play: ensures the rig is complete (new components included).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void AutoSetupOnPlay()
    {
        // The procedural MVP has its own first-person camera/player rig. Running
        // the legacy setup there would add the isometric camera follower and a
        // second player controller, fighting the FPS camera.
        if (UnityEngine.Object.FindFirstObjectByType<ProceduralFirstPersonController>() != null)
        {
            Debug.Log("[ColdCar] Procedural FPS scene detected; legacy isometric bootstrap skipped.");
            return;
        }

        // never skip: new features (player character etc.) must be added even to
        // scenes that were wired before they existed. Run() is idempotent.
        Run("AutoSetup(play)");
    }

    public static void Run(string source)
    {
        // --- atmosphere root with all controllers ---
        var atmo = GameObject.Find("--- ATMOSPHERE ---");
        if (atmo == null)
        {
            atmo = new GameObject("--- ATMOSPHERE ---");
            Log(source, "created --- ATMOSPHERE ---");
        }
        EnsureComponent<FogOfWarController>(atmo);
        EnsureComponent<BlizzardController>(atmo);
        EnsureComponent<PlayerCharacterController>(atmo);
        EnsureComponent<CarInteractionManager>(atmo);
        EnsureComponent<CustomCursor>(atmo);
        EnsureComponent<AmbientAudioController>(atmo);

        // --- stale runtime leftovers saved into the scene (edit mode only) ---
        if (!Application.isPlaying)
        {
            DestroyStale("Snow_Cover", source);
            DestroyStale("Blizzard_Snow", source);
        }

        // --- references ---
        var fog = atmo.GetComponent<FogOfWarController>();
        var inter = atmo.GetComponent<CarInteractionManager>();
        if (fog.viewCamera == null) fog.viewCamera = Camera.main;
        inter.viewCamera = fog.viewCamera != null ? fog.viewCamera : Camera.main;

        // --- PS1 filter on the camera ---
        var cam = fog.viewCamera != null ? fog.viewCamera : Camera.main;
        if (cam != null && cam.GetComponent<Ps1Filter>() == null)
        {
#if UNITY_EDITOR
            UnityEditor.Undo.AddComponent<Ps1Filter>(cam.gameObject);
#else
            cam.gameObject.AddComponent<Ps1Filter>();
#endif
            Log(source, $"Ps1Filter added to '{cam.name}'");
        }

        // --- Zomboid-style camera follow ---
        if (cam != null && cam.GetComponent<CameraFollowController>() == null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.AddComponent<CameraFollowController>(cam.gameObject);
            else cam.gameObject.AddComponent<CameraFollowController>();
#else
            cam.gameObject.AddComponent<CameraFollowController>();
#endif
            Log(source, $"CameraFollowController added to '{cam.name}'");
        }

        // --- car anchor + clickable parts ---
        var car = FindHeroCar(cam);
        if (car != null)
        {
            fog.car = car.transform;
            MakePartsClickable(car, source);
            // the little person spawns next to the hero car
            var pc = atmo.GetComponent<PlayerCharacterController>();
            pc.spawnNear = car.transform;
#if UNITY_EDITOR
            if (!Application.isPlaying) WireEditorCharacter(pc, source);
#endif
            if (pc.character == null && Application.isPlaying) pc.SpawnNow();
            Log(source, $"hero car = '{car.name}', player character spawns next to it");
        }
        else
        {
            Log(source, "no car object found (name should contain 'car') - assign FogOfWarController.car manually");
        }

#if UNITY_EDITOR
        // works in play mode too: AssetDatabase is available inside the editor,
        // so clips wire up even if the scene was saved without them
        WireEditorAudio(atmo.GetComponent<AmbientAudioController>(), source);
#endif

        // --- helper objects like 'Sphere' must not render in play mode ---
        int hidden = 0;
        foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            if (go.name == "Sphere" && go.GetComponent<HideInGame>() == null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) UnityEditor.Undo.AddComponent<HideInGame>(go);
                else go.AddComponent<HideInGame>();
#else
                go.AddComponent<HideInGame>();
#endif
                hidden++;
            }
        }
        if (hidden > 0) Log(source, $"HideInGame added to {hidden} object(s) named 'Sphere'");

        Log(source, "setup complete");
    }

    static void EnsureComponent<T>(GameObject go) where T : Component
    {
        if (go.GetComponent<T>() == null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.AddComponent<T>(go);
            else go.AddComponent<T>();
#else
            go.AddComponent<T>();
#endif
        }
    }

    static void DestroyStale(string name, string source)
    {
        var stale = GameObject.Find(name);
        if (stale != null)
        {
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(stale);
#else
            UnityEngine.Object.Destroy(stale);
#endif
            Log(source, $"removed stale scene object '{name}' (rebuilt automatically on play)");
        }
    }

    /// <summary>
    /// Picks the HERO car among all car-like roots: the one closest to the camera.
    /// Override any time by dragging a car into FogOfWarController.car.
    /// </summary>
    public static GameObject FindHeroCar(Camera cam)
    {
        var cars = new List<GameObject>();
        foreach (var rend in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var root = rend.transform.root.gameObject;
            if (!cars.Contains(root) && root.name.ToLowerInvariant().Contains("car"))
                cars.Add(root);
        }
        if (cars.Count == 0) return null;
        if (cars.Count == 1) return cars[0];

        GameObject best = null;
        float bestD = float.MaxValue;
        Vector3 o = cam != null ? cam.transform.position : Vector3.zero;
        foreach (var carObj in cars)
        {
            float d = Vector3.SqrMagnitude(carObj.transform.position - o);
            if (d < bestD) { bestD = d; best = carObj; }
        }
        return best;
    }

    static void MakePartsClickable(GameObject car, string source)
    {
        int added = 0;
        foreach (var rend in car.GetComponentsInChildren<Renderer>())
        {
            var go = rend.gameObject;
            if (go == car) continue;
            if (go.GetComponent<CarPartClickable>() != null) continue;

            if (go.GetComponent<Collider>() == null)
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = rend.transform.InverseTransformPoint(rend.bounds.center);
                var localSize = rend.transform.InverseTransformVector(rend.bounds.size);
                box.size = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.Undo.AddComponent<CarPartClickable>(go);
            else go.AddComponent<CarPartClickable>();
#else
            go.AddComponent<CarPartClickable>();
#endif
            added++;
        }
        if (added > 0) Log(source, $"made {added} car parts clickable");
    }

    static void Log(string source, string msg)
    {
        Debug.Log($"[ColdCar:{source}] {msg}");
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only: auto-assign audio clips. Wind/breath from Assets/Sounds,
    /// snow footsteps from the footstep pack folders.
    /// </summary>
    static void WireEditorAudio(AmbientAudioController audio, string source)
    {
        var windGuids = UnityEditor.AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Sounds" });
        foreach (var guid in windGuids)
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            var n = clip.name.ToLowerInvariant();
            if (audio.windLoop == null && (n.Contains("wind") || n.Contains("ветер"))) audio.windLoop = clip;
            else if (audio.breathLoop == null && (n.Contains("breath") || n.Contains("дых"))) audio.breathLoop = clip;
        }

        var stepGuids = UnityEditor.AssetDatabase.FindAssets("t:AudioClip",
            new[] { "Assets/Footstep(Snow and Grass)/Stepssingle sound/snow" });
        var steps = new List<AudioClip>();
        foreach (var guid in stepGuids)
        {
            var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            if (clip != null) steps.Add(clip);
        }
        if (steps.Count > 0) audio.footstepClips = steps.ToArray();

        Log(source, $"audio wired: wind={audio.windLoop != null}, breath={audio.breathLoop != null}, {steps.Count} footstep clips");
    }

    /// <summary>
    /// Editor-only: builds the player character from the rig fbx in
    /// Assets/Anims/Player (skinned man_2 + skeleton), gathers ALL animation
    /// clips from that folder into one animator controller, and carries over
    /// the old model's materials. The instance is saved with the scene.
    /// </summary>
    static void WireEditorCharacter(PlayerCharacterController pc, string source)
    {
        const string AnimDir =
            "Assets/Anims/Player";

        var animPaths = UnityEditor.AssetDatabase.FindAssets("t:Model", new[] { AnimDir })
            .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
            .Distinct()
            .OrderBy(p => p)
            .ToArray();
        if (animPaths.Length == 0)
        {
            Log(source, $"no animation fbx found in {AnimDir} - placeholder will be used");
            return;
        }

        // base model = plain Walking (skinned mesh + skeleton + clip)
        string basePath = animPaths.FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p) == "Walking")
                          ?? animPaths[0];
        var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
        if (prefab == null) return;

        // carry-over: remember old materials by renderer name
        var oldChar = GameObject.Find("Player_Character");
        var oldMats = new Dictionary<string, Material[]>();
        if (oldChar != null)
            foreach (var r in oldChar.GetComponentsInChildren<Renderer>(true))
                if (r.sharedMaterials.Length > 0)
                    oldMats[r.name] = r.sharedMaterials;

        pc.characterPrefab = prefab;
        pc.SpawnNow(); // always fresh: destroys the old instance, instantiates the rig

        // carry-over: apply remembered materials where renderer names match
        int matFixed = 0;
        foreach (var r in pc.character.GetComponentsInChildren<Renderer>(true))
        {
            if (oldMats.TryGetValue(r.name, out var mats) && mats != null && mats.Length > 0)
            {
                r.sharedMaterials = mats;
                matFixed++;
            }
        }

        // legacy Animation component: clips registered directly on it, no
        // AnimatorController asset needed (its editor API churns between versions).
        // Mixamo takes are ALL named "mixamo.com" inside - register each clip
        // under a canonical name derived from the FILE name instead.
        foreach (var a in pc.character.GetComponentsInChildren<Animator>(true))
            UnityEngine.Object.DestroyImmediate(a);

        var legacy = pc.character.GetComponent<Animation>();
        if (legacy == null) legacy = pc.character.gameObject.AddComponent<Animation>();

        var registered = new List<string>();
        string defaultClip = null;
        foreach (var p in animPaths)
        {
            var canonical = System.IO.Path.GetFileNameWithoutExtension(p);
            // WalkingTurn180 removed from the game for now (plays everywhere, confuses movement)
            if (canonical.ToLowerInvariant().Contains("turn")) continue;
            foreach (var c in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>())
            {
                if (c.name.StartsWith("__", StringComparison.Ordinal)) continue;
                c.legacy = true;
                c.wrapMode = WrapMode.Loop;
                if (legacy.GetClip(canonical) != null) legacy.RemoveClip(canonical);
                legacy.AddClip(c, canonical);
                registered.Add(canonical);

                var n = canonical.ToLowerInvariant();
                if (defaultClip == null && n.Contains("idle")) defaultClip = canonical;
                if (defaultClip == null && n == "walking") defaultClip = canonical;
            }
        }
        if (defaultClip == null && registered.Count > 0) defaultClip = registered[0];

        if (defaultClip != null)
        {
            legacy.clip = legacy.GetClip(defaultClip);
            legacy.Play(defaultClip);
            Log(source, $"legacy animation wired: {registered.Count} clips ({string.Join(", ", registered)}), default '{defaultClip}'");
        }

        Log(source, $"character wired from '{basePath}' ({matFixed} material slots carried over)");
    }
#endif
}
