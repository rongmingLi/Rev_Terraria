using System;
using System.Collections.Generic;
using TerrariaMod.ID;
using UnityEngine;

namespace TerrariaMod.World
{
    /// <summary>
    /// Pooled 32x32 chunk renderer separated into explicit sorting layers and renderQueues:
    /// Backdrop (-20 / 2980), Walls (-10 / 2990), Tiles (0 / 3000), Liquids (1 / 3005), Canopy (2 / 3010).
    /// Eliminates Unity Transparent-queue submesh/cross-chunk depth sorting flips where walls could paint over tiles.
    /// </summary>
    public class TerrariaChunkRenderer : MonoBehaviour
    {
        private sealed class ChunkLayer
        {
            public readonly string LayerName;
            public readonly int SortingOrder;
            public readonly int RenderQueue;

            private GameObject _go;
            private MeshFilter _filter;
            private MeshRenderer _renderer;
            private Mesh _mesh;

            private readonly List<Vector3> _verts = new List<Vector3>(2048);
            private readonly List<Vector2> _uvs = new List<Vector2>(2048);
            private readonly List<Color32> _colors = new List<Color32>(2048);
            private readonly Dictionary<Texture2D, List<int>> _submeshTris = new Dictionary<Texture2D, List<int>>(12);
            private readonly List<Texture2D> _orderedTextures = new List<Texture2D>(12);
            private readonly List<Material> _submeshMaterials = new List<Material>(12);

            public ChunkLayer(string layerName, int sortingOrder, int renderQueue)
            {
                LayerName = layerName;
                SortingOrder = sortingOrder;
                RenderQueue = renderQueue;
            }

            public void EnsureInitialized(Transform parent)
            {
                if (_go != null) return;
                _go = new GameObject(LayerName);
                _go.transform.SetParent(parent, false);
                _filter = _go.AddComponent<MeshFilter>();
                _renderer = _go.AddComponent<MeshRenderer>();
                _renderer.sortingOrder = SortingOrder;
                _mesh = new Mesh { name = $"Chunk_{LayerName}" };
                _mesh.MarkDynamic();
                _filter.sharedMesh = _mesh;
                _go.SetActive(false);
            }

            public void ClearBuffers()
            {
                _verts.Clear();
                _uvs.Clear();
                _colors.Clear();
                foreach (var list in _submeshTris.Values)
                    list.Clear();
                _orderedTextures.Clear();
            }

            public void Recycle()
            {
                ClearBuffers();
                if (_mesh != null)
                    _mesh.Clear();
                if (_renderer != null)
                    _renderer.sharedMaterials = Array.Empty<Material>();
                if (_go != null)
                    _go.SetActive(false);
            }

            public void AddQuad(
                Texture2D tex,
                float wx, float wy, float ww, float wh, float wz,
                float px, float pyFromTop, float pw, float ph,
                Color32 col)
            {
                AddQuad(tex, wx, wy, ww, wh, wz, px, pyFromTop, pw, ph, col, col, col, col);
            }

            public void AddQuad(
                Texture2D tex,
                float wx, float wy, float ww, float wh, float wz,
                float px, float pyFromTop, float pw, float ph,
                Color32 cBL, Color32 cTL, Color32 cTR, Color32 cBR)
            {
                var tris = GetTriList(tex);
                int baseVert = _verts.Count;

                _verts.Add(new Vector3(wx, wy, wz));
                _verts.Add(new Vector3(wx, wy + wh, wz));
                _verts.Add(new Vector3(wx + ww, wy + wh, wz));
                _verts.Add(new Vector3(wx + ww, wy, wz));

                const float eps = 0.15f;
                float tw = tex.width;
                float th = tex.height;
                float u0 = Mathf.Clamp01((px + eps) / tw);
                float u1 = Mathf.Clamp01((px + pw - eps) / tw);
                float vTop = Mathf.Clamp01(1f - (pyFromTop + eps) / th);
                float vBot = Mathf.Clamp01(1f - (pyFromTop + ph - eps) / th);

                _uvs.Add(new Vector2(u0, vBot));
                _uvs.Add(new Vector2(u0, vTop));
                _uvs.Add(new Vector2(u1, vTop));
                _uvs.Add(new Vector2(u1, vBot));

                _colors.Add(cBL);
                _colors.Add(cTL);
                _colors.Add(cTR);
                _colors.Add(cBR);

                tris.Add(baseVert);
                tris.Add(baseVert + 1);
                tris.Add(baseVert + 2);
                tris.Add(baseVert);
                tris.Add(baseVert + 2);
                tris.Add(baseVert + 3);
            }

