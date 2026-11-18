using System.IO;
using TerrariaMod.Data;
using TerrariaMod.Player;
using TerrariaMod.SaveIO;
using TerrariaMod.UI;
using TerrariaMod.World;
using UnityEngine;

namespace TerrariaMod.Core
{
    /// <summary>
    /// Single-entry Phase 3 runtime orchestrator:
    /// loads ItemDatabase & GameContentCatalog, initializes WorldMap from persistent save or
    /// bundled tutorial/World.bytes, wires PlayerController2D + Interaction + GameHUD + SaveIO.
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        public GameContentCatalog catalog;
        public Camera mainCamera;

        [Header("Camera")]
        public float defaultOrthoSize = 14f;
        public float minOrthoSize = 7f;
        public float maxOrthoSize = 26f;
        public float cameraSmooth = 10f;

        private ItemDatabase _itemDb;
        private RecipeDatabase _recipeDb;
        private InventoryModel _inventory;
        private WorldMap _world;
        private PlayerSaveData _playerSave;

        private TerrariaWorldController _worldCtrl;
        private TerrariaParallaxBackground _parallaxBg;
        private PlayerController2D _player;
        private PlayerInteraction _interaction;
        private GameHUD _hud;

        public WorldMap ActiveWorld => _world;
        public PlayerSaveData ActivePlayerSave => _playerSave;

        private string SaveRoot => Path.Combine(Application.persistentDataPath, "TerrariaModSaves");
        private string WorldSavePath => Path.Combine(SaveRoot, "ActiveWorld.wld");
        private string PlayerSavePath => Path.Combine(SaveRoot, "ActivePlayer.plr");

        private void Start()
        {
            if (catalog == null)
                catalog = Resources.Load<GameContentCatalog>("GameContentCatalog");

            if (mainCamera == null)
                mainCamera = Camera.main;
            if (mainCamera != null)
            {
                mainCamera.orthographic = true;
                mainCamera.orthographicSize = defaultOrthoSize;
                mainCamera.backgroundColor = new Color(0.39f, 0.62f, 0.94f, 1f);
            }

            InitializeItemDatabase();
            CreateSubSystems();
            LoadOrCreateSession(preferPersistentSave: true);
        }

        private void LateUpdate()
        {
            if (mainCamera == null || _player == null) return;

            // Mouse wheel zooms view cleanly unless Shift is held to scroll hotbar
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.05f && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
            {
                mainCamera.orthographicSize = Mathf.Clamp(
                    mainCamera.orthographicSize - scroll * 1.5f,
                    minOrthoSize,
                    maxOrthoSize);
            }

            Vector3 target = new Vector3(_player.transform.position.x, _player.transform.position.y + 1.5f, -10f);
            if (_world != null)
            {
                float halfH = mainCamera.orthographicSize;
                float halfW = halfH * mainCamera.aspect;
                target.x = Mathf.Clamp(target.x, halfW, Mathf.Max(halfW, _world.Width - halfW));
                target.y = Mathf.Clamp(target.y, -(_world.Height - halfH), -halfH);
            }
            mainCamera.transform.position = Vector3.Lerp(mainCamera.transform.position, target, 1f - Mathf.Exp(-cameraSmooth * Time.deltaTime));
            _worldCtrl?.SyncToCamera();
            _parallaxBg?.SyncToCamera();
        }

        public void SaveSession()
        {
            if (_world == null || _playerSave == null) return;
            Directory.CreateDirectory(SaveRoot);
            _inventory.ExportToSave(_playerSave);
            WorldSaveIO.SaveWorld(_world, WorldSavePath);
            PlayerSaveIO.SaveFullPlayer(_playerSave, PlayerSavePath);
            _hud?.ShowToast($"已保存存档 ({_world.WorldName})");
        }

        public void ReloadSession()
        {
            if (!File.Exists(WorldSavePath) || !File.Exists(PlayerSavePath))
            {
                _hud?.ShowToast("尚无本地存档，已为你先保存当前状态");
                SaveSession();
                return;
            }
            LoadOrCreateSession(preferPersistentSave: true);
            _hud?.ShowToast("已从本地 .wld / .plr 重读存档");
        }

