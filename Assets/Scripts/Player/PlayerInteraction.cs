using System.Collections.Generic;
using TerrariaMod.Data;
using TerrariaMod.ID;
using TerrariaMod.World;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TerrariaMod.Player
{
    public enum InteractionToolMode
    {
        Auto = 0,
        Mine = 1,
        Place = 2
    }

    /// <summary>
    /// Tile cursor selection, progressive pickaxe/axe/hammer mining, block/wall placement,
    /// and world item drop spawning.
    /// </summary>
    public class PlayerInteraction : MonoBehaviour
    {
        public int tileReachX = 5;
        public int tileReachY = 4;
        public InteractionToolMode touchModeOverride = InteractionToolMode.Auto;

        private struct HitTileEntry
        {
            public int X;
            public int Y;
            public int Damage;
            public float Ttl;
            public int Variant;
            public SpriteRenderer Overlay;
        }

        private WorldMap _world;
        private TerrariaWorldController _worldCtrl;
        private InventoryModel _inventory;
        private PlayerController2D _player;
        private Camera _camera;
        private GameContentCatalog _catalog;

        private LineRenderer _cursorBox;
        private readonly List<HitTileEntry> _hitTiles = new List<HitTileEntry>(16);
        private readonly Stack<SpriteRenderer> _crackPool = new Stack<SpriteRenderer>(16);
        private Sprite[,] _crackSprites;
        private Transform _crackRoot;
        private float _useCooldown;
        private bool _isPrimaryPressed;
        private bool _wasFocused;

        public int HoveredTileX { get; private set; }
        public int HoveredTileY { get; private set; }
        public bool IsHoverInRange { get; private set; }

        public void Initialize(WorldMap world, TerrariaWorldController worldCtrl, InventoryModel inventory, PlayerController2D player, Camera cam, GameContentCatalog catalog = null)
        {
            _world = world;
            _worldCtrl = worldCtrl;
            _inventory = inventory;
            _player = player;
            _camera = cam;
            _catalog = catalog;
            _isPrimaryPressed = false;
            _wasFocused = Application.isFocused;
            ClearAllHitTiles();
            EnsureCrackSprites();
            BuildCursorIndicator();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
                _isPrimaryPressed = false;
        }

        private void OnDisable()
        {
            _isPrimaryPressed = false;
        }

        private static bool IsPointerInGameView(Vector3 screenPos)
        {
            return screenPos.x >= 0f && screenPos.x <= Screen.width &&
                   screenPos.y >= 0f && screenPos.y <= Screen.height;
        }

        private void Update()
        {
            if (_world == null || _player == null || _camera == null) return;

            if (_useCooldown > 0f)
                _useCooldown -= Time.deltaTime;

            PruneHitTiles(Time.deltaTime);

            bool focused = Application.isFocused;
            bool justGainedFocus = focused && !_wasFocused;
            _wasFocused = focused;

            Vector3 screen = Input.mousePosition;
            bool pointerValid = focused && IsPointerInGameView(screen);
            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

            if (!pointerValid || !Input.GetMouseButton(0))
            {
                _isPrimaryPressed = false;
            }
            else if (!justGainedFocus && Input.GetMouseButtonDown(0) && !overUI)
            {
                // Require an explicit press inside an already-focused Game View so focus clicks or OS mouse0 latch cannot mine.
                _isPrimaryPressed = true;
            }

            UpdateCursor(pointerValid, screen);

            if (!overUI && IsHoverInRange && (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.E)))
            {
                if (TryToggleDoor(HoveredTileX, HoveredTileY))
                    return;
            }

            if (_isPrimaryPressed && !overUI && IsHoverInRange && _useCooldown <= 0f)
            {
                PerformActionAt(HoveredTileX, HoveredTileY);
            }
        }

        private void UpdateCursor(bool pointerValid, Vector3 screen)
        {
            if (!pointerValid)
            {
                IsHoverInRange = false;
                if (_cursorBox != null)
                    _cursorBox.enabled = false;
                return;
            }

            Vector3 wp = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_camera.transform.position.z));

            HoveredTileX = WorldMap.WorldToTileX(wp.x);
            HoveredTileY = WorldMap.WorldToTileY(wp.y);

            Vector2 pc = _player.Center;
            int ptx = WorldMap.WorldToTileX(pc.x);
            int pty = WorldMap.WorldToTileY(pc.y);

            int boost = _inventory != null ? (_inventory.GetSelectedItemDef()?.tileBoost ?? 0) : 0;
            int rx = Mathf.Max(1, tileReachX + boost);
            int ry = Mathf.Max(1, tileReachY + boost);

            IsHoverInRange =
                _world.InBounds(HoveredTileX, HoveredTileY) &&
                Mathf.Abs(HoveredTileX - ptx) <= rx &&
                Mathf.Abs(HoveredTileY - pty) <= ry;

            if (_cursorBox != null)
            {
                bool show = _world.InBounds(HoveredTileX, HoveredTileY);
                _cursorBox.enabled = show;
                if (show)
                {
                    float x0 = HoveredTileX;
                    float y0 = -HoveredTileY - 1f;
                    _cursorBox.SetPosition(0, new Vector3(x0, y0, -0.3f));
                    _cursorBox.SetPosition(1, new Vector3(x0 + 1f, y0, -0.3f));
                    _cursorBox.SetPosition(2, new Vector3(x0 + 1f, y0 + 1f, -0.3f));
                    _cursorBox.SetPosition(3, new Vector3(x0, y0 + 1f, -0.3f));

                    Color col = IsHoverInRange ? new Color(1f, 0.85f, 0.25f, 0.9f) : new Color(0.9f, 0.35f, 0.35f, 0.45f);
                    _cursorBox.startColor = col;
                    _cursorBox.endColor = col;
                }
            }
        }

        public void PerformActionAt(int tx, int ty)
        {
            if (!_world.InBounds(tx, ty)) return;
            var def = _inventory.GetSelectedItemDef();
            if (def == null) return;

            float dx = WorldMap.TileToWorldX(tx) - _player.Center.x;
            if (Mathf.Abs(dx) > 0.25f)
                _player.SetFacing(dx > 0f ? 1 : -1);

            float swingSec = def.useTime > 0 ? Mathf.Clamp(def.useTime / 60f, 0.1f, 0.45f) : 0.25f;
            _useCooldown = swingSec;
            _player.TriggerSwing(swingSec);

            bool wantPlace = touchModeOverride == InteractionToolMode.Place ||
                             (touchModeOverride == InteractionToolMode.Auto && (def.createTile >= 0 || def.createWall > 0) && def.pick <= 0 && def.axe <= 0 && def.hammer <= 0);

            if (wantPlace)
            {
                TryPlace(def, tx, ty);
                return;
            }

            if (def.pick > 0 || def.axe > 0)
            {
                TryMineTile(def, tx, ty);
            }
            else if (def.hammer > 0)
            {
                TryMineWall(def, tx, ty);
            }
        }

        private void TryMineTile(ItemDefinition tool, int tx, int ty)
        {
            if (!TileRules.CanKillTile(_world, tx, ty)) return;
            ushort type = _world.GetTileType(tx, ty);

            // Terraria stores Item.axe as (axePower / 5), e.g. IronAxe = 9 -> 45% axe power; trees require an axe (pickaxes cannot chop trees)
            bool isTree = type == TileID.Trees || type == TileID.PalmTree;
            int power = isTree ? tool.axe * 5 : tool.pick;
            if (power < TileRules.GetRequiredPickPower(type) || power <= 0)
                return;

            int maxDamage = TileRules.GetTileMaxDamage(type);
            int totalDamage = AddHitTileDamage(tx, ty, power, maxDamage);
            if (totalDamage < maxDamage)
                return;

            ClearHitTileAt(tx, ty);
            KillTileInternal(tx, ty, isTree, type);
            _worldCtrl.NotifyLightingChangedAround(tx, ty);
        }

        private void KillTileInternal(int tx, int ty, bool isTree, ushort type)
        {
            if (isTree)
            {
                FellTreeFrom(tx, ty, type);
                return;
            }

            if (TileRules.GetMultiTileSize(type, out int mw, out int mh, out _, out _))
            {
                int idx = _world.Index(tx, ty);
                int dx = (_world.FrameX[idx] / 18) % mw;
                int dy = (_world.FrameY[idx] / 18) % mh;
                int startX = tx - dx;
                int startY = ty - dy;
                for (int iy = 0; iy < mh; iy++)
                {
                    for (int ix = 0; ix < mw; ix++)
                    {
                        int gx = startX + ix;
                        int gy = startY + iy;
                        if (_world.InBounds(gx, gy) && _world.IsActive(gx, gy) && _world.GetTileType(gx, gy) == type)
                        {
                            _world.ClearTile(gx, gy);
                            TileFraming.FrameNeighborhood(_world, gx, gy);
                        }
                    }
                }
                int mDropId = TileRules.GetDropItemForTile(type);
                if (mDropId > 0)
                    _worldCtrl.SpawnDrop(mDropId, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(ty)));
                return;
            }

            // Vanilla WorldGen.KillTile: grass turns to dirt/mud on first break before clearing
            if (type == TileID.Grass || type == TileID.CorruptGrass || type == TileID.CrimsonGrass || type == TileID.HallowedGrass)
            {
                _world.SetTile(tx, ty, TileID.Dirt, active: true);
                TileFraming.FrameNeighborhood(_world, tx, ty);
                CheckTileSurvivalCascade(tx, ty);
                return;
            }
            if (type == TileID.JungleGrass || type == TileID.MushroomGrass)
            {
                _world.SetTile(tx, ty, TileID.Mud, active: true);
                TileFraming.FrameNeighborhood(_world, tx, ty);
                CheckTileSurvivalCascade(tx, ty);
                return;
            }

            _world.ClearTile(tx, ty);
            TileFraming.FrameNeighborhood(_world, tx, ty);

            int dropId = TileRules.GetDropItemForTile(type);
            if (dropId > 0)
                _worldCtrl.SpawnDrop(dropId, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(ty)));

            CheckTileSurvivalCascade(tx, ty);
        }

        private void CheckTileSurvivalCascade(int tx, int ty)
        {
            // Check prop directly above (plants, saplings, bottles, etc.)
            if (_world.InBounds(tx, ty - 1) && _world.IsActive(tx, ty - 1))
            {
                ushort above = _world.GetTileType(tx, ty - 1);
                if (TileRules.IsGroundAnchoredProp(above) && !TileRules.IsSolid(_world, tx, ty))
                {
                    _world.ClearTile(tx, ty - 1);
                    TileFraming.FrameNeighborhood(_world, tx, ty - 1);
                    int dropId = TileRules.GetDropItemForTile(above);
                    if (dropId > 0)
                        _worldCtrl.SpawnDrop(dropId, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(ty - 1)));
                }
            }

            // Check adjacent torches that may have lost their wall/block anchor
            CheckTorchAnchor(tx, ty - 1);
            CheckTorchAnchor(tx - 1, ty);
            CheckTorchAnchor(tx + 1, ty);
        }

        private void CheckTorchAnchor(int tx, int ty)
        {
            if (!_world.InBounds(tx, ty) || !_world.IsActive(tx, ty)) return;
            if (_world.GetTileType(tx, ty) != TileID.Torches) return;
            if (_world.GetWallType(tx, ty) > 0) return;

            bool anchored =
                TileRules.IsSolid(_world, tx, ty + 1) ||
                TileRules.IsSolid(_world, tx - 1, ty) ||
                TileRules.IsSolid(_world, tx + 1, ty);
            if (!anchored)
            {
                _world.ClearTile(tx, ty);
                TileFraming.FrameNeighborhood(_world, tx, ty);
                _worldCtrl.SpawnDrop(ItemID.Torch, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(ty)));
            }
        }

        private int AddHitTileDamage(int tx, int ty, int power, int maxDamage)
        {
            for (int i = 0; i < _hitTiles.Count; i++)
            {
                if (_hitTiles[i].X == tx && _hitTiles[i].Y == ty)
                {
                    var e = _hitTiles[i];
                    e.Damage += power;
                    e.Ttl = 1.5f;
                    UpdateCrackVisual(ref e, maxDamage);
                    _hitTiles[i] = e;
                    return e.Damage;
                }
            }

            var created = new HitTileEntry
            {
                X = tx,
                Y = ty,
                Damage = power,
                Ttl = 1.5f,
                Variant = Random.Range(0, 6),
                Overlay = null
            };
            if (created.Damage < maxDamage)
                UpdateCrackVisual(ref created, maxDamage);
            _hitTiles.Add(created);
            return created.Damage;
        }

        private void ClearHitTileAt(int tx, int ty)
        {
            for (int i = _hitTiles.Count - 1; i >= 0; i--)
            {
                if (_hitTiles[i].X == tx && _hitTiles[i].Y == ty)
                {
                    RecycleCrackOverlay(_hitTiles[i].Overlay);
                    _hitTiles.RemoveAt(i);
                }
            }
        }

        private void PruneHitTiles(float dt)
        {
            for (int i = _hitTiles.Count - 1; i >= 0; i--)
            {
                var e = _hitTiles[i];
                e.Ttl -= dt;
                if (e.Ttl <= 0f || !_world.IsActive(e.X, e.Y))
                {
                    RecycleCrackOverlay(e.Overlay);
                    _hitTiles.RemoveAt(i);
                }
                else
                {
                    _hitTiles[i] = e;
                }
            }
        }

        private void ClearAllHitTiles()
        {
            for (int i = 0; i < _hitTiles.Count; i++)
                RecycleCrackOverlay(_hitTiles[i].Overlay);
            _hitTiles.Clear();
        }

        private void EnsureCrackSprites()
        {
            if (_crackSprites != null || _catalog == null) return;
            Texture2D tex = _catalog.GetPlayerTexture("TileCracks");
            if (tex == null) return;

            _crackSprites = new Sprite[6, 4];
            for (int col = 0; col < 6; col++)
            {
                for (int row = 0; row < 4; row++)
                {
                    int px = col * 18;
                    int pyFromBot = tex.height - row * 18 - 16;
                    if (px + 16 <= tex.width && pyFromBot >= 0)
                        _crackSprites[col, row] = Sprite.Create(tex, new Rect(px, pyFromBot, 16, 16), new Vector2(0.5f, 0.5f), 16f);
                }
            }
        }

        private void UpdateCrackVisual(ref HitTileEntry entry, int maxDamage)
        {
            if (_crackSprites == null) return;
            float ratio = Mathf.Clamp01(entry.Damage / (float)Mathf.Max(1, maxDamage));
            int stage = Mathf.Clamp((int)(ratio * 4f), 0, 3);
            if (entry.Overlay == null)
            {
                if (_crackRoot == null)
                {
                    var rootGo = new GameObject("TileCrackOverlays");
                    rootGo.transform.SetParent(transform, false);
                    _crackRoot = rootGo.transform;
                }
                entry.Overlay = _crackPool.Count > 0 ? _crackPool.Pop() : CreateCrackRenderer();
                entry.Overlay.transform.position = new Vector3(WorldMap.TileToWorldX(entry.X), WorldMap.TileToWorldY(entry.Y), -0.04f);
                entry.Overlay.gameObject.SetActive(true);
            }
            entry.Overlay.sprite = _crackSprites[entry.Variant % 6, stage];
        }

        private SpriteRenderer CreateCrackRenderer()
        {
            var go = new GameObject("CrackOverlay");
            go.transform.SetParent(_crackRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 3;
            return sr;
        }

        private void RecycleCrackOverlay(SpriteRenderer sr)
        {
            if (sr == null) return;
            sr.gameObject.SetActive(false);
            _crackPool.Push(sr);
        }

        private void FellTreeFrom(int tx, int ty, ushort treeType)
        {
            int dropId = TileRules.GetDropItemForTile(treeType);
            for (int y = ty; y >= 0; y--)
            {
                if (!_world.IsActive(tx, y) || _world.GetTileType(tx, y) != treeType)
                    break;

                bool isCanopySentinel = _world.FrameY[_world.Index(tx, y)] >= 198;
                _world.ClearTile(tx, y);
                TileFraming.FrameNeighborhood(_world, tx, y);

                if (dropId > 0 && !isCanopySentinel)
                    _worldCtrl.SpawnDrop(dropId, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(y)));
                else if (isCanopySentinel && Random.value < 0.25f)
                    _worldCtrl.SpawnDrop(ItemID.Acorn, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(y)));

                // Clear attached side branches on (tx - 1, y) and (tx + 1, y)
                ClearTreeBranchIfAttached(tx - 1, y, treeType);
                ClearTreeBranchIfAttached(tx + 1, y, treeType);
            }

            // 1:1 vanilla CheckTree stump-top framing: if chopped mid-trunk, switch the remaining top stump tile to rounded top (FrameX=110)
            int stumpY = ty + 1;
            if (stumpY < _world.Height && _world.IsActive(tx, stumpY) && _world.GetTileType(tx, stumpY) == treeType)
            {
                int idx = _world.Index(tx, stumpY);
                short currentFy = _world.FrameY[idx];
                int variant = currentFy >= 0 ? (currentFy % 66) : ((Mathf.Abs(tx * 7 + stumpY * 13) % 3) * 22);
                if (variant != 0 && variant != 22 && variant != 44)
                    variant = 22;
                _world.FrameX[idx] = 110;
                _world.FrameY[idx] = (short)variant;
                _world.MarkTileAndNeighborsDirty(tx, stumpY);
            }
        }

        private void ClearTreeBranchIfAttached(int bx, int by, ushort treeType)
        {
            if (!_world.InBounds(bx, by) || !_world.IsActive(bx, by) || _world.GetTileType(bx, by) != treeType)
                return;

            // Leafy branches / canopy sentinels (fy >= 198) are never standalone tree trunks
            if (_world.FrameY[_world.Index(bx, by)] >= 198 || !IsSeparateTreeTrunk(bx, by, treeType))
            {
                _world.ClearTile(bx, by);
                TileFraming.FrameNeighborhood(_world, bx, by);
            }
        }

        private bool IsSeparateTreeTrunk(int x, int y, ushort treeType)
        {
            int top = y;
            while (top - 1 >= 0 && _world.IsActive(x, top - 1) && _world.GetTileType(x, top - 1) == treeType && _world.FrameY[_world.Index(x, top - 1)] < 198)
                top--;

            int bot = y;
            while (bot + 1 < _world.Height && _world.IsActive(x, bot + 1) && _world.GetTileType(x, bot + 1) == treeType)
                bot++;

            int height = bot - top + 1;
            return height >= 3 && TileRules.IsSolid(_world, x, bot + 1);
        }

        private void TryMineWall(ItemDefinition tool, int tx, int ty)
        {
            ushort wall = _world.GetWallType(tx, ty);
            if (wall == 0 || tool.hammer <= 0) return;
            if (!HasOpenWallEdge(tx, ty)) return;

            _world.SetWall(tx, ty, 0);
            TileFraming.FrameNeighborhood(_world, tx, ty);

            int dropId = TileRules.GetDropItemForWall(wall);
            if (dropId > 0)
                _worldCtrl.SpawnDrop(dropId, 1, new Vector2(WorldMap.TileToWorldX(tx), WorldMap.TileToWorldY(ty)));
        }

        private bool HasOpenWallEdge(int tx, int ty)
        {
            return IsWallEmptyOrOut(tx - 1, ty) ||
                   IsWallEmptyOrOut(tx + 1, ty) ||
                   IsWallEmptyOrOut(tx, ty - 1) ||
                   IsWallEmptyOrOut(tx, ty + 1);
        }

        private bool IsWallEmptyOrOut(int tx, int ty) => !_world.InBounds(tx, ty) || _world.GetWallType(tx, ty) == 0;

        private void TryPlace(ItemDefinition item, int tx, int ty)
        {
            if (item.createTile >= 0)
            {
                ushort tileId = (ushort)item.createTile;
                if (TileRules.GetMultiTileSize(tileId, out int mw, out int mh, out int ox, out int oy))
                {
                    // If user clicked directly on the solid ground surface, snap placement 1 tile above
                    int placeY = TileRules.IsSolid(_world, tx, ty) && ty > 0 ? ty - 1 : ty;
                    int startX = tx - ox;
                    int startY = placeY - oy;
                    for (int iy = 0; iy < mh; iy++)
                    {
                        for (int ix = 0; ix < mw; ix++)
                        {
                            int gx = startX + ix;
                            int gy = startY + iy;
                            if (!_world.InBounds(gx, gy))
                                return;
                            if (_world.IsActive(gx, gy) && !TileRules.IsCuttablePlant(_world.GetTileType(gx, gy)))
                                return;
                        }
                    }
                    // Require solid or solid-top ground beneath the multi-tile base
                    int groundY = startY + mh;
                    for (int ix = 0; ix < mw; ix++)
                    {
                        int gx = startX + ix;
                        if (!_world.InBounds(gx, groundY) || (!TileRules.IsSolid(_world, gx, groundY) && !TileRules.IsSolidTop(_world, gx, groundY)))
                            return;
                    }
                    if (!_inventory.ConsumeSelected(1)) return;

                    for (int iy = 0; iy < mh; iy++)
                    {
                        for (int ix = 0; ix < mw; ix++)
                        {
                            int gx = startX + ix;
                            int gy = startY + iy;
                            short fx = (short)(item.placeStyle * (mw * 18) + ix * 18);
                            short fy = (short)(iy * 18);
                            _world.SetTile(gx, gy, tileId, active: true, frameX: fx, frameY: fy);
                            _world.MarkTileAndNeighborsDirty(gx, gy);
                        }
                    }
                    return;
                }

                bool canReplacePlant = _world.IsActive(tx, ty) && TileRules.IsCuttablePlant(_world.GetTileType(tx, ty));
                if (_world.IsActive(tx, ty) && !canReplacePlant) return;
                if (!HasAnchorNeighbor(tx, ty)) return;

                // Prevent placing solid block right inside player hitbox
                if (!TileRules.IsSolid(_world, tx, ty) && TileRules.IsSolidForFraming(tileId) && tileId != TileID.Platforms && OverlapsPlayer(tx, ty))
                    return;

                if (!_inventory.ConsumeSelected(1)) return;

                short initFx = tileId == TileID.Platforms ? (short)0 : (short)(item.placeStyle * 18);
                short initFy = tileId == TileID.Platforms ? (short)(item.placeStyle * 18) : (short)0;
                _world.SetTile(tx, ty, tileId, active: true, frameX: initFx, frameY: initFy);
                TileFraming.FrameNeighborhood(_world, tx, ty);
                _worldCtrl.NotifyLightingChangedAround(tx, ty);
            }
            else if (item.createWall > 0)
            {
                if (_world.GetWallType(tx, ty) == item.createWall) return;
                if (!HasAnchorNeighbor(tx, ty)) return;
                if (!_inventory.ConsumeSelected(1)) return;

                _world.SetWall(tx, ty, (ushort)item.createWall);
                TileFraming.FrameNeighborhood(_world, tx, ty);
                _worldCtrl.NotifyLightingChangedAround(tx, ty);
            }
        }

        private bool TryToggleDoor(int tx, int ty)
        {
            if (!_world.InBounds(tx, ty) || !_world.IsActive(tx, ty)) return false;
            ushort type = _world.GetTileType(tx, ty);
            if (type != TileID.ClosedDoor && type != TileID.OpenDoor) return false;

            int idx = _world.Index(tx, ty);
            int dy = (_world.FrameY[idx] / 18) % 3;
            int style = _world.FrameY[idx] / 54;
            int topY = ty - dy;
            if (!_world.InBounds(tx, topY) || !_world.InBounds(tx, topY + 2)) return false;

            if (type == TileID.ClosedDoor)
            {
                int dir = _player.Center.x < tx + 0.5f ? 1 : -1;
                int swingX = tx + dir;
                if (!_world.InBounds(swingX, topY) || !_world.InBounds(swingX, topY + 2)) return false;
                for (int r = 0; r < 3; r++)
                {
                    if (_world.IsActive(swingX, topY + r) && !TileRules.IsCuttablePlant(_world.GetTileType(swingX, topY + r)))
                        return false;
                }

                int leftX = dir > 0 ? tx : swingX;
                short baseFx = (short)(dir > 0 ? 0 : 36);
                for (int r = 0; r < 3; r++)
                {
                    short fy = (short)(style * 54 + r * 18);
                    _world.SetTile(leftX, topY + r, TileID.OpenDoor, active: true, frameX: baseFx, frameY: fy);
                    _world.SetTile(leftX + 1, topY + r, TileID.OpenDoor, active: true, frameX: (short)(baseFx + 18), frameY: fy);
                    _world.MarkTileAndNeighborsDirty(leftX, topY + r);
                    _world.MarkTileAndNeighborsDirty(leftX + 1, topY + r);
                }
                _worldCtrl.NotifyLightingChangedAround(tx, ty);
                return true;
            }
            else
            {
                short fx = _world.FrameX[idx];
                int dx = (fx / 18) % 2;
                int leftX = tx - dx;
                int hingeX = fx < 36 ? leftX : leftX + 1;
                int swingX = fx < 36 ? leftX + 1 : leftX;

                // Do not close door onto the player
                for (int r = 0; r < 3; r++)
                {
                    if (OverlapsPlayer(hingeX, topY + r))
                        return false;
                }

                for (int r = 0; r < 3; r++)
                {
                    short fy = (short)(style * 54 + r * 18);
                    _world.ClearTile(swingX, topY + r);
                    _world.SetTile(hingeX, topY + r, TileID.ClosedDoor, active: true, frameX: 0, frameY: fy);
                    _world.MarkTileAndNeighborsDirty(swingX, topY + r);
                    _world.MarkTileAndNeighborsDirty(hingeX, topY + r);
                }
                _worldCtrl.NotifyLightingChangedAround(hingeX, ty);
                return true;
            }
        }

        private bool HasAnchorNeighbor(int tx, int ty)
        {
            return _world.IsActive(tx - 1, ty) || _world.IsActive(tx + 1, ty) ||
                   _world.IsActive(tx, ty - 1) || _world.IsActive(tx, ty + 1) ||
                   _world.GetWallType(tx, ty) > 0 ||
                   _world.GetWallType(tx - 1, ty) > 0 || _world.GetWallType(tx + 1, ty) > 0 ||
                   _world.GetWallType(tx, ty - 1) > 0 || _world.GetWallType(tx, ty + 1) > 0;
        }

        private bool OverlapsPlayer(int tx, int ty)
        {
            Vector3 p = _player.transform.position;
            float pLeft = p.x - PlayerController2D.HalfWidth + 0.05f;
            float pRight = p.x + PlayerController2D.HalfWidth - 0.05f;
            float pBottom = p.y + 0.05f;
            float pTop = p.y + PlayerController2D.Height - 0.05f;

            float tLeft = tx;
            float tRight = tx + 1f;
            float tBottom = -ty - 1f;
            float tTop = -ty;

            return pLeft < tRight && pRight > tLeft && pBottom < tTop && pTop > tBottom;
        }

        private void BuildCursorIndicator()
        {
            var go = new GameObject("TileCursorBox");
            go.transform.SetParent(transform, false);
            _cursorBox = go.AddComponent<LineRenderer>();
            _cursorBox.loop = true;
            _cursorBox.positionCount = 4;
            _cursorBox.useWorldSpace = true;
            _cursorBox.startWidth = 0.07f;
            _cursorBox.endWidth = 0.07f;
            _cursorBox.material = new Material(Shader.Find("Sprites/Default"));
            _cursorBox.sortingOrder = 25;
        }
    }
}
