using System.Collections.Generic;
using TerrariaMod.Data;
using TerrariaMod.Player;
using UnityEngine;

namespace TerrariaMod.World
{
    /// <summary>
    /// Manages chunk visibility pooling synchronized with the camera:
    /// rebuilds all on-screen chunks immediately (zero pop-in/holes on fast travel or zoom)
    /// and prewarms a 1-chunk outer ring smoothly across frames.
    /// </summary>
    public class TerrariaWorldController : MonoBehaviour
    {
        public int maxPrewarmRebuildsPerFrame = 4;

        private WorldMap _world;
        private GameContentCatalog _catalog;
        private ItemDatabase _itemDb;
        private Camera _camera;
        private Transform _chunkRoot;
        private Transform _dropRoot;

        private struct MaterialKey : System.IEquatable<MaterialKey>
        {
            public readonly Texture2D Texture;
            public readonly int RenderQueue;

            public MaterialKey(Texture2D texture, int renderQueue)
            {
                Texture = texture;
                RenderQueue = renderQueue;
            }

            public bool Equals(MaterialKey other) => Texture == other.Texture && RenderQueue == other.RenderQueue;
            public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);
            public override int GetHashCode() => ((Texture != null ? Texture.GetHashCode() : 0) * 397) ^ RenderQueue;
        }

        private readonly Dictionary<long, TerrariaChunkRenderer> _activeChunks = new Dictionary<long, TerrariaChunkRenderer>();
        private readonly Stack<TerrariaChunkRenderer> _chunkPool = new Stack<TerrariaChunkRenderer>();
        private readonly Dictionary<MaterialKey, Material> _materialCache = new Dictionary<MaterialKey, Material>();
        private readonly List<long> _recycleScratch = new List<long>();
        private readonly List<TerrariaChunkRenderer> _prewarmQueue = new List<TerrariaChunkRenderer>();
        private Material _baseSpriteMat;

        private int _lastPoolMinCx = int.MinValue, _lastPoolMaxCx, _lastPoolMinCy, _lastPoolMaxCy;
        private int _viewMinCx, _viewMaxCx, _viewMinCy, _viewMaxCy;
        private bool _syncedThisFrame;

        private readonly WorldLighting _lighting = new WorldLighting();
        private Transform _playerTransform;
        private InventoryModel _inventory;
        private float _liquidTimer;
        private int _lastPlayerTorchTx = int.MinValue;
        private int _lastPlayerTorchTy = int.MinValue;
        private bool _lastHoldingTorch;

        public WorldMap World => _world;
        public WorldLighting Lighting => _lighting;

        public void Initialize(WorldMap world, GameContentCatalog catalog, ItemDatabase itemDb, Camera cam)
        {
            if (_world != null)
                _world.ChunkDirty -= OnChunkDirty;

            _world = world;
            _catalog = catalog;
            _itemDb = itemDb;
            _camera = cam;

            if (_chunkRoot == null)
            {
                _chunkRoot = new GameObject("Chunks").transform;
                _chunkRoot.SetParent(transform, false);
            }
            if (_dropRoot == null)
            {
                _dropRoot = new GameObject("ItemDrops").transform;
                _dropRoot.SetParent(transform, false);
            }

            if (_baseSpriteMat == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("UI/Default");
                if (shader == null) shader = Shader.Find("Unlit/Transparent");
                _baseSpriteMat = shader != null
                    ? new Material(shader)
                    : Resources.GetBuiltinResource<Material>("Sprites-Default.mat");
            }

            ClearActiveChunks();
            ClearDrops();

            _lastPoolMinCx = int.MinValue;
            if (_world != null)
                _world.ChunkDirty += OnChunkDirty;
        }

        private void OnDestroy()
        {
            if (_world != null)
                _world.ChunkDirty -= OnChunkDirty;
        }

        public void SetPlayerLightSource(Transform playerTransform, InventoryModel inventory)
        {
            _playerTransform = playerTransform;
            _inventory = inventory;
        }

