using UnityEngine;

[ExecuteAlways]
public class SphereMeshCutter : MonoBehaviour
{
    [Header("Object to cut")]
    public Transform targetRoot;

    [Header("Sphere settings")]
    [Min(0.01f)]
    public float radius = 0.5f;

    public Vector3 centerOffset;

    private Renderer[] targetRenderers;
    private MaterialPropertyBlock propertyBlock;

    private static readonly int ClipSphereID =
        Shader.PropertyToID("_ClipSphere");

    private void OnEnable()
    {
        propertyBlock = new MaterialPropertyBlock();
        RefreshTargets();
    }

    private void OnValidate()
    {
        RefreshTargets();
        UpdateCut();
    }


    private void RefreshTargets()
    {
        if (targetRoot != null)
            targetRenderers = targetRoot.GetComponentsInChildren<Renderer>(true);
    }

    private void LateUpdate()
    {
        UpdateCut();
    }

    private void UpdateCut()
    {
        if (targetRoot == null)
            return;

        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();

        if (targetRenderers == null)
            RefreshTargets();

        Vector3 center = transform.position + centerOffset;

        // Unity Sphere primitive имеет радиус 0.5
        float worldRadius = radius * Mathf.Max(
            Mathf.Abs(transform.lossyScale.x),
            Mathf.Abs(transform.lossyScale.y),
            Mathf.Abs(transform.lossyScale.z)
        );

        Vector4 sphere = new Vector4(
            center.x,
            center.y,
            center.z,
            worldRadius
        );

        foreach (Renderer rend in targetRenderers)
        {
            if (rend == null)
                continue;

            rend.GetPropertyBlock(propertyBlock);
            propertyBlock.SetVector(ClipSphereID, sphere);
            rend.SetPropertyBlock(propertyBlock);
        }
    }
}