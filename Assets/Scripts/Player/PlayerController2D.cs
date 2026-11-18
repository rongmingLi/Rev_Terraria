using System.Collections.Generic;
using TerrariaMod.Data;
using TerrariaMod.ID;
using TerrariaMod.World;
using UnityEngine;

namespace TerrariaMod.Player
{
    /// <summary>
    /// Terraria-tuned 2D character controller using swept AABB tile collision
    /// (supports 1-tile auto-step, one-way platform drop-through, keyboard + mobile virtual input).
    /// </summary>
    public class PlayerController2D : MonoBehaviour
    {
        // Hitbox size in world tile units (20px x 42px @ 16 PPU)
        public const float HalfWidth = 0.625f;
        public const float Height = 2.625f;

        public float moveSpeed = 11.5f;
        public float acceleration = 55f;
        public float jumpVelocity = 16.2f;
        public float gravity = 42f;
        public float maxFallSpeed = 26f;

        private WorldMap _world;
        private InventoryModel _inventory;
        private GameContentCatalog _catalog;

        private Vector2 _velocity;
        private bool _isGrounded;
        private int _facing = 1;
        private float _dropThroughTimer;
        private float _walkAnimTime;
        private float _swingTimer;
        private float _swingDuration = 0.28f;

        // Virtual touch inputs driven by GameHUD on mobile or Touch toggle
        private float _virtualMoveX;
        private bool _virtualJumpHeld;
        private bool _virtualJumpPressed;
        private bool _virtualDownHeld;

        private Transform _visualRoot;
        private Transform _bobRoot;
        private SpriteRenderer _backHandSr;
        private SpriteRenderer _backSleeveSr;
        private SpriteRenderer _legsSr;
        private SpriteRenderer _shoesSr;
        private SpriteRenderer _underTorsoSr;
        private SpriteRenderer _torsoSr;
        private SpriteRenderer _headSr;
        private SpriteRenderer _eyeWhiteSr;
        private SpriteRenderer _eyeIrisSr;
        private SpriteRenderer _hairSr;
        private SpriteRenderer _frontArmSkinSr;
        private SpriteRenderer _frontSleeveSr;
        private SpriteRenderer _frontHandSr;
        private SpriteRenderer _heldItemSr;
        private Transform _heldPivot;

        private readonly List<Sprite> _headFrames = new List<Sprite>();
        private readonly List<Sprite> _eyeWhiteFrames = new List<Sprite>();
        private readonly List<Sprite> _eyeIrisFrames = new List<Sprite>();
        private readonly List<Sprite> _legFrames = new List<Sprite>();
        private readonly List<Sprite> _shoeFrames = new List<Sprite>();

        private Sprite[,] _frontArmSkinGrid;
        private Sprite[,] _frontSleeveGrid;
        private Sprite[,] _frontHandGrid;
        private Sprite[,] _backHandGrid;
        private Sprite[,] _backSleeveGrid;
        private Sprite _underTorsoSprite;
        private Sprite _torsoSprite;
        private Sprite _hairSprite;

        public bool IsGrounded => _isGrounded;
        public int Facing => _facing;
        public Vector2 Center => new Vector2(transform.position.x, transform.position.y + Height * 0.5f);

        public void Initialize(WorldMap world, InventoryModel inventory, GameContentCatalog catalog)
        {
            if (_inventory != null)
                _inventory.Changed -= RefreshHeldItemSprite;

            _world = world;
            _inventory = inventory;
            _catalog = catalog;
            if (_inventory != null)
                _inventory.Changed += RefreshHeldItemSprite;

            BuildCharacterVisuals();
            RefreshHeldItemSprite();
            TeleportToSpawn();
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.Changed -= RefreshHeldItemSprite;
        }

        public void SetVirtualMove(float moveX) => _virtualMoveX = Mathf.Clamp(moveX, -1f, 1f);
        public void SetVirtualJump(bool held)
        {
            if (held && !_virtualJumpHeld) _virtualJumpPressed = true;
            _virtualJumpHeld = held;
        }
        public void SetVirtualDown(bool held) => _virtualDownHeld = held;

