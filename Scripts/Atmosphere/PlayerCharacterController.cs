using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The little person you control. Mouse moves -> the character turns to face
/// the cursor; WASD walks relative to the facing (W = forward, A/D = strafe).
/// Moves with a CharacterController whose capsule is auto-sized to the model,
/// so physics just works. The fog-of-war cone follows their gaze (head height).
/// </summary>
public class PlayerCharacterController : MonoBehaviour
{
    public static PlayerCharacterController Instance { get; private set; }

    [Header("References")]
    [Tooltip("The little person. Empty = spawned from `characterPrefab`, else placeholder.")]
    public Transform character;
    [Tooltip("Real animated character model (fbx). Auto-assigned by the setup.")]
    public GameObject characterPrefab;
    [Tooltip("The character spawns next to this (the hero car).")]
    public Transform spawnNear;
    public Camera viewCamera;

    [Header("Model fitting")]
    [Tooltip("Character height in meters after auto-fit.")]
    public float targetHeight = 1.75f;
    [Tooltip("Extra yaw if the model faces the wrong way (degrees).")]
    public float modelYawOffset = 0f;
    [Tooltip("Height of the head/eyes above ground - the fog cone starts here.")]
    public float headHeight = 1.55f;

    [Header("Spawn")]
    public Vector3 spawnOffset = new Vector3(2.4f, 0f, 1.4f);

    [Header("Turning (mouse-driven)")]
    public float turnSpeed = 9f;
    public float groundY = 0f;
    [Tooltip("Layers the aiming ray may hit (should include the car parts layer).")]
    public LayerMask interactMask = ~0;

    [Header("Flashlight (real light that paints color on lit objects)")]
    [Tooltip("Spot light on the character's head, shining where they look. This is what gives nearby objects their color back.")]
    public bool flashlightEnabled = true;
    public Color flashlightColor = new Color(1f, 0.93f, 0.8f);
    public float flashlightIntensity = 4.5f;
    public float flashlightRange = 11f;
    [Range(10f, 120f)] public float flashlightAngle = 55f;
    [Tooltip("Downward tilt of the beam (degrees).")]
    public float flashlightTilt = 15f;
    [Tooltip("Off by default: a shadow-casting torch self-shadows nearby objects into pitch black.")]
    public bool flashlightCastsShadows = false;

    [Header("Movement (WASD, global screen-relative)")]
    [Tooltip("Match this to the walk clip's foot tempo so the character doesn't slide.")]
    public float moveSpeed = 1.7f;
    [Tooltip("Sprint multiplier while holding Left Shift. Animation tempo follows automatically.")]
    public float sprintMultiplier = 1.8f;
    [Tooltip("Speed multiplier while backpedaling (S when facing the movement).")]
    [Range(0.2f, 1f)] public float backwardSpeedMultiplier = 0.6f;
    [Tooltip("Speed multiplier while strafing sideways.")]
    [Range(0.2f, 1f)] public float strafeSpeedMultiplier = 0.75f;

    [Header("Walk cycle sync (no foot sliding, diagonals blend)")]
    [Tooltip("Meters the character covers per full walk-cycle of the animation. Tune until feet stick to the ground.")]
    public float strideLength = 1.5f;

    Vector3 _facingDir = Vector3.forward;
    public Vector3 FacingDir => _facingDir;

    CharacterController _cc;
    Animation _legacyAnim;
    Light _torch;
    float _vy;
    float _floorY = 0f;
    float _currentSpeed;

    // clip name cache (resolved from the legacy Animation component)
    string _clipIdle, _clipForward, _clipBack, _clipLeft, _clipRight;
    float _walkPhase;        // 0..1 - shared phase of all movement clips
    bool _halfStepDone;

    /// <summary>World point of the head/eyes - the fog cone origin.</summary>
    public Vector3 HeadPoint => character != null
        ? character.position + Vector3.up * headHeight
        : Vector3.zero;

    void Awake()
    {
        Instance = this;
        if (viewCamera == null) viewCamera = Camera.main;
    }