        public void ResetToTutorial()
        {
            LoadOrCreateSession(preferPersistentSave: false);
            _hud?.ShowToast("已重置为原版 Tutorial 初始存档");
        }

        public void ToggleDayNight()
        {
            if (_world == null) return;
            _world.DayTime = !_world.DayTime;
            _world.Time = _world.DayTime ? 27000.0 : 8100.0; // Noon (12:00) <-> Night (21:45)
            if (_worldCtrl != null && _worldCtrl.Lighting != null)
                _worldCtrl.Lighting.TimeStepDirty = true;
            _hud?.ShowToast(_world.DayTime ? "已切换至白天 (12:00)" : "已切换至夜晚 (21:45)");
        }

        public void BuildTestWaterPoolNearPlayer()
        {
            if (_world == null || _player == null) return;
            int ptx = WorldMap.WorldToTileX(_player.Center.x);
            int pty = WorldMap.WorldToTileY(_player.Center.y);

            // Build a deep 15x12 stone-lined test basin in front of the player:
            // - Top chamber: 13x5 deep Water (LiquidType=0)
            // - Bottom-left chamber: 6x4 deep Honey (LiquidType=2)
            // - Bottom-right chamber: 6x4 deep Lava (LiquidType=1)
            int left = Mathf.Clamp(ptx + 5, 2, _world.Width - 18);
            int top = Mathf.Clamp(pty - 3, 5, _world.Height - 16);

            for (int x = left; x <= left + 14; x++)
            {
                for (int y = top; y <= top + 11; y++)
                {
                    bool isOuterWall = x == left || x == left + 14 || y == top + 11;
                    bool isDividerFloor = y == top + 6;
                    bool isMidDivider = y >= top + 7 && x == left + 7;
                    bool isWaterInterior = y >= top + 1 && y <= top + 5 && x >= left + 1 && x <= left + 13;
                    bool isHoneyInterior = y >= top + 7 && y <= top + 10 && x >= left + 1 && x <= left + 6;
                    bool isLavaInterior = y >= top + 7 && y <= top + 10 && x >= left + 8 && x <= left + 13;

                    if (isOuterWall || isDividerFloor || isMidDivider)
                    {
                        _world.SetTile(x, y, TerrariaMod.ID.TileID.Stone, active: true);
                        int idx = _world.Index(x, y);
                        _world.Liquid[idx] = 0;
                        _world.LiquidType[idx] = 0;
                    }
                    else if (isWaterInterior)
                    {
                        _world.ClearTile(x, y);
                        int idx = _world.Index(x, y);
                        _world.Liquid[idx] = 255;
                        _world.LiquidType[idx] = 0; // Water
                    }
                    else if (isHoneyInterior)
                    {
                        _world.ClearTile(x, y);
                        int idx = _world.Index(x, y);
                        _world.Liquid[idx] = 255;
                        _world.LiquidType[idx] = 2; // Honey
                    }
                    else if (isLavaInterior)
                    {
                        _world.ClearTile(x, y);
                        int idx = _world.Index(x, y);
                        _world.Liquid[idx] = 255;
                        _world.LiquidType[idx] = 1; // Lava
                    }
                    else
                    {
                        _world.ClearTile(x, y);
                        int idx = _world.Index(x, y);
                        _world.Liquid[idx] = 0;
                        _world.LiquidType[idx] = 0;
                    }
                    TileFraming.FrameNeighborhood(_world, x, y);
                }
            }

            _worldCtrl?.NotifyLightingChangedAround(left + 7, top + 6);
            _hud?.ShowToast("已在前方生成 15×12 深水/蜂蜜/岩浆大测试池");
        }