            public void AddSolidQuad(Texture2D tex, float wx, float wy, float ww, float wh, float wz, Color32 col)
            {
                var tris = GetTriList(tex);
                int baseVert = _verts.Count;

                _verts.Add(new Vector3(wx, wy, wz));
                _verts.Add(new Vector3(wx, wy + wh, wz));
                _verts.Add(new Vector3(wx + ww, wy + wh, wz));
                _verts.Add(new Vector3(wx + ww, wy, wz));

                _uvs.Add(new Vector2(0f, 0f));
                _uvs.Add(new Vector2(0f, 1f));
                _uvs.Add(new Vector2(1f, 1f));
                _uvs.Add(new Vector2(1f, 0f));

                _colors.Add(col);
                _colors.Add(col);
                _colors.Add(col);
                _colors.Add(col);

                tris.Add(baseVert);
                tris.Add(baseVert + 1);
                tris.Add(baseVert + 2);
                tris.Add(baseVert);
                tris.Add(baseVert + 2);
                tris.Add(baseVert + 3);
            }

            public bool Commit(Func<Texture2D, int, Material> getMaterial)
            {
                _mesh.Clear();
                if (_verts.Count == 0 || _orderedTextures.Count == 0)
                {
                    _renderer.sharedMaterials = Array.Empty<Material>();
                    _go.SetActive(false);
                    return false;
                }

                _mesh.SetVertices(_verts);
                _mesh.SetUVs(0, _uvs);
                _mesh.SetColors(_colors);
                _mesh.subMeshCount = _orderedTextures.Count;

                _submeshMaterials.Clear();
                for (int i = 0; i < _orderedTextures.Count; i++)
                {
                    Texture2D tex = _orderedTextures[i];
                    _mesh.SetTriangles(_submeshTris[tex], i, calculateBounds: false);
                    _submeshMaterials.Add(getMaterial(tex, RenderQueue));
                }
                _mesh.RecalculateBounds();
                _renderer.sortingOrder = SortingOrder;
                _renderer.sharedMaterials = _submeshMaterials.ToArray();
                _go.SetActive(true);
                return true;
            }

            private List<int> GetTriList(Texture2D tex)
            {
                if (!_submeshTris.TryGetValue(tex, out var tris))
                {
                    tris = new List<int>(256);
                    _submeshTris[tex] = tris;
                }
                if (tris.Count == 0)
                    _orderedTextures.Add(tex);
                return tris;
            }
        }

        private ChunkLayer _backdropLayer;
        private ChunkLayer _wallLayer;
        private ChunkLayer _tileLayer;
        private ChunkLayer _liquidLayer;
        private ChunkLayer _canopyLayer;
        private bool _initialized;

        public int ChunkX { get; private set; }
        public int ChunkY { get; private set; }
        public bool IsDirty { get; set; }

        public void Initialize()
        {
            if (!_initialized)
            {
                _backdropLayer = new ChunkLayer("Backdrop", sortingOrder: -20, renderQueue: 2980);
                _wallLayer = new ChunkLayer("Walls", sortingOrder: -10, renderQueue: 2990);
                _liquidLayer = new ChunkLayer("Liquids", sortingOrder: -2, renderQueue: 2995);
                _tileLayer = new ChunkLayer("Tiles", sortingOrder: 0, renderQueue: 3000);
                _canopyLayer = new ChunkLayer("Canopy", sortingOrder: 2, renderQueue: 3010);
                _initialized = true;
            }

            _backdropLayer.EnsureInitialized(transform);
            _wallLayer.EnsureInitialized(transform);
            _tileLayer.EnsureInitialized(transform);
            _liquidLayer.EnsureInitialized(transform);
            _canopyLayer.EnsureInitialized(transform);
        }

        public void Assign(int cx, int cy)
        {
            Initialize();
            ChunkX = cx;
            ChunkY = cy;
            IsDirty = true;
            name = $"Chunk_{cx}_{cy}";
            gameObject.SetActive(false);
        }

        public void Recycle()
        {
            IsDirty = false;
            if (_initialized)
            {
                _backdropLayer.Recycle();
                _wallLayer.Recycle();
                _tileLayer.Recycle();
                _liquidLayer.Recycle();
                _canopyLayer.Recycle();
            }
            gameObject.SetActive(false);
        }