    void Update()
    {
        if (character == null)
        {
            // self-heal: if nothing assigned the spawn point, use the hero car
            if (spawnNear == null && FogOfWarController.Instance != null)
                spawnNear = FogOfWarController.Instance.car;
            if (spawnNear != null) SpawnNow();
            return;
        }
        if (viewCamera == null && Camera.main != null) viewCamera = Camera.main;
        if (viewCamera == null) return;

        TurnTowardCursor();
        MoveWASD();

        // the torch must follow the GAZE, not the mesh forward: modelYawOffset
        // used to make it point backwards, so hovered objects went black
        if (_torch != null && _facingDir.sqrMagnitude > 0.01f)
            _torch.transform.rotation = Quaternion.LookRotation(_facingDir, Vector3.up)
                                        * Quaternion.Euler(-flashlightTilt, 0f, 0f);
    }

    void TurnTowardCursor()
    {
        Ray ray = MouseRayUtil.CursorRay(viewCamera);

        // Aim at the actual world surface under the cursor, including walls.
        // The old code accepted a hit only for CarPartClickable. When the cursor
        // was over a wall, it discarded that hit and projected the ray onto the
        // ground plane; in an isometric view this shifts the horizontal aim point
        // and makes the fog cone/shadow wedge disagree with the surface being viewed.
        Vector3 target = character.position;
        bool aimed = false;
        if (Physics.Raycast(ray, out var hit, 200f, interactMask, QueryTriggerInteraction.Ignore))
        {
            Transform hitTransform = hit.collider.transform;
            bool hitOwnCharacter = hitTransform == character || hitTransform.IsChildOf(character);
            if (!hitOwnCharacter)
            {
                target = hit.point;
                aimed = true;
            }
        }

        if (!aimed)
        {
            Plane plane = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
            if (!plane.Raycast(ray, out float dist)) return;
            target = ray.GetPoint(dist);
        }

        Vector3 dir = target - character.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0004f) return;

