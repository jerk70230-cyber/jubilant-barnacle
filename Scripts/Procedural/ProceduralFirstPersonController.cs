using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Minimal first-person controller for the procedural-room MVP. The player root
/// has only a CharacterController: no body mesh is created or rendered.
/// Footsteps are routed through the project's existing AmbientAudioController.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class ProceduralFirstPersonController : MonoBehaviour
{
    [Header("References")]
    public Camera playerCamera;
    public Light flashlight;
    public ProceduralRoomManager roomManager;
    [Tooltip("Single inspector control for flashlight, lighting and all visual filters.")]
    public ProceduralFPSVisualSettings visualSettings;
    [Tooltip("Press F to switch the flashlight.")]
    [HideInInspector] public bool flashlightStartsOn = false; // moved to ProceduralFPSVisualSettings
    public LayerMask interactMask = ~0;

    [Header("Look")]
    [Range(0.01f, 0.5f)] public float mouseSensitivity = 0.12f;
    [Range(60f, 100f)] public float verticalLookLimit = 88f;
    public bool lockCursorOnStart = true;

    [Header("Movement")]
    [Min(0.1f)] public float walkSpeed = 2.0f;
    [Min(0.1f)] public float runSpeed = 3.7f;
    [Min(0f)] public float gravity = 22f;
    [Min(0f)] public float footstepDistance = 1.55f;

    [Header("Camera movement")]
    [Tooltip("Small camera movement while walking; running gets a stronger shake.")]
    [Range(0f, 0.08f)] public float walkShake = 0.012f;
    [Range(0f, 0.14f)] public float runShake = 0.036f;
    [Range(0f, 1f)] public float walkRoll = 0.18f;
    [Range(0f, 2f)] public float runRoll = 0.65f;
    [Min(1f)] public float cameraSmoothing = 15f;

    [Header("Interaction")]
    [Min(1f)] public float interactDistance = 3.2f;
    public bool useMouseClickAsInteract = true;

    CharacterController _controller;
    Vector3 _cameraBaseLocalPosition;
    Quaternion _cameraBaseLocalRotation;
    float _pitch;
    float _verticalVelocity;
    float _cameraPhase;
    int _plantIndex = -1;
    bool _cursorLocked;
    OneWayProceduralDoor _lookedAtDoor;
    bool _isRunning;
    Vector3 _lastPlanarVelocity;
    // Bodycam-lite state
    float _moveFactor;
    float _pitchSmoothed;
    float _yawLagOffset;
    float _accelPitchSmooth;
    float _prevSpeed;
    float _dipOffset;
    float _fallSpeed;
    bool _wasGrounded = true;
    float _baseFov;
    Vector2 _mouseSmoothDelta;
    // Инерция движения + тилт от поворота
    Vector3 _planarVelocity;
    float _yawRate;
    float _yawRateSmooth;
    float _runBlend;
    InventoryUI _inventory;
    Vector3 _forestLastLateUpdatePosition;
    bool _forestHasLateUpdatePosition;

    public Transform VisionTarget => playerCamera != null ? playerCamera.transform : transform;
    public bool IsMoving => _lastPlanarVelocity.sqrMagnitude > 0.01f;
    public bool IsRunning => _isRunning && IsMoving;

    void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _controller.height = 1.8f;
        _controller.radius = 0.31f;
        _controller.center = new Vector3(0f, 0.9f, 0f);
        _controller.slopeLimit = 48f;
        _controller.stepOffset = 0.35f; // ступени 0.2–0.3 м перешагиваются без прыжка
        _controller.skinWidth = 0.035f;
        _controller.minMoveDistance = 0f;

        if (playerCamera == null) playerCamera = GetComponentInChildren<Camera>();
        if (playerCamera != null)
        {
            _cameraBaseLocalPosition = playerCamera.transform.localPosition;
            _cameraBaseLocalRotation = playerCamera.transform.localRotation;
        }

        if (flashlight == null && playerCamera != null)
            flashlight = playerCamera.GetComponentInChildren<Light>();

        if (playerCamera != null) _baseFov = playerCamera.fieldOfView;
        _pitchSmoothed = 0f;

        if (roomManager == null) roomManager = FindFirstObjectByType<ProceduralRoomManager>();
        if (visualSettings == null) visualSettings = GetComponent<ProceduralFPSVisualSettings>();
    }

    void Start()
    {
        if (visualSettings != null) visualSettings.Initialize(this);
        else if (flashlight != null) flashlight.enabled = flashlightStartsOn;
        // Инвентарь на TAB: компонент вешается автоматически, ручки — в
        // ProceduralFPSVisualSettings, секция «ИНВЕНТАРЬ (TAB)».
        if (GetComponent<InventoryUI>() == null) gameObject.AddComponent<InventoryUI>();
        _inventory = GetComponent<InventoryUI>();
        SetCursorLocked(lockCursorOnStart);
    }

    /// <summary>Вызывается инвентарём при открытии/закрытии: освобождает курсор.</summary>
    public void OnInventoryToggled(bool open)
    {
        if (open) SetCursorLocked(false);
        else SetCursorLocked(true);
    }

    bool IsInventoryOpen() => _inventory != null && _inventory.IsOpen;

    void Update()
    {
        Vector3 positionAtUpdateStart = transform.position;
        if (roomManager != null && roomManager.HasPendingTransition && _forestHasLateUpdatePosition)
        {
            Vector3 betweenFramesDelta = positionAtUpdateStart - _forestLastLateUpdatePosition;
            if (betweenFramesDelta.sqrMagnitude > 0.01f)
            {
                Debug.LogWarning(
                    $"[Forest EXTERNAL MOVE] Between LateUpdate and Update: delta={betweenFramesDelta}, " +
                    $"from={_forestLastLateUpdatePosition}, to={positionAtUpdateStart}, frame={Time.frameCount}. " +
                    "This displacement happened outside ProceduralFirstPersonController.HandleMovement().",
                    this);
            }
        }

        HandleFlashlightToggle();
        HandleCursor();
        bool inventoryOpen = IsInventoryOpen();

        // Mouse look and world interaction require a locked cursor, but opening
        // the inventory is NOT a pause: WASD movement, gravity, footsteps,
        // ambience and all other world updates continue normally.
        if (_cursorLocked)
        {
            HandleLook();
            HandleInteraction();

            // TryOpenDoor builds the whole destination synchronously from this call.
            // Any player transform change here is not normal FPS movement.
            if (roomManager != null && roomManager.HasPendingTransition)
            {
                Vector3 interactionDelta = transform.position - positionAtUpdateStart;
                if (interactionDelta.sqrMagnitude > 0.000001f)
                {
                    Debug.LogWarning(
                        $"[Forest INTERACTION MOVE] Player moved during HandleInteraction/TryOpenDoor: " +
                        $"delta={interactionDelta}, before={positionAtUpdateStart}, after={transform.position}.",
                        this);
                }
            }
        }
        else
        {
            _lookedAtDoor = null;
        }

        if (_cursorLocked || inventoryOpen)
        {
            HandleMovement();
        }
        else
        {
            _lastPlanarVelocity = Vector3.zero;
            _isRunning = false;
            float decel = visualSettings != null ? visualSettings.moveDeceleration : 22f;
            _planarVelocity = Vector3.MoveTowards(_planarVelocity, Vector3.zero, decel * Time.deltaTime);
        }

        UpdateCameraMotion();
        UpdateFlashlight();
    }


    void HandleFlashlightToggle()
    {
        var keyboard = Keyboard.current;
        if (flashlight == null || keyboard == null || !keyboard.fKey.wasPressedThisFrame) return;
        flashlight.enabled = !flashlight.enabled;
    }

    void HandleCursor()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            SetCursorLocked(false);
        // Пока открыт инвентарь — клик мышью не захватывает курсор обратно.
        else if (!_cursorLocked && !IsInventoryOpen() && mouse != null && mouse.leftButton.wasPressedThisFrame)
            SetCursorLocked(true);
    }

    void SetCursorLocked(bool locked)
    {
        _cursorLocked = locked;
        _mouseSmoothDelta = Vector2.zero;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void HandleLook()
    {
        if (playerCamera == null || Mouse.current == null) return;
        Vector2 rawDelta = Mouse.current.delta.ReadValue();

        // Сглаживание мыши: сырой дельта-инпут микродёргается на высоком FPS.
        // mouseSmoothing 0..1 отображается в постоянную времени 1..50 мс.
        float smoothTime = visualSettings != null
            ? Mathf.Lerp(0.001f, 0.05f, visualSettings.mouseSmoothing)
            : 0.001f;
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0005f, smoothTime));
        _mouseSmoothDelta = Vector2.Lerp(_mouseSmoothDelta, rawDelta, k);
        Vector2 delta = _mouseSmoothDelta;

        float yaw = delta.x * mouseSensitivity;
        float pitchDelta = delta.y * mouseSensitivity;

        // Угловая скорость поворота — источник камера-тилта (наклон в сторону поворота).
        if (Time.deltaTime > 0f) _yawRate = yaw / Time.deltaTime;

        transform.Rotate(Vector3.up, yaw, Space.World);
        // Камера визуально «отстаёт» от поворота тела: накапливаем смещение,
        // которое UpdateCameraMotion плавно догоняет. Итоговая ориентация
        // камеры целиком собирается в UpdateCameraMotion.
        _yawLagOffset = Mathf.Clamp(_yawLagOffset - yaw, -8f, 8f);
        _pitch = Mathf.Clamp(_pitch - pitchDelta, -verticalLookLimit, verticalLookLimit);
    }

    void HandleMovement()
    {
        var keyboard = Keyboard.current;
        Vector2 input = Vector2.zero;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) input.y += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) input.y -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) input.x += 1f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) input.x -= 1f;
        }

        if (input.sqrMagnitude > 1f) input.Normalize();
        _isRunning = keyboard != null && keyboard.leftShiftKey.isPressed && input.sqrMagnitude > 0.001f;
        float speed = _isRunning ? runSpeed : walkSpeed;

        // Инерция: фактическая скорость плавно разгоняется/тормозит к целевой.
        // В воздухе управляемость слабее (airControl), инерция сохраняется.
        bool accelerating = input.sqrMagnitude > 0.01f;
        float accelRate = visualSettings != null
            ? (accelerating ? visualSettings.moveAcceleration : visualSettings.moveDeceleration)
            : (accelerating ? 16f : 22f);
        float airMul = visualSettings != null ? visualSettings.airControl : 0.35f;
        if (!_controller.isGrounded) accelRate *= airMul;
        _planarVelocity = Vector3.MoveTowards(_planarVelocity,
            (transform.right * input.x + transform.forward * input.y) * speed,
            accelRate * Time.deltaTime);

        Vector3 beforeMove = transform.position;

        if (_controller.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
        _verticalVelocity -= gravity * Time.deltaTime;

        Vector3 displacement = _planarVelocity + Vector3.up * _verticalVelocity;
        Vector3 requestedDelta = displacement * Time.deltaTime;
        CollisionFlags flags = _controller.Move(requestedDelta);
        Vector3 positionAfterMove = transform.position;
        Vector3 actualMoveDelta = positionAfterMove - beforeMove;

        if (roomManager != null && roomManager.HasPendingTransition)
        {
            Debug.Log(
                $"[Forest MOVE TRACE] frame={Time.frameCount}, dt={Time.deltaTime:0.0000}, " +
                $"before={beforeMove}, requestedDelta={requestedDelta}, after={positionAfterMove}, " +
                $"actualDelta={actualMoveDelta}, flags={flags}, grounded={_controller.isGrounded}, " +
                $"vY={_verticalVelocity:0.000}.",
                this);
        }

        if ((flags & CollisionFlags.Above) != 0 && _verticalVelocity > 0f)
            _verticalVelocity = 0f;
        if ((flags & CollisionFlags.Below) != 0 && _verticalVelocity < 0f)
            _verticalVelocity = -2f;

        Vector3 actualDelta = transform.position - beforeMove;
        actualDelta.y = 0f;
        Vector3 actualPlanarVelocity = Time.deltaTime > 0f ? actualDelta / Time.deltaTime : Vector3.zero;
        _lastPlanarVelocity = actualPlanarVelocity;
        // Звуки шагов триггерятся фазой боба в UpdateCameraMotion — так звук
        // и покачивание камеры всегда синхронны.
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit == null || roomManager == null || !roomManager.HasPendingTransition) return;

        Debug.Log(
            $"[Forest CONTROLLER HIT] frame={Time.frameCount}, object='{hit.collider.name}', " +
            $"type={hit.collider.GetType().Name}, layer={hit.collider.gameObject.layer}, " +
            $"point={hit.point}, normal={hit.normal}, moveDirection={hit.moveDirection}, " +
            $"moveLength={hit.moveLength:0.000}.",
            hit.collider);
    }

    void LateUpdate()
    {
        _forestLastLateUpdatePosition = transform.position;
        _forestHasLateUpdatePosition = true;
    }

    void PlayFootstep()
    {
        AmbientAudioController audio = AmbientAudioController.Instance;
        if (audio == null) audio = FindFirstObjectByType<AmbientAudioController>();
        if (audio == null) return;

        // Звук по поверхности: текстура пола под ногами → запись в
        // ProceduralRoomManager.footstepSurfaces. Бег — свой набор (Run),
        // ходьба — Walk, изредка шарканье (Scuff) для естественности.
        Texture2D floorTex = GetFloorTextureUnderfoot();
        RoomFootstepSurface surface = roomManager != null ? roomManager.FindFootstepSurface(floorTex) : null;
        if (surface == null)
        {
            audio.PlayFootstep();
            return;
        }

        AudioClip[] set;
        if (_isRunning && HasAnyClip(surface.footstepRunClips)) set = surface.footstepRunClips;
        else if (HasAnyClip(surface.footstepClips)) set = surface.footstepClips;
        else if (HasAnyClip(surface.footstepRunClips)) set = surface.footstepRunClips;
        else if (HasAnyClip(surface.scuffClips)) set = surface.scuffClips;
        else
        {
            audio.PlayFootstep();
            return;
        }
        // Изредка шарканье вместо шага (только на шаге, не на беге).
        if (!_isRunning && HasAnyClip(surface.scuffClips) && Random.value < 0.18f)
            set = surface.scuffClips;

        audio.PlayFootstepForClips(set, surface.volumeMultiplier);
    }

    static bool HasAnyClip(AudioClip[] clips)
    {
        if (clips == null) return false;
        foreach (AudioClip clip in clips)
            if (clip != null) return true;
        return false;
    }

    Texture2D GetFloorTextureUnderfoot()
    {
        Vector3 origin = transform.position + Vector3.up * 0.6f;
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        // Первый коллайдер может быть без рендерера (посадочная площадка леса,
        // коллайдер-дублёр рельефа) — перебираем попадания и соседей по общему
        // родителю, пока не найдём материал с текстурой.
        foreach (RaycastHit hit in hits)
        {
            Texture2D direct = FindBaseMapTexture(hit.collider.transform);
            if (direct != null) return direct;

            if (hit.collider.transform.parent == null) continue;
            foreach (Transform sibling in hit.collider.transform.parent)
            {
                if (sibling == hit.collider.transform) continue;
                MeshRenderer siblingRenderer = sibling.GetComponent<MeshRenderer>();
                if (siblingRenderer == null || siblingRenderer.sharedMaterial == null) continue;
                if (!siblingRenderer.bounds.Contains(hit.point)) continue;
                Texture2D siblingTex = siblingRenderer.sharedMaterial.HasProperty("_BaseMap")
                    ? siblingRenderer.sharedMaterial.GetTexture("_BaseMap") as Texture2D
                    : null;
                if (siblingTex != null) return siblingTex;
            }
        }
        return null;
    }

    static Texture2D FindBaseMapTexture(Transform transform)
    {
        for (int depth = 0; transform != null && depth < 3; depth++, transform = transform.parent)
        {
            MeshRenderer renderer = transform.GetComponent<MeshRenderer>();
            if (renderer == null || renderer.sharedMaterial == null) continue;
            if (!renderer.sharedMaterial.HasProperty("_BaseMap")) continue;
            return renderer.sharedMaterial.GetTexture("_BaseMap") as Texture2D;
        }
        return null;
    }

    void HandleInteraction()
    {
        _lookedAtDoor = null;
        if (playerCamera == null) return;

        Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, interactDistance, interactMask, QueryTriggerInteraction.Ignore))
            _lookedAtDoor = hit.collider.GetComponentInParent<OneWayProceduralDoor>();

        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        bool pressed = (keyboard != null && keyboard.eKey.wasPressedThisFrame) ||
                       (useMouseClickAsInteract && mouse != null && mouse.leftButton.wasPressedThisFrame);
        if (pressed && _lookedAtDoor != null)
            _lookedAtDoor.TryInteract();
    }

    /// <summary>
    /// Bodycam-lite: резиновое догоняние взгляда, микро-шум рук, дыхание в
    /// покое, восьмёрка-боб от шагов, крен на стрейфах и скорости, клевок при
    /// ускорении, нырок при приземлении и лёгкий FOV-буст на бегу.
    /// Все ручки лежат в ProceduralFPSVisualSettings (секция «КАМЕРА / ЖИВОСТЬ»).
    /// </summary>
    void UpdateCameraMotion()
    {
        if (playerCamera == null) return;
        float dt = Mathf.Max(0.0001f, Time.deltaTime);
        var s = visualSettings;

        // --- параметры (инспектор настроек или запасные значения) ---
        float lookLagSpeed = s != null ? s.lookLagSpeed : 13f;
        float handheldYaw = s != null ? s.handheldYawNoise : 0.45f;
        float handheldPitch = s != null ? s.handheldPitchNoise : 0.3f;
        float handheldSpeed = s != null ? s.handheldNoiseSpeed : 1.1f;
        float idleSway = s != null ? s.idleSway : 0.007f;
        float idleSwaySpeed = s != null ? s.idleSwaySpeed : 0.8f;
        float bobStrength = s != null ? s.bobStrength : (walkShake + runShake) * 0.5f;
        float strideWalk = s != null ? s.strideWalk : 0.85f;
        float strideRun = s != null ? s.strideRun : 1.3f;
        float bobSideSwing = s != null ? s.bobSideSwing : 0.55f;
        float speedRollScale = s != null ? s.speedRollScale : 0.22f;
        float strafeRollScale = s != null ? s.strafeRollScale : 0.4f;
        float accelPitchScale = s != null ? s.accelPitchScale : 0.55f;
        float accelPitchMax = s != null ? s.accelPitchMax : 1.3f;
        float landingDipScale = s != null ? s.landingDipScale : 0.03f;
        float landingDipMax = s != null ? s.landingDipMax : 0.09f;
        float landingRecover = s != null ? s.landingRecoverSpeed : 4.5f;
        float sprintFovBoost = s != null ? s.sprintFovBoost : 4f;
        float fovKickIn = s != null ? s.fovKickInSpeed : 14f;
        float fovKickOut = s != null ? s.fovKickOutSpeed : 6f;
        float bobRunBoost = s != null ? s.bobRunBoost : 1.4f;
        float lookTiltScale = s != null ? s.lookTiltScale : 0.08f;
        float lookTiltMax = s != null ? s.lookTiltMax : 1.5f;

        // --- фактор движения (плавный 0..1) ---
        bool stepping = _lastPlanarVelocity.sqrMagnitude > 0.01f && (_cursorLocked || IsInventoryOpen());
        _moveFactor = Mathf.MoveTowards(_moveFactor, stepping ? 1f : 0f, dt * 5f);
        float speed = _lastPlanarVelocity.magnitude;
        float speedNorm = Mathf.Clamp01(speed / Mathf.Max(0.2f, walkSpeed));
        float bobAmp = _moveFactor * (0.55f + 0.45f * speedNorm) * (_isRunning ? bobRunBoost : 1f);

        // --- фаза шагов привязана к ФАКТИЧЕСКОЙ скорости: темп = скорость / длина шага.
        // Быстрее двигаешься — чаще шаги (и звук, и боб всегда в такт движению).
        _runBlend = Mathf.MoveTowards(_runBlend, _isRunning && stepping ? 1f : 0f, dt * 3f);
        float stride = Mathf.Max(0.4f, Mathf.Lerp(strideWalk, strideRun, _runBlend));
        float stepRate = speed / stride;          // шагов в секунду
        _cameraPhase += dt * stepRate * Mathf.PI; // π фазы = один шаг

        // Шаг строго в момент «толчка» боба (низшая точка вертикали:
        // sin(2φ) минимален при φ ≡ 3π/4 + πk) — звук и картинка синхронны.
        int plantIndex = Mathf.FloorToInt((_cameraPhase - 0.75f * Mathf.PI) / Mathf.PI);
        if (stepping && _moveFactor > 0.5f)
        {
            if (plantIndex > _plantIndex)
            {
                _plantIndex = plantIndex;
                PlayFootstep();
            }
        }
        else if (plantIndex > _plantIndex)
        {
            _plantIndex = plantIndex; // на месте догоняем индекс без звука
        }

        float bobUp = Mathf.Sin(_cameraPhase * 2f) * bobStrength * bobAmp;
        float bobSide = Mathf.Cos(_cameraPhase) * bobStrength * bobSideSwing * bobAmp;

        // --- дыхание/дрожь в покое (живость без движения) ---
        float idle = 1f - _moveFactor * 0.75f;
        float t = Time.time;
        float swayX = (Mathf.PerlinNoise(t * idleSwaySpeed, 0.37f) - 0.5f) * 2f * idleSway * idle;
        float swayY = (Mathf.PerlinNoise(3.1f, t * idleSwaySpeed) - 0.5f) * 2f * idleSway * idle;

        // --- приземление: нырок вниз и мягкий подъём обратно ---
        bool grounded = _controller != null && _controller.isGrounded;
        if (!grounded) _fallSpeed = Mathf.Max(0f, -_verticalVelocity);
        if (grounded && !_wasGrounded && _fallSpeed > 2.5f && landingDipScale > 0f)
            _dipOffset -= Mathf.Min(_fallSpeed * landingDipScale, landingDipMax);
        if (grounded) _fallSpeed = 0f;
        _wasGrounded = grounded;
        _dipOffset = Mathf.Lerp(_dipOffset, 0f, 1f - Mathf.Exp(-landingRecover * dt));

        // --- клевок при ускорении/торможении ---
        float accel = (speed - _prevSpeed) / dt;
        _prevSpeed = speed;
        float accelTarget = Mathf.Clamp(accel * accelPitchScale, -accelPitchMax, accelPitchMax);
        _accelPitchSmooth = Mathf.Lerp(_accelPitchSmooth, accelTarget, 1f - Mathf.Exp(-9f * dt));

        // --- крены: скорость + стрейф (градусы на м/с) + тилт в сторону поворота ---
        Vector3 localVel = transform.InverseTransformDirection(_lastPlanarVelocity);
        _yawRate = Mathf.Lerp(_yawRate, 0f, 1f - Mathf.Exp(-10f * dt));
        _yawRateSmooth = Mathf.Lerp(_yawRateSmooth, _yawRate, 1f - Mathf.Exp(-8f * dt));
        float lookTilt = Mathf.Clamp(_yawRateSmooth * lookTiltScale, -lookTiltMax, lookTiltMax);
        float roll = _moveFactor * (speed * speedRollScale) + (-localVel.x * strafeRollScale) + lookTilt;

        // --- шум рук (повороты камеры) ---
        float nt = t * handheldSpeed;
        float noiseMul = 0.4f + 0.6f * _moveFactor;
        float yawNoise = (Mathf.PerlinNoise(nt, 11.3f) - 0.5f) * 2f * handheldYaw * noiseMul;
        float pitchNoise = (Mathf.PerlinNoise(7.7f, nt) - 0.5f) * 2f * handheldPitch * noiseMul;

        // --- резиновое догоняние взгляда ---
        float lagK = 1f - Mathf.Exp(-lookLagSpeed * dt);
        _yawLagOffset = Mathf.Lerp(_yawLagOffset, 0f, lagK);
        _pitchSmoothed = Mathf.LerpAngle(_pitchSmoothed, _pitch, lagK);

        // --- итоговая позиция и ориентация камеры ---
        playerCamera.transform.localPosition = _cameraBaseLocalPosition
            + new Vector3(bobSide + swayX, bobUp + swayY + _dipOffset, 0f);
        playerCamera.transform.localRotation = _cameraBaseLocalRotation
            * Quaternion.Euler(_pitchSmoothed + _accelPitchSmooth + pitchNoise, _yawLagOffset + yawNoise, roll);

        // --- FOV-кик на бегу: быстрый пинок на старте, мягкий возврат после ---
        float baseFov = s != null ? s.cameraFieldOfView : _baseFov;
        float targetFov = baseFov + (stepping && _isRunning ? sprintFovBoost : 0f);
        float fovSpeed = targetFov > playerCamera.fieldOfView ? fovKickIn : fovKickOut;
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov, 1f - Mathf.Exp(-fovSpeed * dt));
    }

    void UpdateFlashlight()
    {
        if (visualSettings != null) return;
        if (flashlight == null || playerCamera == null) return;
        flashlight.transform.position = playerCamera.transform.position;
        flashlight.transform.rotation = playerCamera.transform.rotation;
    }

    void OnGUI()
    {
        if (!_cursorLocked) return;
        const float size = 8f;
        GUI.color = new Color(0.9f, 0.88f, 0.78f, 0.9f);
        GUI.Label(new Rect(Screen.width * 0.5f - size * 0.5f, Screen.height * 0.5f - size * 0.5f, size, size), "+");
        GUIStyle lightStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 12,
            fontStyle = FontStyle.Bold
        };
        lightStyle.normal.textColor = flashlight != null && flashlight.enabled
            ? new Color(0.92f, 0.84f, 0.58f) : new Color(0.55f, 0.52f, 0.48f);
        GUI.Label(new Rect(18f, 16f, 230f, 24f),
            "F — ФОНАРЬ: " + (flashlight != null && flashlight.enabled ? "ВКЛ" : "ВЫКЛ"), lightStyle);

        if (_lookedAtDoor == null) return;
        string message = _lookedAtDoor.IsLocked ? "ЗАПЕРТО — ОБРАТНОГО ПУТИ НЕТ" : "E / ЛКМ — ОТКРЫТЬ ДВЕРЬ";
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = _lookedAtDoor.IsLocked ? new Color(0.75f, 0.35f, 0.25f) : new Color(0.92f, 0.88f, 0.74f);
        GUI.Label(new Rect(0f, Screen.height * 0.5f + 26f, Screen.width, 28f), message, style);
    }

    void OnDisable()
    {
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