        public void NotifyLightingChangedAround(int tx, int ty)
        {
            RecomputeVisibleLighting();
            int minCx = Mathf.Max(0, (tx - 16) / WorldMap.ChunkSize);
            int maxCx = Mathf.Min(_world.ChunksX - 1, (tx + 16) / WorldMap.ChunkSize);
            int minCy = Mathf.Max(0, (ty - 16) / WorldMap.ChunkSize);
            int maxCy = Mathf.Min(_world.ChunksY - 1, (ty + 16) / WorldMap.ChunkSize);
            for (int cx = minCx; cx <= maxCx; cx++)
                for (int cy = minCy; cy <= maxCy; cy++)
                    OnChunkDirty(cx, cy);
        }

        private void LateUpdate()
        {
            if (_world != null)
            {
                _lighting.AdvanceTime(_world, Time.deltaTime);
                if (_camera != null)
                {
                    Color sky = _lighting.AmbientSkyColor;
                    _camera.backgroundColor = new Color(0.39f * sky.r, 0.62f * sky.g, 0.94f * sky.b, 1f);
                }

                _liquidTimer += Time.deltaTime;
                if (_liquidTimer >= 0.066f)
                {
                    _liquidTimer = 0f;
                    int lMinX = _viewMinCx * WorldMap.ChunkSize - 8;
                    int lMaxX = (_viewMaxCx + 1) * WorldMap.ChunkSize + 8;
                    int lMinY = _viewMinCy * WorldMap.ChunkSize - 8;
                    int lMaxY = (_viewMaxCy + 1) * WorldMap.ChunkSize + 8;
                    LiquidSim.StepRegion(_world, lMinX, lMaxX, lMinY, lMaxY);
                }

                bool holdingTorch = IsPlayerHoldingLight();
                int ptx = _playerTransform != null ? WorldMap.WorldToTileX(_playerTransform.position.x) : int.MinValue;
                int pty = _playerTransform != null ? WorldMap.WorldToTileY(_playerTransform.position.y + 1.2f) : int.MinValue;
                if (holdingTorch != _lastHoldingTorch || (holdingTorch && (ptx != _lastPlayerTorchTx || pty != _lastPlayerTorchTy)))
                {
                    _lastHoldingTorch = holdingTorch;
                    _lastPlayerTorchTx = ptx;
                    _lastPlayerTorchTy = pty;
                    if (_playerTransform != null)
                        NotifyLightingChangedAround(ptx, pty);
                }

                if (_lighting.TimeStepDirty)
                {
                    _lighting.TimeStepDirty = false;
                    RecomputeVisibleLighting();
                    foreach (var kv in _activeChunks)
                        kv.Value.IsDirty = true;
                }
            }

            if (!_syncedThisFrame)
                SyncToCamera();
            _syncedThisFrame = false;
        }

        private bool IsPlayerHoldingLight()
        {
            if (_inventory == null) return false;
            var def = _inventory.GetSelectedItemDef();
            return def != null && def.createTile == TerrariaMod.ID.TileID.Torches;
        }

        private void RecomputeVisibleLighting()
        {
            if (_world == null) return;
            int minX = Mathf.Max(0, (_viewMinCx - 1) * WorldMap.ChunkSize - 16);
            int maxX = Mathf.Min(_world.Width - 1, (_viewMaxCx + 2) * WorldMap.ChunkSize + 16);
            int minY = Mathf.Max(0, (_viewMinCy - 1) * WorldMap.ChunkSize - 16);
            int maxY = Mathf.Min(_world.Height - 1, (_viewMaxCy + 2) * WorldMap.ChunkSize + 16);
            Vector2? pPos = _playerTransform != null ? (Vector2?)new Vector2(_playerTransform.position.x, _playerTransform.position.y + 1.2f) : null;
            _lighting.ComputeRegion(_world, minX, maxX, minY, maxY, pPos, IsPlayerHoldingLight());
        }

        private void EnsureLightingCoversChunk(TerrariaChunkRenderer chunk)
        {
            int cMinX = chunk.ChunkX * WorldMap.ChunkSize;
            int cMaxX = Mathf.Min(_world.Width - 1, cMinX + WorldMap.ChunkSize - 1);
            int cMinY = chunk.ChunkY * WorldMap.ChunkSize;
            int cMaxY = Mathf.Min(_world.Height - 1, cMinY + WorldMap.ChunkSize - 1);
            if (!_lighting.CoversTileRect(cMinX, cMaxX, cMinY, cMaxY))
                RecomputeVisibleLighting();
        }

