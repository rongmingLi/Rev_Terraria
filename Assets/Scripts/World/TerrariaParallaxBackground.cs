using System;
using System.Collections.Generic;
using TerrariaMod.Data;
using UnityEngine;

namespace TerrariaMod.World
{
    /// <summary>
    /// Multi-layer Terraria parallax background renderer:
    /// Sky gradient (Background_0), 4-layer Forest surface parallax (Background_7/9/11/55),
    /// and seamless Underground/Cavern layers (Background_1/2/4/5) below SurfaceLayer.
    /// Zero per-frame heap allocations; synchronized at end of GameBootstrap.LateUpdate().
    /// </summary>
    public class TerrariaParallaxBackground : MonoBehaviour
    {
        private sealed class LayerMesh
        {
            public readonly Mesh Mesh;
            public readonly MeshRenderer Renderer;
            public readonly Material Material;
            public readonly List<Vector3> Verts = new List<Vector3>(64);
            public readonly List<Vector2> UVs = new List<Vector2>(64);
            public readonly List<Color32> Colors = new List<Color32>(64);
            public readonly List<int> Tris = new List<int>(96);

            public LayerMesh(Transform parent, string name, Texture2D tex, Shader shader, int sortingOrder, int renderQueue)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var mf = go.AddComponent<MeshFilter>();
                Renderer = go.AddComponent<MeshRenderer>();
                Renderer.sortingOrder = sortingOrder;

                Material = new Material(shader)
                {
                    mainTexture = tex,
                    renderQueue = renderQueue
                };
                Renderer.sharedMaterial = Material;

                Mesh = new Mesh { name = $"BG_{name}" };
                Mesh.MarkDynamic();
                mf.sharedMesh = Mesh;
            }

            public void Clear()
            {
                Verts.Clear();
                UVs.Clear();
                Colors.Clear();
                Tris.Clear();
            }

            public void AddQuad(float x0, float y0, float w, float h, float z, float u0, float v0, float u1, float v1, Color32 col)
            {
                int bv = Verts.Count;
                Verts.Add(new Vector3(x0, y0, z));
                Verts.Add(new Vector3(x0, y0 + h, z));
                Verts.Add(new Vector3(x0 + w, y0 + h, z));
                Verts.Add(new Vector3(x0 + w, y0, z));

                UVs.Add(new Vector2(u0, v0));
                UVs.Add(new Vector2(u0, v1));
                UVs.Add(new Vector2(u1, v1));
                UVs.Add(new Vector2(u1, v0));

                Colors.Add(col);
                Colors.Add(col);
                Colors.Add(col);
                Colors.Add(col);

                Tris.Add(bv);
                Tris.Add(bv + 1);
                Tris.Add(bv + 2);
                Tris.Add(bv);
                Tris.Add(bv + 2);
                Tris.Add(bv + 3);
            }

            public void Commit()
            {
                Mesh.Clear();
                if (Verts.Count == 0)
                {
                    Renderer.enabled = false;
                    return;
                }
                Mesh.SetVertices(Verts);
                Mesh.SetUVs(0, UVs);
                Mesh.SetColors(Colors);
                Mesh.SetTriangles(Tris, 0, calculateBounds: false);
                Mesh.RecalculateBounds();
                Renderer.enabled = true;
            }
        }

        private WorldMap _world;
        private Camera _camera;
        private Shader _shader;

        private LayerMesh _skyLayer;
        private LayerMesh _surface0;
        private LayerMesh _surface1;
        private LayerMesh _surface2;
        private LayerMesh _surface3;
        private LayerMesh _surfaceFill;
        private LayerMesh _ugTransition;
        private LayerMesh _ugDirt;
        private LayerMesh _cavernTransition;
        private LayerMesh _cavernRock;

        private Texture2D _texSky;
        private Texture2D _texSurf0;
        private Texture2D _texSurf1;
        private Texture2D _texSurf2;
        private Texture2D _texSurf3;
        private Texture2D _texUgTrans;
        private Texture2D _texUgDirt;
        private Texture2D _texCavTrans;
        private Texture2D _texCavRock;

        private float _surfaceAnchorY;
        private bool _built;

