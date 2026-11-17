using System;
using UnityEngine;

/// <summary>
/// Attach to a car part (door, tire, hood, trunk, steering wheel...).
/// Pure interaction marker: click events + hover notification (for the cursor
/// sprite). It does NOT touch materials in any way - an object's visibility
/// and color are governed exclusively by the fog-of-war cone.
/// Requires a collider on the part (setup script adds BoxColliders).
/// </summary>
public class CarPartClickable : MonoBehaviour
{
    public static event Action<bool> HoverChanged;
    public static event Action<CarPartClickable> PartClicked;

    [Tooltip("Name shown in logs/events. Defaults to the GameObject name.")]
    public string partName;

    public string PartName => partName;

    void Awake()
    {
        if (string.IsNullOrEmpty(partName)) partName = gameObject.name;
    }

    public void Click() => PartClicked?.Invoke(this);

    public void SetHover(bool hover) => HoverChanged?.Invoke(hover);
}

/// <summary>
/// One per scene: raycasts the cursor every frame, manages hover/click of
/// CarPartClickable parts. Attach together with FogOfWarController.
/// </summary>
public class CarInteractionManager : MonoBehaviour
{
    public Camera viewCamera;
    public LayerMask interactMask = ~0;

    CarPartClickable _hovered;

    void Update()
    {
        if (viewCamera == null) viewCamera = Camera.main;
        if (viewCamera == null) return;

        CarPartClickable hitPart = null;
        Ray ray = MouseRayUtil.CursorRay(viewCamera);
        if (Physics.Raycast(ray, out var hit, 200f, interactMask))
            hitPart = hit.collider.GetComponentInParent<CarPartClickable>();

        if (_hovered != hitPart)
        {
            _hovered?.SetHover(false);
            _hovered = hitPart;
            _hovered?.SetHover(true);
        }

        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && _hovered != null)
        {
            Debug.Log($"[Car] Clicked part: {_hovered.PartName}");
            _hovered.Click();
        }
    }
}