        public void SyncToCamera()
        {
            _syncedThisFrame = true;
            if (_world == null || _camera == null) return;
            UpdateVisibleChunks();
            RebuildDirtyChunks(forceAllOnScreen: true);
        }

        public void ForceRebuildVisibleNow()
        {
            if (_world == null || _camera == null) return;
            UpdateVisibleChunks();
            RecomputeVisibleLighting();
            foreach (var chunk in _activeChunks.Values)
            {
                if (!chunk.IsDirty) continue;
                chunk.Rebuild(_world, GetTileTex, GetWallTex, GetSpecialTex, GetMaterialForTexture, _lighting);
            }
        }

        public void SpawnDrop(int itemId, int stack, Vector2 worldPos)
        {
            if (_catalog == null || itemId <= 0 || stack <= 0) return;
            Sprite sprite = _catalog.GetItemSprite(itemId);
            if (sprite == null) return;

            var go = new GameObject($"Drop_{itemId}");
            go.transform.SetParent(_dropRoot, false);
            go.transform.position = new Vector3(worldPos.x, worldPos.y, -0.2f);
            var drop = go.AddComponent<WorldItemDrop>();
            drop.Setup(_world, itemId, stack, sprite);
        }

        private void UpdateVisibleChunks()
        {
            float halfH = _camera.orthographic ? _camera.orthographicSize : 15f;
            float halfW = halfH * _camera.aspect;
            Vector3 camPos = _camera.transform.position;

            int minTx = WorldMap.WorldToTileX(camPos.x - halfW) - 2;
            int maxTx = WorldMap.WorldToTileX(camPos.x + halfW) + 2;
            int minTy = WorldMap.WorldToTileY(camPos.y + halfH) - 2;
            int maxTy = WorldMap.WorldToTileY(camPos.y - halfH) + 2;

            _viewMinCx = Mathf.Clamp(Mathf.FloorToInt(minTx / (float)WorldMap.ChunkSize), 0, _world.ChunksX - 1);
            _viewMaxCx = Mathf.Clamp(Mathf.FloorToInt(maxTx / (float)WorldMap.ChunkSize), 0, _world.ChunksX - 1);
            _viewMinCy = Mathf.Clamp(Mathf.FloorToInt(minTy / (float)WorldMap.ChunkSize), 0, _world.ChunksY - 1);
            _viewMaxCy = Mathf.Clamp(Mathf.FloorToInt(maxTy / (float)WorldMap.ChunkSize), 0, _world.ChunksY - 1);

            // Keep 1-chunk prewarm margin around the active viewport
            int poolMinCx = Mathf.Max(0, _viewMinCx - 1);
            int poolMaxCx = Mathf.Min(_world.ChunksX - 1, _viewMaxCx + 1);
            int poolMinCy = Mathf.Max(0, _viewMinCy - 1);
            int poolMaxCy = Mathf.Min(_world.ChunksY - 1, _viewMaxCy + 1);

            if (poolMinCx == _lastPoolMinCx && poolMaxCx == _lastPoolMaxCx &&
                poolMinCy == _lastPoolMinCy && poolMaxCy == _lastPoolMaxCy)
                return;

            _lastPoolMinCx = poolMinCx;
            _lastPoolMaxCx = poolMaxCx;
            _lastPoolMinCy = poolMinCy;
            _lastPoolMaxCy = poolMaxCy;

            _recycleScratch.Clear();
            foreach (var kv in _activeChunks)
            {
                var c = kv.Value;
                if (c.ChunkX < poolMinCx || c.ChunkX > poolMaxCx || c.ChunkY < poolMinCy || c.ChunkY > poolMaxCy)
                    _recycleScratch.Add(kv.Key);
            }

            for (int i = 0; i < _recycleScratch.Count; i++)
            {
                long key = _recycleScratch[i];
                var chunk = _activeChunks[key];
                _activeChunks.Remove(key);
                chunk.Recycle();
                _chunkPool.Push(chunk);
            }

            for (int cx = poolMinCx; cx <= poolMaxCx; cx++)
            {
                for (int cy = poolMinCy; cy <= poolMaxCy; cy++)
                {
                    long key = ChunkKey(cx, cy);
                    if (_activeChunks.ContainsKey(key)) continue;

                    TerrariaChunkRenderer chunk = AcquireChunk();
                    chunk.Assign(cx, cy);
                    _activeChunks[key] = chunk;
                }
            }
        }