        public void Rebuild(
            WorldMap world,
            Func<int, Texture2D> getTileTex,
            Func<int, Texture2D> getWallTex,
            Func<string, Texture2D> getSpecialTex,
            Func<Texture2D, int, Material> getMaterial,
            WorldLighting lighting = null)
        {
            Initialize();
            IsDirty = false;

            _backdropLayer.ClearBuffers();
            _wallLayer.ClearBuffers();
            _tileLayer.ClearBuffers();
            _liquidLayer.ClearBuffers();
            _canopyLayer.ClearBuffers();

            int startX = ChunkX * WorldMap.ChunkSize;
            int endX = Mathf.Min(world.Width, startX + WorldMap.ChunkSize);
            int startY = ChunkY * WorldMap.ChunkSize;
            int endY = Mathf.Min(world.Height, startY + WorldMap.ChunkSize);

            // Pass 1: Background walls (strictly behind Tiles across all chunks)
            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    ushort wType = world.GetWallType(x, y);
                    if (wType == 0) continue;

                    Texture2D tex = getWallTex(wType);
                    if (tex == null) continue;

                    TileFraming.EnsureWallFrame(world, x, y, out short fx, out short fy);
                    if (lighting != null)
                    {
                        GetCornerLights(lighting, x, y, 0.72f, out Color32 cBL, out Color32 cTL, out Color32 cTR, out Color32 cBR);
                        _wallLayer.AddQuad(tex, x - 0.5f, -y - 1.5f, 2f, 2f, 0.5f, fx, fy, 32f, 32f, cBL, cTL, cTR, cBR);
                    }
                    else
                    {
                        Color32 light = ComputeTileLight(world, x, y, isWall: true);
                        _wallLayer.AddQuad(tex, x - 0.5f, -y - 1.5f, 2f, 2f, 0.5f, fx, fy, 32f, 32f, light);
                    }
                }
            }

            // Pass 2 & 3: Foreground tiles, tree canopies, and liquids
            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    int idx = world.Index(x, y);

                    if ((world.Flags[idx] & 1) != 0)
                    {
                        ushort tType = world.TileType[idx];
                        Texture2D tex = getTileTex(tType);
                        if (tex != null)
                        {
                            if (!world.IsImportantTile(tType) || tType == TileID.Platforms || tType == TileID.Torches)
                                TileFraming.FrameTile(world, x, y);

                            short fx = world.FrameX[idx];
                            short fy = world.FrameY[idx];
                            if (fx < 0 || fy < 0 || fx >= tex.width || fy >= tex.height)
                            {
                                fx = 0;
                                fy = 0;
                            }

                            Color32 cBL, cTL, cTR, cBR;
                            if (tType == TileID.Torches)
                            {
                                cBL = cTL = cTR = cBR = new Color32(255, 240, 200, 255);
                            }
                            else if (lighting != null)
                            {
                                GetCornerLights(lighting, x, y, 1f, out cBL, out cTL, out cTR, out cBR);
                            }
                            else
                            {
                                cBL = cTL = cTR = cBR = ComputeTileLight(world, x, y, isWall: false);
                            }

                            if (tType == TileID.Trees)
                            {
                                if (fx == 22 && fy >= 198 && getSpecialTex != null)
                                {
                                    Texture2D topsTex = getSpecialTex("Tree_Tops_0");
                                    if (topsTex != null)
                                    {
                                        int variant = Mathf.Clamp((fy - 198) / 22, 0, Mathf.Max(0, topsTex.width / 82 - 1));
                                        _canopyLayer.AddQuad(topsTex, x - 2f, -y - 1f, 5f, 5f, -0.02f, variant * 82f, 0f, 80f, 80f, cBL, cTL, cTR, cBR);
                                    }
                                }
                                else if ((fx == 44 || fx == 66) && fy >= 198 && getSpecialTex != null)
                                {
                                    Texture2D brTex = getSpecialTex("Tree_Branches_0");
                                    if (brTex != null)
                                    {
                                        int row = Mathf.Clamp((fy - 198) / 22, 0, Mathf.Max(0, brTex.height / 42 - 1));
                                        float bx = fx == 44 ? x - 1.5f : x;
                                        float uX = fx == 44 ? 0f : 42f;
                                        _canopyLayer.AddQuad(brTex, bx, -y - 1.75f, 2.5f, 2.5f, -0.01f, uX, row * 42f, 40f, 40f, cBL, cTL, cTR, cBR);
                                    }
                                }
                                else
                                {
                                    _tileLayer.AddQuad(tex, x - 2f / 16f, -y - 1f - 2f / 16f, 20f / 16f, 20f / 16f, 0f, fx, fy, 20f, 20f, cBL, cTL, cTR, cBR);
                                }
                            }
                            else if (tType == TileID.Torches)
                            {
                                _tileLayer.AddQuad(tex, x - 2f / 16f, -y - 1f - 2f / 16f, 20f / 16f, 20f / 16f, 0f, fx, fy, 20f, 20f, cBL, cTL, cTR, cBR);
                            }
                            else if (tType == TileID.Plants || tType == TileID.CorruptPlants ||
                                     tType == TileID.JunglePlants || tType == TileID.MushroomPlants || tType == TileID.Plants2)
                            {
                                float ch = Mathf.Min(20f, tex.height);
                                _tileLayer.AddQuad(tex, x, -y - 1f, 1f, ch / 16f, 0f, fx, fy, 16f, ch, cBL, cTL, cTR, cBR);
                            }
                            else
                            {
                                _tileLayer.AddQuad(tex, x, -y - 1f, 1f, 1f, 0f, fx, fy, 16f, 16f, cBL, cTL, cTR, cBR);
                            }
                        }
                    }

