using UnityEngine;

/// <summary>
/// Custom cursor, fully procedural (no texture assets needed).
/// Draws a classic arrow pointer with a dark outline; tints amber when
/// hovering a car part. Set `useCustomCursor = false` to keep the OS cursor.
/// If you have your own PNG cursor, assign it in `customTexture`.
/// </summary>
[DisallowMultipleComponent]
public class CustomCursor : MonoBehaviour
{
    [Header("Mode")]
    [Tooltip("Off = normal OS cursor, nothing is replaced.")]
    public bool useCustomCursor = true;
    [Tooltip("Assign your own texture here to skip the procedural cursor.")]
    public Texture2D customTexture;

    [Header("Look")]
    public int size = 28;
    public Color fillColor = new Color(0.95f, 0.95f, 0.95f);
    public Color outlineColor = new Color(0.05f, 0.05f, 0.07f);
    public Color hoverTint = new Color(1f, 0.72f, 0.2f);

    Texture2D _normal, _hovered;
    bool _isHover;

    void Awake()
    {
        if (!useCustomCursor) return;

        if (customTexture != null)
        {
            _normal = customTexture;
            _hovered = customTexture;
        }
        else
        {
            _normal = DrawArrow(fillColor, outlineColor);
            _hovered = DrawArrow(hoverTint, outlineColor);
        }
        // hotspot at the arrow tip (top-left)
        Cursor.SetCursor(_normal, new Vector2(1, 1), CursorMode.ForceSoftware);
        CarPartClickable.HoverChanged += OnHover;
    }

    void OnDestroy()
    {
        CarPartClickable.HoverChanged -= OnHover;
        Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
    }

    void OnHover(bool hovered)
    {
        if (!useCustomCursor || _isHover == hovered) return;
        _isHover = hovered;
        Cursor.SetCursor(hovered ? _hovered : _normal, new Vector2(1, 1), CursorMode.ForceSoftware);
    }

    // classic Windows-style arrow polygon in a 0..1 space
    static readonly Vector2[] ArrowShape =
    {
        new Vector2(0.00f, 0.00f),
        new Vector2(0.00f, 0.78f),
        new Vector2(0.21f, 0.60f),
        new Vector2(0.34f, 0.93f),
        new Vector2(0.47f, 0.87f),
        new Vector2(0.34f, 0.56f),
        new Vector2(0.62f, 0.56f),
    };

    Texture2D DrawArrow(Color fill, Color outline)
    {
        var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Unity texture Y goes bottom-up; cursor hotpoint is top-left,
                // so flip Y to keep the arrow pointing up-left
                float u = x / (float)(size - 1);
                float v = 1f - y / (float)(size - 1);
                var p = new Vector2(u, v);

                bool inside = PointInPolygon(p, ArrowShape);
                bool nearEdge = inside && !PointInPolygon(Shrink(p, 0.06f), ArrowShape);

                Color col = Color.clear;
                if (nearEdge) col = outline;
                else if (inside) col = fill;
                tex.SetPixel(x, y, col);
            }
        }
        tex.Apply();
        return tex;
    }

    static Vector2 Shrink(Vector2 p, float amount)
    {
        var c = new Vector2(0.2f, 0.2f);
        return c + (p - c) * (1f - amount);
    }

    static bool PointInPolygon(Vector2 p, Vector2[] poly)
    {
        bool inside = false;
        int j = poly.Length - 1;
        for (int i = 0; i < poly.Length; j = i++)
        {
            if (((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                inside = !inside;
        }
        return inside;
    }
}