        public void Initialize(WorldMap world, GameContentCatalog catalog, Camera cam)
        {
            _world = world;
            _camera = cam;

            if (_shader == null)
            {
                _shader = Shader.Find("Sprites/Default");
                if (_shader == null) _shader = Shader.Find("UI/Default");
                if (_shader == null) _shader = Shader.Find("Unlit/Transparent");
            }

            // Compute ground horizon anchor near spawn so surface mountains/trees frame the player naturally
            int spawnGroundY = FindGroundY(world, world != null ? world.SpawnX : 0, world != null ? world.SpawnY : 200);
            _surfaceAnchorY = -spawnGroundY;

            if (!_built && catalog != null && _shader != null)
            {
                _texSky = catalog.GetPlayerTexture("Background_0");
                _texSurf0 = catalog.GetPlayerTexture("Background_7");
                _texSurf1 = catalog.GetPlayerTexture("Background_9");
                _texSurf2 = catalog.GetPlayerTexture("Background_11");
                _texSurf3 = catalog.GetPlayerTexture("Background_55");
                _texUgTrans = catalog.GetPlayerTexture("Background_1");
                _texUgDirt = catalog.GetPlayerTexture("Background_2");
                _texCavTrans = catalog.GetPlayerTexture("Background_4");
                _texCavRock = catalog.GetPlayerTexture("Background_5");

                if (_texSky != null) _skyLayer = new LayerMesh(transform, "Sky", _texSky, _shader, -40, 2950);
                if (_texSurf0 != null) _surface0 = new LayerMesh(transform, "Surface0", _texSurf0, _shader, -36, 2955);
                if (_texSurf1 != null) _surface1 = new LayerMesh(transform, "Surface1", _texSurf1, _shader, -34, 2960);
                if (_texSurf2 != null) _surface2 = new LayerMesh(transform, "Surface2", _texSurf2, _shader, -32, 2965);
                if (_texSurf3 != null) _surface3 = new LayerMesh(transform, "Surface3", _texSurf3, _shader, -30, 2970);
                _surfaceFill = new LayerMesh(transform, "SurfaceBottomFill", Texture2D.whiteTexture, _shader, -29, 2971);
                if (_texUgDirt != null) _ugDirt = new LayerMesh(transform, "UndergroundDirt", _texUgDirt, _shader, -26, 2973);
                if (_texCavRock != null) _cavernRock = new LayerMesh(transform, "CavernRock", _texCavRock, _shader, -25, 2974);
                if (_texUgTrans != null) _ugTransition = new LayerMesh(transform, "UndergroundTransition", _texUgTrans, _shader, -24, 2975);
                if (_texCavTrans != null) _cavernTransition = new LayerMesh(transform, "CavernTransition", _texCavTrans, _shader, -23, 2976);

                _built = true;
            }

            SyncToCamera();
        }

        public void SyncToCamera()
        {
            if (!_built || _world == null || _camera == null) return;

            float halfH = _camera.orthographic ? _camera.orthographicSize : 14f;
            float halfW = halfH * _camera.aspect;
            Vector3 camPos = _camera.transform.position;
            float left = camPos.x - halfW - 2f;
            float right = camPos.x + halfW + 2f;
            float bot = camPos.y - halfH - 2f;
            float top = camPos.y + halfH + 2f;

            float surfLayerY = -(float)(_world.SurfaceLayer > 10 ? _world.SurfaceLayer : _world.Height * 0.28);
            float rockLayerY = -(float)(_world.RockLayer > 10 ? _world.RockLayer : _world.Height * 0.35);

            // 1. Sky gradient & surface parallax tinted by WorldLighting day/night sky color
            Color sky = WorldLighting.EvaluateSkyColor(_world, out _);
            Color32 skyTint = new Color32((byte)(sky.r * 255f), (byte)(sky.g * 255f), (byte)(sky.b * 255f), 255);

            if (_skyLayer != null)
            {
                _skyLayer.Clear();
                if (top > surfLayerY)
                {
                    float y0 = Mathf.Max(bot, surfLayerY);
                    float y1 = top;
                    float v0 = Mathf.Clamp01((y0 - surfLayerY) / 140f);
                    float v1 = Mathf.Clamp01((y1 - surfLayerY) / 140f);
                    _skyLayer.AddQuad(left, y0, right - left, y1 - y0, 4f, 0.2f, v0, 0.8f, v1, skyTint);
                }
                _skyLayer.Commit();
            }

            // 2. Surface parallax silhouettes (far mountains -> close forest)
            float lowestParallaxBot = top;
            UpdateSurfaceLayer(_surface0, _texSurf0, left, right, top, surfLayerY, camPos, 0.06f, 0.90f, 3.0f, 3.6f, skyTint, ref lowestParallaxBot);
            UpdateSurfaceLayer(_surface1, _texSurf1, left, right, top, surfLayerY, camPos, 0.13f, 0.82f, -1.5f, 3.3f, skyTint, ref lowestParallaxBot);
            UpdateSurfaceLayer(_surface2, _texSurf2, left, right, top, surfLayerY, camPos, 0.22f, 0.72f, -6.0f, 3.0f, skyTint, ref lowestParallaxBot);
            UpdateSurfaceLayer(_surface3, _texSurf3, left, right, top, surfLayerY, camPos, 0.32f, 0.62f, -10.5f, 2.7f, skyTint, ref lowestParallaxBot);

            // Extend closest forest canopy base seamlessly down to SurfaceLayer so deep surface pits never show a cutoff edge
            if (_surfaceFill != null)
            {
                _surfaceFill.Clear();
                float fillTop = Mathf.Min(top, lowestParallaxBot + 0.1f);
                if (fillTop > surfLayerY && bot < fillTop)
                {
                    float y0 = Mathf.Max(bot, surfLayerY);
                    Color32 fillCol = new Color32((byte)(40 * sky.r), (byte)(111 * sky.g), (byte)(88 * sky.b), 255);
                    _surfaceFill.AddQuad(left, y0, right - left, fillTop - y0, 3.5f, 0f, 0f, 1f, 1f, fillCol);
                }
                _surfaceFill.Commit();
            }

            // 3. Underground dirt layer (SurfaceLayer down to RockLayer)
            UpdateTiledBand(_ugDirt, _texUgDirt, left, right, Mathf.Max(bot, rockLayerY), Mathf.Min(top, surfLayerY), camPos, 0.55f, 2.2f, new Color32(225, 225, 225, 255));

            // 4. Cavern rock layer (below RockLayer)
            UpdateTiledBand(_cavernRock, _texCavRock, left, right, bot, Mathf.Min(top, rockLayerY), camPos, 0.55f, 2.0f, new Color32(205, 205, 205, 255));

            // 5. Horizontal transition strips along SurfaceLayer and RockLayer
            UpdateStrip(_ugTransition, _texUgTrans, left, right, bot, top, surfLayerY, camPos, 0.55f, 2.1f);
            UpdateStrip(_cavernTransition, _texCavTrans, left, right, bot, top, rockLayerY, camPos, 0.55f, 1.9f);
        }