                    byte liq = world.Liquid[idx];
                    if (liq > 0)
                    {
                        byte lType = world.LiquidType[idx];
                        bool hasAbove = world.InBounds(x, y - 1) && world.Liquid[world.Index(x, y - 1)] > 0;
                        float fill = hasAbove ? 1f : Mathf.Clamp01(liq / 255f);

                        // Extend 2px (2/16) ONLY into adjacent solid tiles (never overlap neighboring liquid tiles!)
                        const float pad = 2f / 16f;
                        float padL = TileRules.IsSolid(world, x - 1, y) ? pad : 0f;
                        float padR = TileRules.IsSolid(world, x + 1, y) ? pad : 0f;
                        float padD = TileRules.IsSolid(world, x, y + 1) ? pad : 0f;
                        float padU = (!hasAbove && fill >= 0.98f && TileRules.IsSolid(world, x, y - 1)) ? pad : 0f;

                        float wx = x - padL;
                        float wy = -y - 1f - padD;
                        float ww = 1f + padL + padR;
                        float wh = fill + padD + padU;

                        Texture2D liqTex = GetLiquidTexture(lType, getSpecialTex);
                        Color32 lBL, lTL, lTR, lBR;
                        GetLiquidCornerColors(lType, lighting, x, y, out lBL, out lTL, out lTR, out lBR);
                        if (liqTex != null)
                        {
                            float uW = lType == 0 ? 16f : liqTex.width;
                            // Submerged tiles sample uniform body band (rows 10..14) so water/honey/lava looks seamless
                            float py = hasAbove ? 10f : (1f - fill) * 6f;
                            float ph = hasAbove ? 4f : (16f - py);
                            _liquidLayer.AddQuad(liqTex, wx, wy, ww, wh, 0.1f, 0f, py, uW, ph, lBL, lTL, lTR, lBR);
                        }
                        else
                        {
                            _liquidLayer.AddSolidQuad(Texture2D.whiteTexture, wx, wy, ww, wh, 0.1f, lBL);
                        }
                    }
                }
            }

            bool any = false;
            any |= _backdropLayer.Commit(getMaterial);
            any |= _wallLayer.Commit(getMaterial);
            any |= _tileLayer.Commit(getMaterial);
            any |= _liquidLayer.Commit(getMaterial);
            any |= _canopyLayer.Commit(getMaterial);

            gameObject.SetActive(any);
        }

        private static Texture2D GetLiquidTexture(byte liquidType, Func<string, Texture2D> getSpecialTex)
        {
            if (getSpecialTex == null) return null;
            switch (liquidType)
            {
                case 1: return getSpecialTex("Liquid_1");
                case 2: return getSpecialTex("Liquid_11");
                case 3: return getSpecialTex("Liquid_14");
                default: return getSpecialTex("Liquid_0");
            }
        }

        private static void GetLiquidCornerColors(
            byte liquidType, WorldLighting lighting, int x, int y,
            out Color32 cBL, out Color32 cTL, out Color32 cTR, out Color32 cBR)
        {
            if (liquidType == 1)
            {
                cBL = cTL = cTR = cBR = new Color32(255, 210, 175, 235);
                return;
            }
            if (liquidType == 3)
            {
                cBL = cTL = cTR = cBR = new Color32(230, 185, 255, 210);
                return;
            }

            if (lighting == null)
            {
                byte a = liquidType == 2 ? (byte)220 : (byte)175;
                cBL = cTL = cTR = cBR = new Color32(255, 255, 255, a);
                return;
            }

            GetCornerLights(lighting, x, y, 1f, out cBL, out cTL, out cTR, out cBR);
            if (liquidType == 2)
            {
                cBL = TintLiquid(cBL, 0.98f, 0.90f, 0.72f, 220);
                cTL = TintLiquid(cTL, 0.98f, 0.90f, 0.72f, 220);
                cTR = TintLiquid(cTR, 0.98f, 0.90f, 0.72f, 220);
                cBR = TintLiquid(cBR, 0.98f, 0.90f, 0.72f, 220);
            }
            else
            {
                cBL = TintLiquid(cBL, 0.85f, 0.92f, 1.0f, 175);
                cTL = TintLiquid(cTL, 0.85f, 0.92f, 1.0f, 175);
                cTR = TintLiquid(cTR, 0.85f, 0.92f, 1.0f, 175);
                cBR = TintLiquid(cBR, 0.85f, 0.92f, 1.0f, 175);
            }
        }

        private static Color32 TintLiquid(Color32 c, float r, float g, float b, byte a)
        {
            return new Color32((byte)(c.r * r), (byte)(c.g * g), (byte)(c.b * b), a);
        }

        private static Color32 GetLiquidRimColor(byte liquidType, Color32 bodyCol)
        {
            switch (liquidType)
            {
                case 1: return new Color32(255, 235, 130, 240);
                case 2: return new Color32(255, 230, 110, 235);
                case 3: return new Color32(245, 210, 255, 230);
                default:
                    return new Color32(
                        (byte)Mathf.Min(255, bodyCol.r + 70),
                        (byte)Mathf.Min(255, bodyCol.g + 90),
                        (byte)Mathf.Min(255, bodyCol.b + 110),
                        205);
            }
        }

        private static void GetCornerLights(
            WorldLighting lighting, int x, int y, float scale,
            out Color32 cBL, out Color32 cTL, out Color32 cTR, out Color32 cBR)
        {
            Color32 c = lighting.GetLight(x, y);
            Color32 l = lighting.GetLight(x - 1, y);
            Color32 r = lighting.GetLight(x + 1, y);
            Color32 u = lighting.GetLight(x, y - 1);
            Color32 d = lighting.GetLight(x, y + 1);
            Color32 ul = lighting.GetLight(x - 1, y - 1);
            Color32 ur = lighting.GetLight(x + 1, y - 1);
            Color32 dl = lighting.GetLight(x - 1, y + 1);
            Color32 dr = lighting.GetLight(x + 1, y + 1);

            cBL = Avg4(c, l, d, dl, scale);
            cTL = Avg4(c, l, u, ul, scale);
            cTR = Avg4(c, r, u, ur, scale);
            cBR = Avg4(c, r, d, dr, scale);
        }

        private static Color32 Avg4(Color32 a, Color32 b, Color32 c, Color32 d, float scale)
        {
            float s = scale * 0.25f;
            return new Color32(
                (byte)((a.r + b.r + c.r + d.r) * s),
                (byte)((a.g + b.g + c.g + d.g) * s),
                (byte)((a.b + b.b + c.b + d.b) * s),
                255);
        }

        private static Color32 ComputeTileLight(WorldMap world, int x, int y, bool isWall)
        {
            double surf = world.SurfaceLayer > 10 ? world.SurfaceLayer : world.Height * 0.28;
            float daylight = 1f;
            if (y > surf)
            {
                float depth = (float)((y - surf) / 60.0);
                daylight = Mathf.Clamp(1f - depth * 0.72f, 0.28f, 1f);
            }

            bool airExposed =
                !world.IsActive(x, y - 1) ||
                !world.IsActive(x, y + 1) ||
                !world.IsActive(x - 1, y) ||
                !world.IsActive(x + 1, y);

            if (airExposed && daylight < 0.65f)
                daylight = Mathf.Min(0.75f, daylight + 0.22f);

            float wallAtten = isWall ? 0.72f : 1f;
            float r = Mathf.Clamp01(daylight * wallAtten);
            float g = Mathf.Clamp01(daylight * wallAtten);
            float b = Mathf.Clamp01(Mathf.Lerp(0.32f, 1f, daylight) * wallAtten);

            return new Color32(
                (byte)(r * 255f),
                (byte)(g * 255f),
                (byte)(b * 255f),
                255);
        }
    }
}