        public void SetFacing(int dir)
        {
            if (dir != 0)
                _facing = dir > 0 ? 1 : -1;
        }

        public void TriggerSwing(float duration = 0.25f)
        {
            _swingDuration = Mathf.Max(0.12f, duration);
            _swingTimer = _swingDuration;
        }

        public void TeleportToSpawn()
        {
            if (_world == null) return;
            float sx = WorldMap.TileToWorldX(_world.SpawnX);
            // Place feet safely on top of SpawnY tile
            float sy = -_world.SpawnY + 0.1f;
            // Lift out if spawn tile itself happens to be solid
            for (int step = 0; step < 30; step++)
            {
                if (!OverlapsSolid(new Vector2(sx, sy + step)))
                {
                    sy += step;
                    break;
                }
            }
            transform.position = new Vector3(sx, sy, -0.1f);
            _velocity = Vector2.zero;
        }

        private void Update()
        {
            if (_world == null) return;

            float dt = Mathf.Min(Time.deltaTime, 0.04f);
            UpdateMovement(dt);
            CollectNearbyDrops(dt);
        }

        private void LateUpdate()
        {
            if (_world == null) return;
            UpdateVisuals(Mathf.Min(Time.deltaTime, 0.04f));
        }

        private void UpdateMovement(float dt)
        {
            float inputX = Input.GetAxisRaw("Horizontal");
            bool leftHeld = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
            bool rightHeld = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
            if (leftHeld != rightHeld)
                inputX = rightHeld ? 1f : -1f;
            if (Mathf.Abs(_virtualMoveX) > 0.05f) inputX = _virtualMoveX;

            if (_swingTimer <= 0f && Mathf.Abs(inputX) > 0.05f)
                _facing = inputX > 0 ? 1 : -1;

            float targetVx = inputX * moveSpeed;
            float accel = _isGrounded ? acceleration : acceleration * 0.72f;
            _velocity.x = Mathf.MoveTowards(_velocity.x, targetVx, accel * dt);

            bool downHeld = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) || _virtualDownHeld;
            if (downHeld && _isGrounded)
                _dropThroughTimer = 0.18f;
            else if (_dropThroughTimer > 0f)
                _dropThroughTimer -= dt;