        private void UpdateSurfaceLayer(
            LayerMesh layer, Texture2D tex,
            float left, float right, float viewTop, float surfLayerY,
            Vector3 camPos, float parallaxX, float parallaxY, float baseOffsetY, float z,
            Color32 tint, ref float lowestBot)
        {
            if (layer == null || tex == null) return;
            layer.Clear();

            if (viewTop <= surfLayerY)
            {
                layer.Commit();
                return;
            }

            float tileW = tex.width / 16f;
            float tileH = tex.height / 16f;
            // Anchor near surface horizon with subtle vertical parallax
            float yBot = _surfaceAnchorY + baseOffsetY + (camPos.y - _surfaceAnchorY) * parallaxY - tileH * 0.45f;
            if (yBot < lowestBot) lowestBot = yBot;

            float scrollX = camPos.x * (1f - parallaxX);
            float startX = Mathf.Floor((left - scrollX) / tileW) * tileW + scrollX;

            for (float x = startX; x < right; x += tileW)
            {
                layer.AddQuad(x, yBot, tileW, tileH, z, 0f, 0f, 1f, 1f, tint);
            }
            layer.Commit();
        }

        private static void UpdateTiledBand(
            LayerMesh layer, Texture2D tex,
            float left, float right, float yMin, float yMax,
            Vector3 camPos, float parallax, float z, Color32 tint)
        {
            if (layer == null || tex == null) return;
            layer.Clear();

            if (yMax <= yMin)
            {
                layer.Commit();
                return;
            }

            float tileW = tex.width / 16f;
            float tileH = tex.height / 16f;
            float scrollX = camPos.x * (1f - parallax);
            float scrollY = camPos.y * (1f - parallax);

            float startX = Mathf.Floor((left - scrollX) / tileW) * tileW + scrollX;
            float startY = Mathf.Floor((yMin - scrollY) / tileH) * tileH + scrollY;

            for (float x = startX; x < right; x += tileW)
            {
                for (float y = startY; y < yMax; y += tileH)
                {
                    float qy0 = Mathf.Max(y, yMin);
                    float qy1 = Mathf.Min(y + tileH, yMax);
                    if (qy1 <= qy0) continue;

                    float v0 = (qy0 - y) / tileH;
                    float v1 = (qy1 - y) / tileH;
                    layer.AddQuad(x, qy0, tileW, qy1 - qy0, z, 0f, v0, 1f, v1, tint);
                }
            }
            layer.Commit();
        }

        private static void UpdateStrip(
            LayerMesh layer, Texture2D tex,
            float left, float right, float viewBot, float viewTop, float lineY,
            Vector3 camPos, float parallaxX, float z)
        {
            if (layer == null || tex == null) return;
            layer.Clear();

            float tileW = tex.width / 16f;
            float tileH = tex.height / 16f;
            float y0 = lineY - tileH * 0.5f;
            if (y0 + tileH < viewBot || y0 > viewTop)
            {
                layer.Commit();
                return;
            }

            float scrollX = camPos.x * (1f - parallaxX);
            float startX = Mathf.Floor((left - scrollX) / tileW) * tileW + scrollX;
            for (float x = startX; x < right; x += tileW)
            {
                layer.AddQuad(x, y0, tileW, tileH, z, 0f, 0f, 1f, 1f, new Color32(255, 255, 255, 255));
            }
            layer.Commit();
        }

        private static int FindGroundY(WorldMap world, int sx, int sy)
        {
            if (world == null) return 225;
            int minY = Mathf.Clamp(sy - 20, 0, world.Height - 1);
            int maxY = Mathf.Clamp(sy + 40, 0, world.Height - 1);
            for (int y = minY; y <= maxY; y++)
            {
                if (TileRules.IsSolid(world, sx, y))
                    return y;
            }
            return sy;
        }
    }
}
