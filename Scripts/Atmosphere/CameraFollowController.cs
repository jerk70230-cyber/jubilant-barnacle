using UnityEngine;

/// <summary>
/// Project Zomboid-style camera: fixed iso angle and zoom (the current camera
/// offset is captured once), the position smoothly follows the player.
/// Attach to the camera - the bootstrap does it automatically.
/// </summary>
[DisallowMultipleComponent]
public class CameraFollowController : MonoBehaviour
{
    [Tooltip("Follow target. Empty = player character, else the hero car.")]
    public Transform target;
    [Tooltip("How lazily the camera catches up (seconds). Smaller = snappier.")]
    public float smoothTime = 0.1f;
    [Tooltip("Keep the camera height fixed (classic iso).")]
    public bool lockHeight = true;

    Vector3 _offset;
    Vector3 _vel;
    bool _offsetCaptured;

    void LateUpdate()
    {
        if (target == null) ResolveTarget();
        if (target == null) return;

        if (!_offsetCaptured)
        {
            // keep the camera's angle, but aim it so the character sits dead-center:
            // offset goes straight back along the view ray at a sane distance
            float dist = Mathf.Clamp(Vector3.Distance(transform.position, target.position), 6f, 14f);
            _offset = -transform.forward * dist;
            _offsetCaptured = true;
        }

        Vector3 desired = target.position + _offset;
        if (lockHeight) desired.y = transform.position.y;
        transform.position = Vector3.SmoothDamp(transform.position, desired, ref _vel, smoothTime);
    }

    void ResolveTarget()
    {
        var pc = PlayerCharacterController.Instance;
        if (pc != null && pc.character != null) { target = pc.character; return; }
        var fog = FogOfWarController.Instance;
        if (fog != null && fog.car != null) target = fog.car;
    }
}
