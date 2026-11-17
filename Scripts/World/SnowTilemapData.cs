using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Данные сетки земли. Компонент хранит размеры поля и все нарисованные тайлы.
/// Геометрия создаётся редакторским инструментом Snow Tilemap Editor.
/// </summary>
[DisallowMultipleComponent]
public sealed class SnowTilemapData : MonoBehaviour
{
    public enum WallSide
    {
        North, // +Z
        South, // -Z
        East,  // +X
        West   // -X
    }

    [Serializable]
    public sealed class FloorTile
    {
        [Min(0)] public int x;
        [Min(0)] public int z;
        public Texture2D texture;

        public FloorTile(int x, int z, Texture2D texture)
        {
            this.x = x;
            this.z = z;
            this.texture = texture;
        }
    }

    [Serializable]
    public sealed class WallTile
    {
        [Min(0)] public int x;
        [Min(0)] public int z;
        public WallSide side;
        [Min(0)] public int layer;
        public Texture2D texture;

        public WallTile(int x, int z, WallSide side, Texture2D texture, int layer = 0)
        {
            this.x = x;
            this.z = z;
            this.side = side;
            this.layer = Mathf.Max(0, layer);
            this.texture = texture;
        }
    }

    [Header("Размер и положение сетки")]
    [SerializeField, Min(1), Tooltip("Количество клеток по оси X. Измените значение и нажмите «Применить размер».")]
    private int cellsX = 20;

    [SerializeField, Min(1), Tooltip("Количество клеток по оси Z. Измените значение и нажмите «Применить размер».")]
    private int cellsZ = 20;

    [SerializeField, Min(0.01f), Tooltip("Размер одной клетки в мировых единицах Unity. Поле растёт от центра.")]
    private float cellSize = 1f;

    [SerializeField, Tooltip("Высота поверхности пола относительно объекта сетки.")]
    private float floorHeight = 0.02f;

    [SerializeField, Min(0.01f), Tooltip("Высота вертикальных настенных спрайтов.")]
    private float wallHeight = 1f;

    [Header("Столкновения")]
    [SerializeField, Tooltip("Добавлять Mesh Collider к клеткам пола. Обычно включено, чтобы по поверхности можно было ходить.")]
    private bool floorColliders = true;

    [SerializeField, Tooltip("Добавлять тонкий Mesh Collider к плоским стенам. Отключите, если стены нужны только визуально.")]
    private bool wallColliders;

    [Header("Защита помещений от снега и тумана войны")]
    [SerializeField, Tooltip("Автоматически распознавать закрытые помещения: у каждой клетки должен быть пол, а по всему внешнему периметру — стены. Внутри таких помещений снег и туман войны отключаются.")]
    private bool autoDetectShelteredRooms = true;

    [SerializeField, Min(0.5f), Tooltip("Высота защищённой зоны над полом. Обычно равна высоте стен.")]
    private float shelterZoneHeight = 2f;

    [Header("Сохранённые тайлы")]
    [SerializeField, HideInInspector] private List<FloorTile> floorTiles = new List<FloorTile>();
    [SerializeField, HideInInspector] private List<WallTile> wallTiles = new List<WallTile>();
    [SerializeField, HideInInspector] private string generatedAssetsFolder;

    public int CellsX => cellsX;
    public int CellsZ => cellsZ;
    public float CellSize => Mathf.Max(0.01f, cellSize);
    public float FloorHeight => floorHeight;
    [SerializeField] private bool squareWalls = true;
    public bool SquareWalls => squareWalls;
    public void SetSquareWalls(bool value) { squareWalls = value; }
    public float WallHeight => squareWalls ? CellSize : Mathf.Max(0.01f, wallHeight);
    public bool FloorColliders => floorColliders;
    public bool WallColliders => wallColliders;
    public bool AutoDetectShelteredRooms => autoDetectShelteredRooms;
    public float ShelterZoneHeight => Mathf.Max(0.5f, shelterZoneHeight);
    public List<FloorTile> FloorTiles => floorTiles;
    public List<WallTile> WallTiles => wallTiles;
    public string GeneratedAssetsFolder => generatedAssetsFolder;

