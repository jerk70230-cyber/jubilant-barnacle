using UnityEngine;

/// <summary>
/// Disables all renderers under this object ONLY in play mode.
/// Use it on helper/prototype objects (Sphere, Cube, clip volumes) that must
/// exist and work in the scene but must not be visible in the game.
/// In the editor (edit mode) everything stays visible as usual.
/// </summary>
public class HideInGame : MonoBehaviour
{
    void Awake()
    {
        if (!Application.isPlaying) return;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.enabled = false;
    }
}