        private void InitializeItemDatabase()
        {
            _itemDb = new ItemDatabase();
            if (catalog != null && catalog.itemsJson != null)
            {
                _itemDb.LoadFromJson(catalog.itemsJson.text);
            }
            else
            {
                string fallback = Path.Combine(Application.dataPath, "Data/Items.json");
                if (File.Exists(fallback))
                    _itemDb.LoadFromFile(fallback);
            }
            TileRules.BuildDropMappings(_itemDb);

            _recipeDb = new RecipeDatabase();
            if (catalog != null && catalog.recipesJson != null)
            {
                _recipeDb.LoadFromJson(catalog.recipesJson.text);
            }
            else
            {
                string recipeFallback = Path.Combine(Application.dataPath, "Data/Recipes.json");
                if (File.Exists(recipeFallback))
                    _recipeDb.LoadFromFile(recipeFallback);
            }

            _inventory = new InventoryModel(_itemDb);
        }

        private void CreateSubSystems()
        {
            var worldGo = new GameObject("WorldController");
            _worldCtrl = worldGo.AddComponent<TerrariaWorldController>();

            var bgGo = new GameObject("ParallaxBackground");
            _parallaxBg = bgGo.AddComponent<TerrariaParallaxBackground>();

            var playerGo = new GameObject("Player");
            _player = playerGo.AddComponent<PlayerController2D>();
            _interaction = playerGo.AddComponent<PlayerInteraction>();

            var hudGo = new GameObject("HUD");
            _hud = hudGo.AddComponent<GameHUD>();
        }

        private void LoadOrCreateSession(bool preferPersistentSave)
        {
            string sourceLabel;
            if (preferPersistentSave && File.Exists(WorldSavePath))
            {
                _world = WorldSaveIO.LoadWorld(WorldSavePath);
                sourceLabel = WorldSavePath;
            }
            else if (catalog != null && catalog.tutorialWorldBytes != null)
            {
                _world = WorldSaveIO.LoadWorld(catalog.tutorialWorldBytes.bytes);
                sourceLabel = "GameContentCatalog.tutorialWorldBytes";
            }
            else
            {
                var tutRes = Resources.Load<TextAsset>("tutorial/World");
                _world = tutRes != null ? WorldSaveIO.LoadWorld(tutRes.bytes) : WorldMap.CreateStarterSurface();
                sourceLabel = tutRes != null ? "Resources/tutorial/World" : "StarterSurface";
            }
            Debug.Log($"[GameBootstrap] Loaded world '{_world.WorldName}' from {sourceLabel}");

            if (preferPersistentSave && File.Exists(PlayerSavePath))
            {
                _playerSave = PlayerSaveIO.LoadFullPlayer(PlayerSavePath);
            }
            else if (catalog != null && catalog.tutorialPlayerBytes != null)
            {
                _playerSave = PlayerSaveIO.LoadFullPlayer(catalog.tutorialPlayerBytes.bytes);
            }
            else
            {
                var plrRes = Resources.Load<TextAsset>("tutorial/Player");
                _playerSave = plrRes != null
                    ? PlayerSaveIO.LoadFullPlayer(plrRes.bytes)
                    : new PlayerSaveData { Name = "ModPlayer", StatLife = 100, StatLifeMax = 100, StatMana = 20, StatManaMax = 20 };
            }

            _inventory.ImportFromSave(_playerSave);
            if (!_inventory.HasAnyItems())
                _inventory.GrantStarterKit();

            _worldCtrl.Initialize(_world, catalog, _itemDb, mainCamera);
            _player.Initialize(_world, _inventory, catalog);
            _worldCtrl.SetPlayerLightSource(_player.transform, _inventory);
            _interaction.Initialize(_world, _worldCtrl, _inventory, _player, mainCamera, catalog);
            _hud.Initialize(this, _inventory, _itemDb, _recipeDb, catalog, _player, _world);

            if (mainCamera != null)
                mainCamera.transform.position = new Vector3(_player.transform.position.x, _player.transform.position.y + 1.5f, -10f);

            _parallaxBg?.Initialize(_world, catalog, mainCamera);
            BuildTestWaterPoolNearPlayer();
            _worldCtrl.ForceRebuildVisibleNow();
        }
    }
}
