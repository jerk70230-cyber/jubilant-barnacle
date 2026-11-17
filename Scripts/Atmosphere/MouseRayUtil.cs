using UnityEngine;

/// <summary>
/// Cursor -> ray helper that works with the new Input System and with any
/// camera setup, by converting the mouse position to viewport coordinates.
/// </summary>
public static class MouseRayUtil
{
    public static Ray CursorRay(Camera cam)
    {
        Vector2 m = UnityEngine.InputSystem.Mouse.current != null
            ? UnityEngine.InputSystem.Mouse.current.position.ReadValue()
            : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        return cam.ViewportPointToRay(new Vector3(
            m.x / Screen.width,
            m.y / Screen.height,
            0f));
    }

    public static Vector2 CursorViewport()
    {
        Vector2 m = UnityEngine.InputSystem.Mouse.current != null
            ? UnityEngine.InputSystem.Mouse.current.position.ReadValue()
            : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        return new Vector2(m.x / Screen.width, m.y / Screen.height);
    }
}