        _facingDir = Vector3.Slerp(_facingDir, dir.normalized, Time.deltaTime * turnSpeed).normalized;
        character.rotation = Quaternion.LookRotation(_facingDir, Vector3.up) * Quaternion.Euler(0f, modelYawOffset, 0f);
    }

    void MoveWASD()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        Vector2 input = Vector2.zero;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) input.y += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) input.y -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) input.x += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) input.x -= 1f;

        // GLOBAL screen-relative controls (Project Zomboid style):
        // W = away from camera, A = screen left - always, regardless of facing.
        // The character model may visually backpedal - that is intended;
        // only the cursor turns the body.
        Vector3 camF = viewCamera.transform.forward; camF.y = 0f;
        Vector3 camR = viewCamera.transform.right;   camR.y = 0f;
        if (camF.sqrMagnitude < 1e-6f) camF = Vector3.forward;
        if (camR.sqrMagnitude < 1e-6f) camR = Vector3.right;
        Vector3 move = camF.normalized * input.y + camR.normalized * input.x;
        if (move.sqrMagnitude > 1f) move.Normalize();

        // direction-relative speed: backpedal and strafing are slower than walking forward
        float dirMul = 1f;
        if (move.sqrMagnitude > 0.001f)
        {
            float dfn = Vector3.Dot(move.normalized, _facingDir.normalized);
            if (dfn < -0.4f) dirMul = backwardSpeedMultiplier;
            else if (Mathf.Abs(dfn) <= 0.4f) dirMul = strafeSpeedMultiplier;
        }

        float speed = moveSpeed * dirMul * (kb.leftShiftKey.isPressed ? sprintMultiplier : 1f);
        _currentSpeed = input.sqrMagnitude > 0.001f ? speed : 0f;
        UpdateAnimationState(input, move);

        if (input.sqrMagnitude < 0.001f) { ApplyGravity(); return; }

        ApplyGravity();
        Vector3 disp = move * (speed * Time.deltaTime) + Vector3.up * (_vy * Time.deltaTime);

        if (_cc != null) _cc.Move(disp);
        else character.position += disp;

        ClampToFloor();
    }

    /// <summary>
    /// Picks the right walk cycle from the legacy Animation component:
    /// idle when standing; forward/back/strafe relative to the CHARACTER'S
    /// FACING FRAME (no model yaw offset involved - selection can't invert).
    /// Diagonals BLEND two clips by weight (forward + strafe), all movement
    /// clips share one walk phase driven by the actual ground speed - feet
    /// never slide, and footstep sounds fire exactly when a foot lands.
    /// </summary>
    void UpdateAnimationState(Vector2 input, Vector3 move)
    {
        if (_legacyAnim == null && character != null)
            _legacyAnim = character.GetComponent<Animation>() ?? character.GetComponentInChildren<Animation>();

        bool moving = input.sqrMagnitude > 0.001f && _currentSpeed > 0.05f;

        // Footsteps are gameplay audio and must not depend on whether an
        // optional legacy Animation component is present on the character.
        if (moving)
        {
            float prev = _walkPhase;
            _walkPhase += (_currentSpeed / Mathf.Max(0.3f, strideLength)) * Time.deltaTime;
            if (_walkPhase >= 1f)
            {
                _walkPhase -= 1f;
                OnFootstep();
            }
            else if (!_halfStepDone && _walkPhase >= 0.5f)
            {
                _halfStepDone = true;
                OnFootstep();
            }
            if (prev > _walkPhase) _halfStepDone = false;
        }

        if (_legacyAnim == null) return;

        ResolveClipNames();
        float dt = Time.deltaTime;
        float blend = 1f - Mathf.Exp(-12f * dt); // smooth weight transitions

        // movement weights: forward/back/strafe components of the move vector
        float wIdle = moving ? 0f : 1f;
        float wF = 0f, wB = 0f, wR = 0f, wL = 0f;
        if (moving)
        {
            // facing frame WITHOUT modelYawOffset: pure LookRotation of the gaze
            Quaternion fRot = Quaternion.LookRotation(_facingDir, Vector3.up);
            Vector3 local = Quaternion.Inverse(fRot) * move;
            float fw = Mathf.Max(0f, local.z);
            float bk = Mathf.Max(0f, -local.z);
            float rt = Mathf.Max(0f, local.x);
            float lf = Mathf.Max(0f, -local.x);
            float sum = fw + bk + rt + lf;
            if (sum < 0.05f) { fw = 1f; bk = rt = lf = 0f; sum = 1f; }
            fw /= sum; bk /= sum; rt /= sum; lf /= sum;

            // missing clips redirect their weight to the forward cycle
            wF = fw + bk * (_clipBack == null ? 1f : 0f)
                    + rt * (_clipRight == null ? 1f : 0f)
                    + lf * (_clipLeft == null ? 1f : 0f);
            wB = _clipBack != null ? bk : 0f;
            wR = _clipRight != null ? rt : 0f;
            wL = _clipLeft != null ? lf : 0f;
        }

        ApplyWeight(_clipIdle, wIdle, blend, false);
        ApplyWeight(_clipForward, moving ? wF : 0f, blend, true);
        ApplyWeight(_clipBack, moving ? wB : 0f, blend, true);
        ApplyWeight(_clipRight, moving ? wR : 0f, blend, true);
        ApplyWeight(_clipLeft, moving ? wL : 0f, blend, true);

        // keep the component alive even when all weights dip during transitions
        if (!_legacyAnim.isPlaying)
        {
            string any = moving ? (_clipForward ?? _clipIdle) : (_clipIdle ?? _clipForward);
            if (!string.IsNullOrEmpty(any)) _legacyAnim.Play(any);
        }
    }

    void ApplyWeight(string clipName, float target, float blend, bool syncPhase)
    {
        if (string.IsNullOrEmpty(clipName)) return;
        var st = _legacyAnim[clipName];
        if (st == null) return;

        st.weight = Mathf.Lerp(st.weight, target, blend);
        st.enabled = st.weight > 0.004f;
        if (st.enabled && syncPhase) st.normalizedTime = _walkPhase;
    }

    void OnFootstep()
    {
        var audio = AmbientAudioController.Instance;
        if (audio == null)
            audio = FindFirstObjectByType<AmbientAudioController>();
        if (audio == null) return;

        // resolve the surface under the feet (tilemap binds textures to classes)
        if (_tilemap == null)
        {
            foreach (var m in FindObjectsByType<SnowTilemapData>(FindObjectsSortMode.None))
            {
                _tilemap = m;
                break;
            }
        }
        var surface = _tilemap != null ? _tilemap.GetSurfaceAtWorldPosition(character.position) : null;
        audio.PlayFootstepForSurface(surface);
    }

    SnowTilemapData _tilemap;

    /// <summary>Resolve movement clip names once from the registered clips.</summary>
    void ResolveClipNames()
    {
        _clipIdle = FindClip("idle");
        _clipForward = FindClip("walking");
        _clipBack = FindClip("back");
        _clipLeft = FindClip("left");
        _clipRight = FindClip("right");
    }

    /// <summary>
    /// Find a registered clip by keyword. STRICT for "walking": exact "Walking"
    /// first, then a clean walk cycle - never Injured/Strafe/Turn/Stop variants.
    /// </summary>
    string FindClip(string keyword)
    {
        string best = null;
        foreach (AnimationState st in _legacyAnim)
        {
            var n = st.name.ToLowerInvariant();
            switch (keyword)
            {
                case "idle":
                    if (n.Contains("idle")) return st.name;
                    break;
                case "back":
                    if (n.Contains("back")) return st.name;
                    break;
                case "left":
                    if (n.Contains("left")) return st.name;
                    break;
                case "right":
                    if (n.Contains("right")) return st.name;
                    break;
                case "walking":
                    if (n == "walking") return st.name; // exact hit
                    if (n.Contains("walk") && !n.Contains("back") && !n.Contains("injured")
                        && !n.Contains("strafe") && !n.Contains("turn") && !n.Contains("stop")
                        && !n.Contains("idle"))
                        best ??= st.name;
                    break;
            }
        }
        return best;
    }

    void ApplyGravity()
    {
        _vy -= 9.81f * Time.deltaTime;
        if (_cc != null) _cc.Move(Vector3.up * (_vy * Time.deltaTime));
        else character.position += Vector3.up * (_vy * Time.deltaTime);
        ClampToFloor();
    }

    void ClampToFloor()
    {
        if (character.position.y < _floorY)
        {
            character.position = new Vector3(character.position.x, _floorY, character.position.z);
            _vy = 0f;
        }
    }

    /// <summary>Spawns the character: prefab first, placeholder second. ALWAYS fresh - stale saved instances caused the placeholder-forever bug.</summary>
    public void SpawnNow()
    {
        // destroy any previous instance (old placeholder saved into the scene etc.)
        var existing = GameObject.Find("Player_Character");
        if (existing != null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(existing);
            else Destroy(existing.gameObject);
#else
            Destroy(existing.gameObject);
#endif
        }

        if (characterPrefab != null)
        {
            var go = Instantiate(characterPrefab);
            go.name = "Player_Character";
            character = go.transform;
            PlaceAndFit();
            Debug.Log($"[ColdCar] Player character spawned from prefab '{characterPrefab.name}' at {character.position}");
        }
        else
        {
            Vector3 pos = SpawnPosition(out float ground);
            pos.y = ground;
            character = BuildPlaceholderHuman(pos).transform;
            FitCharacterController(pos);
            Debug.Log($"[ColdCar] Player character spawned at {pos} (placeholder human - no prefab assigned)");
        }

        _facingDir = character.forward;
        _facingDir.y = 0f;
        if (_facingDir.sqrMagnitude < 0.01f) _facingDir = Vector3.forward;
        character.rotation = Quaternion.LookRotation(_facingDir, Vector3.up) * Quaternion.Euler(0f, modelYawOffset, 0f);

        WireFlashlight();
    }

    /// <summary>Head-mounted spotlight. Aiming happens in Update along the GAZE.</summary>
    void WireFlashlight()
    {
        _torch = character.GetComponentInChildren<Light>();
        if (!flashlightEnabled)
        {
            if (_torch != null) Destroy(_torch.gameObject);
            _torch = null;
            return;
        }
        if (_torch == null)
        {
            var go = new GameObject("Flashlight");
            go.transform.SetParent(character, false);
            _torch = go.AddComponent<Light>();
            _torch.type = LightType.Spot;
            _torch.innerSpotAngle = 0f;
        }
        _torch.color = flashlightColor;
        _torch.intensity = flashlightIntensity;
        _torch.range = flashlightRange;
        _torch.spotAngle = flashlightAngle;
        _torch.shadows = flashlightCastsShadows ? LightShadows.Soft : LightShadows.None;
        // origin stays AT the head (not pushed forward): a forward offset put the
        // light INSIDE an adjacent car's body when point-blank, and a light inside
        // a closed mesh lights nothing (all surfaces face away) - pitch-black car
        // with the beam escaping behind it
        _torch.transform.localPosition = new Vector3(0f, headHeight, 0.1f);
    }

    Vector3 SpawnPosition(out float ground)
    {
        Vector3 pos = Vector3.zero;
        ground = 0f;
        if (spawnNear != null)
        {
            pos = spawnNear.position + spawnOffset;
            float bottom = float.PositiveInfinity;
            foreach (var r in spawnNear.GetComponentsInChildren<Renderer>())
                bottom = Mathf.Min(bottom, r.bounds.min.y);
            if (!float.IsInfinity(bottom)) ground = bottom;
        }
        return pos;
    }

    void PlaceAndFit()
    {
        Vector3 pos = SpawnPosition(out float ground);
        character.position = new Vector3(pos.x, ground, pos.z);
        _floorY = ground;

        var rends = character.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return;

        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        float h = Mathf.Max(0.001f, b.size.y);
        float s = targetHeight / h;
        character.localScale = character.localScale * s;

        // recompute after scaling and put the feet on the ground
        b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        character.position += new Vector3(0f, -b.min.y, 0f);

        FitCharacterController(b.center);
    }

    /// <summary>CharacterController capsule sized from the model's world bounds.</summary>
    void FitCharacterController(Vector3 boundsCenter)
    {
        var rends = character.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return;

        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);

        _cc = character.GetComponent<CharacterController>();
        if (_cc == null) _cc = character.gameObject.AddComponent<CharacterController>();

        _cc.height = Mathf.Max(0.5f, b.size.y * 0.96f);
        _cc.radius = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z) * 0.45f, 0.2f, _cc.height * 0.45f);
        _cc.center = character.InverseTransformPoint(new Vector3(b.center.x, b.center.y, b.center.z));
        _cc.slopeLimit = 45f;
        _cc.stepOffset = 0.35f;
        _cc.skinWidth = 0.03f;
    }

    GameObject BuildPlaceholderHuman(Vector3 pos)
    {
        var root = new GameObject("Player_Character");
        root.transform.position = pos;

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
        body.transform.localScale = new Vector3(0.42f, 0.55f, 0.32f);

        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = new Vector3(0f, 1.55f, 0f);
        head.transform.localScale = Vector3.one * 0.3f;

        var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        nose.name = "Nose";
        nose.transform.SetParent(head.transform, false);
        nose.transform.localPosition = new Vector3(0f, 0f, 0.5f);
        nose.transform.localScale = new Vector3(0.14f, 0.14f, 0.35f);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.22f, 0.3f, 0.45f));
        foreach (var r in root.GetComponentsInChildren<Renderer>())
            r.sharedMaterial = mat;
        var noseMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        noseMat.SetColor("_BaseColor", new Color(0.85f, 0.55f, 0.35f));
        nose.GetComponent<Renderer>().sharedMaterial = noseMat;

        // the CharacterController handles physics; primitive colliders would fight it
        foreach (var col in root.GetComponentsInChildren<Collider>())
            Destroy(col);

        return root;
    }
}