        private void RebuildDirtyChunks(bool forceAllOnScreen)
        {
            _prewarmQueue.Clear();

            foreach (var chunk in _activeChunks.Values)
            {
                if (!chunk.IsDirty) continue;

                bool inView =
                    chunk.ChunkX >= _viewMinCx && chunk.ChunkX <= _viewMaxCx &&
                    chunk.ChunkY >= _viewMinCy && chunk.ChunkY <= _viewMaxCy;

                if (inView && forceAllOnScreen)
                {
                    EnsureLightingCoversChunk(chunk);
                    chunk.Rebuild(_world, GetTileTex, GetWallTex, GetSpecialTex, GetMaterialForTexture, _lighting);
                }
                else
                    _prewarmQueue.Add(chunk);
            }

            if (_prewarmQueue.Count == 0) return;

            int centerCx = (_viewMinCx + _viewMaxCx) >> 1;
            int centerCy = (_viewMinCy + _viewMaxCy) >> 1;
            _prewarmQueue.Sort((a, b) =>
            {
                int da = Mathf.Abs(a.ChunkX - centerCx) + Mathf.Abs(a.ChunkY - centerCy);
                int db = Mathf.Abs(b.ChunkX - centerCx) + Mathf.Abs(b.ChunkY - centerCy);
                return da.CompareTo(db);
            });

            int budget = Mathf.Min(maxPrewarmRebuildsPerFrame, _prewarmQueue.Count);
            for (int i = 0; i < budget; i++)
            {
                EnsureLightingCoversChunk(_prewarmQueue[i]);
                _prewarmQueue[i].Rebuild(_world, GetTileTex, GetWallTex, GetSpecialTex, GetMaterialForTexture, _lighting);
            }
        }

        private void OnChunkDirty(int cx, int cy)
        {
            if (_activeChunks.TryGetValue(ChunkKey(cx, cy), out var chunk))
                chunk.IsDirty = true;
        }

        private TerrariaChunkRenderer AcquireChunk()
        {
            if (_chunkPool.Count > 0)
                return _chunkPool.Pop();

            var go = new GameObject("Chunk");
            go.transform.SetParent(_chunkRoot, false);
            var c = go.AddComponent<TerrariaChunkRenderer>();
            c.Initialize();
            return c;
        }

        private void ClearActiveChunks()
        {
            foreach (var chunk in _activeChunks.Values)
            {
                chunk.Recycle();
                _chunkPool.Push(chunk);
            }
            _activeChunks.Clear();
        }

        private void ClearDrops()
        {
            if (_dropRoot == null) return;
            for (int i = _dropRoot.childCount - 1; i >= 0; i--)
                Destroy(_dropRoot.GetChild(i).gameObject);
        }

        private Texture2D GetTileTex(int tileId) => _catalog != null ? _catalog.GetTileTexture(tileId) : null;
        private Texture2D GetWallTex(int wallId) => _catalog != null ? _catalog.GetWallTexture(wallId) : null;
        private Texture2D GetSpecialTex(string key)
        {
            Texture2D tex = _catalog != null ? _catalog.GetPlayerTexture(key) : null;
#if UNITY_EDITOR
            if (tex == null && key.StartsWith("Liquid_"))
                tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/Sprites/Standalone/{key}.png");
#endif
            return tex;
        }

        private Material GetMaterialForTexture(Texture2D tex, int renderQueue)
        {
            var key = new MaterialKey(tex, renderQueue);
            if (!_materialCache.TryGetValue(key, out var mat))
            {
                mat = new Material(_baseSpriteMat)
                {
                    mainTexture = tex,
                    renderQueue = renderQueue
                };
                _materialCache[key] = mat;
            }
            return mat;
        }

        private static long ChunkKey(int cx, int cy) => ((long)cx << 32) | (uint)cy;
    }
}