    // ------------------------------------------------------------------
    // Surface classes: named footstep sound sets bound to floor textures.
    // ------------------------------------------------------------------

    [Serializable]
    public class SurfaceClass
    {
        public string name = "Снег";
        public AudioClip[] footstepClips;
        [Range(0f, 1f)] public float volumeMultiplier = 1f;
    }

    [Serializable]
    public class TextureSurfaceBinding
    {
        public string textureName; // runtime-safe key (texture asset name)
        public int classIndex;
    }

    [SerializeField] private List<SurfaceClass> surfaceClasses = new List<SurfaceClass>();
    [SerializeField] private List<TextureSurfaceBinding> textureSurfaces = new List<TextureSurfaceBinding>();

    public List<SurfaceClass> SurfaceClasses => surfaceClasses ??= new List<SurfaceClass>();
    public List<TextureSurfaceBinding> TextureSurfaces => textureSurfaces ??= new List<TextureSurfaceBinding>();

    /// <summary>Distinct floor textures currently painted on the map.</summary>
    public List<Texture2D> GetUsedFloorTextures()
    {
        var list = new List<Texture2D>();
        foreach (var tile in FloorTiles)
        {
            if (tile == null || tile.texture == null || list.Contains(tile.texture)) continue;
            list.Add(tile.texture);
        }
        return list;
    }