            bool jumpPressed = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow) || _virtualJumpPressed;
            bool jumpHeld = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) || _virtualJumpHeld;
            _virtualJumpPressed = false;

            if (jumpPressed && _isGrounded)
            {
                _velocity.y = jumpVelocity;
                _isGrounded = false;
            }

            if (!jumpHeld && _velocity.y > 4f)
                _velocity.y -= gravity * 0.9f * dt;

            _velocity.y = Mathf.Max(-maxFallSpeed, _velocity.y - gravity * dt);

            Vector2 pos = transform.position;

            // Horizontal move + 1-tile Terraria auto-step
            float targetX = pos.x + _velocity.x * dt;
            if (!OverlapsSolid(new Vector2(targetX, pos.y)))
            {
                pos.x = targetX;
            }
            else if (_isGrounded && !OverlapsSolid(new Vector2(targetX, pos.y + 1.05f)))
            {
                // Auto-step smoothly up a 1-block ledge without jumping
                pos.x = targetX;
                pos.y = Mathf.Ceil(pos.y + 0.01f);
            }
            else
            {
                _velocity.x = 0f;
            }

            // Swept vertical move (prevents 1-frame ground skin oscillation/jitter)
            float targetY = pos.y + _velocity.y * dt;
            int minX = WorldMap.WorldToTileX(pos.x - HalfWidth + 0.04f);
            int maxX = WorldMap.WorldToTileX(pos.x + HalfWidth - 0.04f);

            if (_velocity.y <= 0f)
            {
                bool hitFloor = false;
                int startTy = WorldMap.WorldToTileY(pos.y - 0.002f);
                int endTy = WorldMap.WorldToTileY(targetY - 0.002f);

                for (int ty = startTy; ty <= endTy; ty++)
                {
                    for (int tx = minX; tx <= maxX; tx++)
                    {
                        bool solid = TileRules.IsSolid(_world, tx, ty);
                        bool platform = !solid && _dropThroughTimer <= 0f && pos.y >= -ty - 0.12f && TileRules.IsSolidTop(_world, tx, ty);
                        if (solid || platform)
                        {
                            pos.y = -ty;
                            _velocity.y = 0f;
                            _isGrounded = true;
                            hitFloor = true;
                            break;
                        }
                    }
                    if (hitFloor) break;
                }

                if (!hitFloor)
                {
                    pos.y = targetY;
                    _isGrounded = false;
                }
            }
            else
            {
                bool hitCeil = false;
                int startTy = WorldMap.WorldToTileY(pos.y + Height - 0.002f);
                int endTy = WorldMap.WorldToTileY(targetY + Height + 0.002f);

                for (int ty = startTy; ty >= endTy; ty--)
                {
                    for (int tx = minX; tx <= maxX; tx++)
                    {
                        if (TileRules.IsSolid(_world, tx, ty))
                        {
                            pos.y = -(ty + 1) - Height;
                            _velocity.y = 0f;
                            hitCeil = true;
                            break;
                        }
                    }
                    if (hitCeil) break;
                }

                if (!hitCeil)
                    pos.y = targetY;
                _isGrounded = false;
            }

            pos.x = Mathf.Clamp(pos.x, 1f, _world.Width - 1f);
            pos.y = Mathf.Clamp(pos.y, -(_world.Height - 2f), -1f);
            transform.position = new Vector3(pos.x, pos.y, -0.1f);
        }

        private bool OverlapsSolid(Vector2 feetPos)
        {
            int minX = WorldMap.WorldToTileX(feetPos.x - HalfWidth + 0.04f);
            int maxX = WorldMap.WorldToTileX(feetPos.x + HalfWidth - 0.04f);
            int minY = WorldMap.WorldToTileY(feetPos.y + Height - 0.02f);
            int maxY = WorldMap.WorldToTileY(feetPos.y + 0.02f);

            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    if (TileRules.IsSolid(_world, x, y))
                        return true;
            return false;
        }

        private void CollectNearbyDrops(float dt)
        {
            if (_inventory == null) return;
            Vector2 center = Center;
            for (int i = WorldItemDrop.ActiveDrops.Count - 1; i >= 0; i--)
            {
                WorldItemDrop drop = WorldItemDrop.ActiveDrops[i];
                if (drop == null || !drop.CanBePickedUp) continue;

                if (!_inventory.CanAcceptAny(drop.ItemId))
                    continue;

                float dist = Vector2.Distance(center, drop.transform.position);
                if (dist < 1.15f)
                {
                    int left = _inventory.AddItem(drop.ItemId, drop.Stack);
                    if (left <= 0)
                        Destroy(drop.gameObject);
                    else if (left < drop.Stack)
                        drop.SetStack(left);
                }
                else if (dist < 6.0f)
                {
                    drop.PullToward(center, dt);
                }
            }
        }

        private void BuildCharacterVisuals()
        {
            if (_visualRoot != null) return;

            _visualRoot = new GameObject("Visuals").transform;
            _visualRoot.SetParent(transform, false);
            _visualRoot.localPosition = Vector3.zero;

            _bobRoot = new GameObject("BobRoot").transform;
            _bobRoot.SetParent(_visualRoot, false);
            _bobRoot.localPosition = Vector3.zero;

            // Render order matches vanilla Terraria PlayerDrawLayers
            _backHandSr = CreateLayerRenderer("BackHand", _bobRoot, 8);
            _backSleeveSr = CreateLayerRenderer("BackSleeve", _bobRoot, 9);
            _legsSr = CreateLayerRenderer("Legs", _visualRoot, 10);
            _shoesSr = CreateLayerRenderer("Shoes", _visualRoot, 11);
            _underTorsoSr = CreateLayerRenderer("UnderTorso", _bobRoot, 12);
            _torsoSr = CreateLayerRenderer("Torso", _bobRoot, 13);
            _headSr = CreateLayerRenderer("Head", _visualRoot, 14);
            _eyeWhiteSr = CreateLayerRenderer("EyeWhite", _visualRoot, 15);
            _eyeIrisSr = CreateLayerRenderer("EyeIris", _visualRoot, 16);
            _hairSr = CreateLayerRenderer("Hair", _bobRoot, 17);

            _heldPivot = new GameObject("HeldItemPivot").transform;
            _heldPivot.SetParent(_bobRoot, false);
            _heldPivot.localPosition = new Vector3(0.35f, 1.55f, -0.05f);

            var heldGo = new GameObject("HeldItem");
            heldGo.transform.SetParent(_heldPivot, false);
            heldGo.transform.localPosition = new Vector3(0.45f, 0.45f, 0f);
            _heldItemSr = heldGo.AddComponent<SpriteRenderer>();
            _heldItemSr.sortingOrder = 18;

            _frontArmSkinSr = CreateLayerRenderer("FrontArmSkin", _bobRoot, 19);
            _frontSleeveSr = CreateLayerRenderer("FrontSleeve", _bobRoot, 20);
            _frontHandSr = CreateLayerRenderer("FrontHand", _bobRoot, 21);

            if (_catalog != null)
            {
                SliceVerticalFrames(_catalog.GetPlayerTexture("Player_0_0"), _headFrames);
                SliceVerticalFrames(_catalog.GetPlayerTexture("Player_0_1"), _eyeWhiteFrames);
                SliceVerticalFrames(_catalog.GetPlayerTexture("Player_0_2"), _eyeIrisFrames);
                SliceVerticalFrames(_catalog.GetPlayerTexture("Player_0_10"), _legFrames);
                SliceVerticalFrames(_catalog.GetPlayerTexture("Player_0_12"), _shoeFrames);

                _underTorsoSprite = SliceCell(_catalog.GetPlayerTexture("Player_0_6"), 0, 0);
                _torsoSprite = SliceCell(_catalog.GetPlayerTexture("Player_0_3"), 0, 0);
                _hairSprite = SliceCell(_catalog.GetPlayerTexture("Player_Hair_1"), 0, 0);

                _frontArmSkinGrid = SliceSheetGrid(_catalog.GetPlayerTexture("Player_0_7"), 9, 4);
                _frontSleeveGrid = SliceSheetGrid(_catalog.GetPlayerTexture("Player_0_4"), 9, 4);
                _frontHandGrid = SliceSheetGrid(_catalog.GetPlayerTexture("Player_0_5"), 9, 4);
                _backSleeveGrid = SliceSheetGrid(_catalog.GetPlayerTexture("Player_0_8"), 9, 4);
                _backHandGrid = SliceSheetGrid(_catalog.GetPlayerTexture("Player_0_9"), 9, 4);
            }

            Color skinCol = new Color(1f, 0.82f, 0.69f, 1f);
            Color shirtCol = new Color(0.76f, 0.45f, 0.33f, 1f);
            Color underCol = new Color(0.61f, 0.33f, 0.25f, 1f);
            Color pantsCol = new Color(0.25f, 0.37f, 0.65f, 1f);
            Color shoeCol = new Color(0.30f, 0.22f, 0.18f, 1f);
            Color hairCol = new Color(0.71f, 0.37f, 0.18f, 1f);
            Color eyeCol = new Color(0.20f, 0.30f, 0.52f, 1f);

            _backHandSr.color = skinCol;
            _backSleeveSr.color = underCol;
            _legsSr.color = pantsCol;
            _shoesSr.color = shoeCol;
            _underTorsoSr.color = underCol;
            _torsoSr.color = shirtCol;
            _headSr.color = skinCol;
            _eyeWhiteSr.color = Color.white;
            _eyeIrisSr.color = eyeCol;
            _hairSr.color = hairCol;
            _frontArmSkinSr.color = skinCol;
            _frontSleeveSr.color = shirtCol;
            _frontHandSr.color = skinCol;

            _underTorsoSr.sprite = _underTorsoSprite;
            _torsoSr.sprite = _torsoSprite;
            _hairSr.sprite = _hairSprite;
        }

        private SpriteRenderer CreateLayerRenderer(string layerName, Transform parent, int order)
        {
            var go = new GameObject(layerName);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = order;
            return sr;
        }

        private static void SliceVerticalFrames(Texture2D tex, List<Sprite> dst)
        {
            dst.Clear();
            if (tex == null) return;
            int rows = (tex.height + 55) / 56;
            for (int r = 0; r < rows; r++)
                dst.Add(SliceCell(tex, 0, r));
        }

        private static Sprite[,] SliceSheetGrid(Texture2D tex, int cols, int rows)
        {
            var grid = new Sprite[cols, rows];
            if (tex == null) return grid;
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    grid[c, r] = SliceCell(tex, c, r);
            return grid;
        }

        private static Sprite SliceCell(Texture2D tex, int col, int row)
        {
            if (tex == null) return null;
            int x = col * 40;
            int yFromTop = row * 56;
            if (x < 0 || x + 40 > tex.width || yFromTop >= tex.height) return null;
            int h = Mathf.Min(56, tex.height - yFromTop);
            int yFromBot = tex.height - yFromTop - h;
            if (yFromBot < 0 || h <= 0) return null;
            // Align shoe-bottom pixel row (y=54 from cell top) cleanly to local y=0
            float pivotY = Mathf.Clamp01((h - 54f) / h);
            return Sprite.Create(tex, new Rect(x, yFromBot, 40, h), new Vector2(0.5f, pivotY), 16f);
        }

        private void RefreshHeldItemSprite()
        {
            if (_heldItemSr == null || _inventory == null || _catalog == null) return;
            var slot = _inventory.SelectedSlot;
            _heldItemSr.sprite = slot.IsEmpty ? null : _catalog.GetItemSprite(slot.itemId);
            var def = _inventory.GetSelectedItemDef();
            bool smallBlock = def != null && (def.createTile >= 0 || def.createWall > 0) && def.pick <= 0 && def.axe <= 0;
            bool holdFront = def != null && (def.holdStyle == 1 || def.createTile == TileID.Torches);
            _heldItemSr.transform.localScale = (smallBlock && !holdFront) ? new Vector3(0.65f, 0.65f, 1f) : Vector3.one;

            // Anchor bottom-left handle of the held sprite directly at _heldPivot (player's palm)
            if (_heldItemSr.sprite != null)
            {
                Vector3 ext = _heldItemSr.sprite.bounds.extents;
                float scale = _heldItemSr.transform.localScale.x;
                _heldItemSr.transform.localPosition = new Vector3(ext.x * scale * 0.78f, ext.y * scale * 0.78f, 0f);
            }
            _heldItemSr.enabled = _heldItemSr.sprite != null && (_swingTimer > 0f || holdFront);
        }

        private void UpdateVisuals(float dt)
        {
            if (_visualRoot == null) return;
            _visualRoot.localScale = new Vector3(_facing, 1f, 1f);

            int bodyRow = 0;
            int frontArmCol = 2, frontArmRow = 0;
            int backArmCol = 2, backArmRow = 0;

            if (!_isGrounded)
            {
                bodyRow = 5;
                frontArmCol = 2; frontArmRow = 1;
                backArmCol = 2; backArmRow = 1;
            }
            else if (Mathf.Abs(_velocity.x) > 0.35f)
            {
                _walkAnimTime += dt * Mathf.Abs(_velocity.x) * 1.45f;
                int walkStep = (int)_walkAnimTime % 14;
                bodyRow = 6 + walkStep;

                // Alternate swinging arm frames smoothly across forward/back stride phases
                if (bodyRow >= 7 && bodyRow <= 9)
                {
                    frontArmCol = 7;
                    backArmCol = 8;
                }
                else if (bodyRow >= 14 && bodyRow <= 16)
                {
                    frontArmCol = 8;
                    backArmCol = 7;
                }
            }
            else
            {
                _walkAnimTime = 0f;
                bodyRow = 0;
            }

            // Rows 7..9 and 14..16 shift head 2px higher inside Player_0_0..0_2; sync torso/hair/arms
            bool bobUp = (bodyRow >= 7 && bodyRow <= 9) || (bodyRow >= 14 && bodyRow <= 16);
            _bobRoot.localPosition = new Vector3(0f, bobUp ? (2f / 16f) : 0f, 0f);

            if (_swingTimer > 0f)
            {
                _swingTimer = Mathf.Max(0f, _swingTimer - dt);
                float t = 1f - (_swingTimer / _swingDuration);
                int swingCol = Mathf.Clamp(3 + (int)(t * 4f), 3, 6);
                frontArmCol = swingCol;
                frontArmRow = 0;

                Vector2 handPos;
                switch (swingCol)
                {
                    case 3: handPos = new Vector2(-0.62f, 2.25f); break;
                    case 4: handPos = new Vector2(0.44f, 2.25f); break;
                    case 5: handPos = new Vector2(0.56f, 1.38f); break;
                    default: handPos = new Vector2(0.44f, 0.88f); break;
                }
                float toolAngle = Mathf.Lerp(85f, -90f, t);

                _heldPivot.localPosition = new Vector3(handPos.x, handPos.y, -0.05f);
                _heldPivot.localRotation = Quaternion.Euler(0f, 0f, toolAngle);
                _heldItemSr.enabled = _heldItemSr.sprite != null;
            }
            else
            {
                var def = _inventory != null ? _inventory.GetSelectedItemDef() : null;
                bool holdFront = def != null && (def.holdStyle == 1 || def.createTile == TileID.Torches);
                if (holdFront && _heldItemSr.sprite != null)
                {
                    frontArmCol = 5;
                    frontArmRow = 0;
                    _heldPivot.localPosition = new Vector3(0.56f, 1.38f, -0.05f);
                    _heldPivot.localRotation = Quaternion.Euler(0f, 0f, -15f);
                    _heldItemSr.enabled = true;
                }
                else
                {
                    _heldPivot.localRotation = Quaternion.identity;
                    _heldItemSr.enabled = false;
                }
            }

            SetListFrame(_headSr, _headFrames, bodyRow);
            SetListFrame(_eyeWhiteSr, _eyeWhiteFrames, bodyRow);
            SetListFrame(_eyeIrisSr, _eyeIrisFrames, bodyRow);
            SetListFrame(_legsSr, _legFrames, bodyRow);
            SetListFrame(_shoesSr, _shoeFrames, bodyRow);

            SetGridFrame(_frontArmSkinSr, _frontArmSkinGrid, frontArmCol, frontArmRow);
            SetGridFrame(_frontSleeveSr, _frontSleeveGrid, frontArmCol, frontArmRow);
            SetGridFrame(_frontHandSr, _frontHandGrid, frontArmCol, frontArmRow);
            SetGridFrame(_backSleeveSr, _backSleeveGrid, backArmCol, backArmRow);
            SetGridFrame(_backHandSr, _backHandGrid, backArmCol, backArmRow);
        }

        private static void SetListFrame(SpriteRenderer sr, List<Sprite> frames, int index)
        {
            if (sr == null || frames == null || frames.Count == 0) return;
            sr.sprite = frames[Mathf.Clamp(index, 0, frames.Count - 1)];
        }

        private static void SetGridFrame(SpriteRenderer sr, Sprite[,] grid, int col, int row)
        {
            if (sr == null || grid == null) return;
            if (col < 0 || col >= grid.GetLength(0) || row < 0 || row >= grid.GetLength(1)) return;
            sr.sprite = grid[col, row];
        }
    }
}