    public void BindTextureToSurface(Texture2D tex, int classIndex)
    {
        if (tex == null) return;
        var list = TextureSurfaces;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null && list[i].textureName == tex.name)
            {
                list[i].classIndex = classIndex;
                return;
            }
        }
        list.Add(new TextureSurfaceBinding { textureName = tex.name, classIndex = classIndex });
    }

    public int GetTextureSurfaceIndex(Texture2D tex)
    {
        if (tex == null) return -1;
        foreach (var b in TextureSurfaces)
            if (b != null && b.textureName == tex.name) return b.classIndex;
        return -1;
    }

    /// <summary>
    /// Finds the painted floor tile directly from the map grid.
    /// This deliberately does not raycast or inspect Renderer/Material: the
    /// SnowGround shader exposes _BaseMap rather than Unity's legacy _MainTex,
    /// and physics can hit unrelated colliders before the actual floor.
    /// </summary>
    public SurfaceClass GetSurfaceAtWorldPosition(Vector3 worldPos)
    {
        EnsureLists();

        float size = CellSize;
        Vector3 local = transform.InverseTransformPoint(worldPos);
        float startX = -cellsX * size * 0.5f;
        float startZ = -cellsZ * size * 0.5f;
        int x = Mathf.FloorToInt((local.x - startX) / size);
        int z = Mathf.FloorToInt((local.z - startZ) / size);

        if (!IsInside(x, z)) return null;

        Texture2D texture = null;
        // Floor tiles are stored as editable records, not in a guaranteed order.
        for (int i = floorTiles.Count - 1; i >= 0; i--)
        {
            FloorTile tile = floorTiles[i];
            if (tile != null && tile.x == x && tile.z == z)
            {
                texture = tile.texture;
                break;
            }
        }

        return texture != null ? FindSurfaceForTexture(texture) : null;
    }

    public SurfaceClass FindSurfaceForTexture(Texture2D tex)
    {
        if (tex == null || surfaceClasses == null || surfaceClasses.Count == 0) return null;
        foreach (var b in TextureSurfaces)
        {
            if (b == null || b.textureName != tex.name) continue;
            if (b.classIndex >= 0 && b.classIndex < surfaceClasses.Count)
                return surfaceClasses[b.classIndex];
        }
        return null;
    }

    public void SetDimensions(int x, int z, float newCellSize, float newFloorHeight, float newWallHeight)
    {
        cellsX = Mathf.Max(1, x);
        cellsZ = Mathf.Max(1, z);
        SetGeometrySettings(newCellSize, newFloorHeight, newWallHeight);
        RemoveOutOfBoundsTiles();
    }

    public void SetGeometrySettings(float newCellSize, float newFloorHeight, float newWallHeight)
    {
        cellSize = Mathf.Max(0.01f, newCellSize);
        floorHeight = newFloorHeight;
        wallHeight = Mathf.Max(0.01f, newWallHeight);
    }

    public void SetColliderOptions(bool floors, bool walls)
    {
        floorColliders = floors;
        wallColliders = walls;
    }

    public void SetGeneratedAssetsFolder(string folder)
    {
        generatedAssetsFolder = folder;
    }

    public void EnsureLists()
    {
        if (floorTiles == null) floorTiles = new List<FloorTile>();
        if (wallTiles == null) wallTiles = new List<WallTile>();
    }

    public Texture2D GetFloor(int x, int z)
    {
        EnsureLists();
        for (int i = floorTiles.Count - 1; i >= 0; i--)
        {
            FloorTile tile = floorTiles[i];
            if (tile != null && tile.x == x && tile.z == z)
                return tile.texture;
        }
        return null;
    }

    public bool SetFloor(int x, int z, Texture2D texture)
    {
        EnsureLists();
        if (!IsInside(x, z)) return false;

        for (int i = floorTiles.Count - 1; i >= 0; i--)
        {
            FloorTile tile = floorTiles[i];
            if (tile != null && tile.x == x && tile.z == z)
            {
                if (tile.texture == texture) return false;
                if (texture == null) floorTiles.RemoveAt(i);
                else tile.texture = texture;
                return true;
            }
        }

        if (texture == null) return false;
        floorTiles.Add(new FloorTile(x, z, texture));
        return true;
    }

    public Texture2D GetWall(int x, int z, WallSide side)
    {
        EnsureLists();
        for (int i = wallTiles.Count - 1; i >= 0; i--)
        {
            WallTile tile = wallTiles[i];
            if (tile != null && tile.x == x && tile.z == z && tile.side == side)
                return tile.texture;
        }
        return null;
    }

    public bool SetWall(int x, int z, WallSide side, Texture2D texture)
    {
        return SetWall(x, z, side, texture, 0);
    }

    // Layer 0 is the bottom segment; higher layers stack vertically above it.
    public bool SetWall(int x, int z, WallSide side, Texture2D texture, int layer)
    {
        EnsureLists();
        if (!IsInside(x, z)) return false;
        layer = Mathf.Max(0, layer);

        for (int i = wallTiles.Count - 1; i >= 0; i--)
        {
            WallTile tile = wallTiles[i];
            if (tile != null && tile.x == x && tile.z == z && tile.side == side && tile.layer == layer)
            {
                if (tile.texture == texture) return false;
                if (texture == null) wallTiles.RemoveAt(i);
                else tile.texture = texture;
                return true;
            }
        }

        if (texture == null) return false;
        wallTiles.Add(new WallTile(x, z, side, texture, layer));
        return true;
    }

    public bool EraseCell(int x, int z)
    {
        EnsureLists();
        bool changed = SetFloor(x, z, null);
        for (int i = wallTiles.Count - 1; i >= 0; i--)
        {
            WallTile tile = wallTiles[i];
            if (tile != null && tile.x == x && tile.z == z)
            {
                wallTiles.RemoveAt(i);
                changed = true;
            }
        }
        return changed;
    }

    public void ResizePreservingTiles(int newX, int newZ, Texture2D[] fillTextures, int seed, bool fillNewCells)
    {
        EnsureLists();
        int oldCellsX = cellsX;
        int oldCellsZ = cellsZ;
        cellsX = Mathf.Max(1, newX);
        cellsZ = Mathf.Max(1, newZ);
        RemoveOutOfBoundsTiles();

        if (!fillNewCells || fillTextures == null || fillTextures.Length == 0)
            return;

        Dictionary<long, Texture2D> existing = new Dictionary<long, Texture2D>();
        for (int i = 0; i < floorTiles.Count; i++)
        {
            FloorTile tile = floorTiles[i];
            if (tile != null && tile.texture != null)
                existing[CellKey(tile.x, tile.z)] = tile.texture;
        }

        System.Random random = new System.Random(seed);
        List<Texture2D> candidates = new List<Texture2D>(fillTextures.Length);
        for (int z = 0; z < cellsZ; z++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                long key = CellKey(x, z);
                // Заполняем только клетки, которые появились за пределами старых размеров.
                // Пустые клетки внутри прежнего поля могли быть намеренно очищены ластиком.
                bool isNewCell = x >= oldCellsX || z >= oldCellsZ;
                if (!isNewCell || existing.ContainsKey(key)) continue;

                existing.TryGetValue(CellKey(x - 1, z), out Texture2D leftTexture);
                existing.TryGetValue(CellKey(x, z - 1), out Texture2D lowerTexture);
                candidates.Clear();
                for (int i = 0; i < fillTextures.Length; i++)
                {
                    Texture2D candidate = fillTextures[i];
                    if (candidate != null && candidate != leftTexture && candidate != lowerTexture)
                        candidates.Add(candidate);
                }

                if (candidates.Count == 0)
                {
                    for (int i = 0; i < fillTextures.Length; i++)
                        if (fillTextures[i] != null) candidates.Add(fillTextures[i]);
                }

                if (candidates.Count > 0)
                {
                    Texture2D selected = candidates[random.Next(candidates.Count)];
                    existing[key] = selected;
                    floorTiles.Add(new FloorTile(x, z, selected));
                }
            }
        }
    }

    public void FillAllRandomly(Texture2D[] textures, int seed)
    {
        EnsureLists();
        floorTiles.Clear();
        if (textures == null || textures.Length == 0) return;

        System.Random random = new System.Random(seed);
        Dictionary<long, Texture2D> assigned = new Dictionary<long, Texture2D>();
        List<Texture2D> candidates = new List<Texture2D>(textures.Length);
        for (int z = 0; z < cellsZ; z++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                assigned.TryGetValue(CellKey(x - 1, z), out Texture2D leftTexture);
                assigned.TryGetValue(CellKey(x, z - 1), out Texture2D lowerTexture);
                candidates.Clear();
                for (int i = 0; i < textures.Length; i++)
                {
                    Texture2D candidate = textures[i];
                    if (candidate != null && candidate != leftTexture && candidate != lowerTexture)
                        candidates.Add(candidate);
                }
                if (candidates.Count == 0)
                    for (int i = 0; i < textures.Length; i++)
                        if (textures[i] != null) candidates.Add(textures[i]);

                if (candidates.Count > 0)
                {
                    Texture2D selected = candidates[random.Next(candidates.Count)];
                    assigned[CellKey(x, z)] = selected;
                    floorTiles.Add(new FloorTile(x, z, selected));
                }
            }
        }
    }

    public void ClearAllTiles()
    {
        EnsureLists();
        floorTiles.Clear();
        wallTiles.Clear();
    }

    public bool IsInside(int x, int z)
    {
        return x >= 0 && z >= 0 && x < cellsX && z < cellsZ;
    }

    private void RemoveOutOfBoundsTiles()
    {
        EnsureLists();
        for (int i = floorTiles.Count - 1; i >= 0; i--)
        {
            FloorTile tile = floorTiles[i];
            if (tile == null || tile.x < 0 || tile.z < 0 || tile.x >= cellsX || tile.z >= cellsZ)
                floorTiles.RemoveAt(i);
        }

        for (int i = wallTiles.Count - 1; i >= 0; i--)
        {
            WallTile tile = wallTiles[i];
            if (tile == null || tile.x < 0 || tile.z < 0 || tile.x >= cellsX || tile.z >= cellsZ)
                wallTiles.RemoveAt(i);
        }
    }

    private static long CellKey(int x, int z)
    {
        return ((long)z << 32) | (uint)x;
    }
}
