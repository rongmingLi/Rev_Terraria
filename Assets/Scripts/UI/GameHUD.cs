using System;
using System.Collections.Generic;
using TerrariaMod.Core;
using TerrariaMod.Data;
using TerrariaMod.ID;
using TerrariaMod.Player;
using TerrariaMod.World;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TerrariaMod.UI
{
    /// <summary>
    /// Runtime-constructed uGUI HUD following an 8px grid:
    /// 10-slot hotbar, 50-slot expandable inventory with click-to-swap,
    /// item stats/Chinese tooltip, HP/Mana readout, Save/Load controls, and mobile touch pad.
    /// </summary>
    public class GameHUD : MonoBehaviour
    {
        private GameBootstrap _bootstrap;
        private InventoryModel _inventory;
        private ItemDatabase _itemDb;
        private RecipeDatabase _recipeDb;
        private GameContentCatalog _catalog;
        private PlayerController2D _player;
        private WorldMap _world;

        private Font _font;
        private bool _built;
        private GameObject _bagPanel;
        private GameObject _craftingPanel;
        private GameObject _touchPanel;
        private bool _bagOpen;
        private bool _touchVisible;
        private int _swapSourceIndex = -1;

        private readonly Image[] _slotBg = new Image[InventoryModel.MainSlotCount];
        private readonly Image[] _slotIcon = new Image[InventoryModel.MainSlotCount];
        private readonly Text[] _slotCount = new Text[InventoryModel.MainSlotCount];

        // Crafting UI state (6x3 = 18 recipes per page)
        private const int CraftPageSize = 18;
        private readonly Image[] _craftBg = new Image[CraftPageSize];
        private readonly Image[] _craftIcon = new Image[CraftPageSize];
        private readonly Text[] _craftCount = new Text[CraftPageSize];
        private readonly List<int> _availableRecipes = new List<int>();
        private readonly HashSet<int> _adjTiles = new HashSet<int>();
        private readonly Dictionary<int, int> _invCounts = new Dictionary<int, int>();
        private bool _nearWater, _nearHoney, _nearLava;
        private int _recipePage;
        private int _selectedAvailableIndex = -1;
        private int _lastEnvTx = int.MinValue;
        private int _lastEnvTy = int.MinValue;
        private float _envScanTimer;
        private Image _craftResultIcon;
        private Text _craftTitleText;
        private Text _craftStationText;
        private Text _craftIngredientsText;
        private Text _craftPageText;
        private Image _craftBtnBg;

        private Text _selectedLabel;
        private Text _statusLabel;
        private Text _toastLabel;
        private float _toastTimer;

        private static readonly Color SlateDark = new Color(0.08f, 0.11f, 0.16f, 0.90f);
        private static readonly Color SlateSlot = new Color(0.14f, 0.18f, 0.26f, 0.92f);
        private static readonly Color SlateButton = new Color(0.18f, 0.24f, 0.34f, 0.96f);
        private static readonly Color AmberSelect = new Color(0.92f, 0.70f, 0.16f, 0.98f);
        private static readonly Color SwapSelect = new Color(0.24f, 0.68f, 0.92f, 0.98f);
        private static readonly Color TextPrimary = new Color(0.95f, 0.96f, 0.98f, 1f);
        private static readonly Color TextMuted = new Color(0.70f, 0.76f, 0.84f, 1f);

        public void Initialize(
            GameBootstrap bootstrap,
            InventoryModel inventory,
            ItemDatabase itemDb,
            RecipeDatabase recipeDb,
            GameContentCatalog catalog,
            PlayerController2D player,
            WorldMap world)
        {
            if (_inventory != null)
                _inventory.Changed -= RefreshSlots;

            _bootstrap = bootstrap;
            _inventory = inventory;
            _itemDb = itemDb;
            _recipeDb = recipeDb;
            _catalog = catalog;
            _player = player;
            _world = world;
            _lastEnvTx = int.MinValue;
            _lastEnvTy = int.MinValue;

            if (!_built)
            {
                _font = ResolveFont();
                _touchVisible = Application.isMobilePlatform;
                BuildCanvas();
                _built = true;
            }

            if (_inventory != null)
                _inventory.Changed += RefreshSlots;
            RefreshSlots();
        }

        private void OnDestroy()
        {
            if (_inventory != null)
                _inventory.Changed -= RefreshSlots;
        }

        private void Update()
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
                EventSystem.current.SetSelectedGameObject(null);

            HandleHotkeys();
            UpdateStatusText();

            if (_bagOpen && _recipeDb != null && _player != null && _world != null)
            {
                _envScanTimer += Time.deltaTime;
                if (_envScanTimer >= 0.2f)
                {
                    _envScanTimer = 0f;
                    int tx = WorldMap.WorldToTileX(_player.Center.x);
                    int ty = WorldMap.WorldToTileY(_player.Center.y);
                    if (tx != _lastEnvTx || ty != _lastEnvTy)
                        RefreshCrafting(forceEnvScan: true);
                }
            }

            if (_toastTimer > 0f)
            {
                _toastTimer -= Time.deltaTime;
                if (_toastTimer <= 0f && _toastLabel != null)
                    _toastLabel.gameObject.SetActive(false);
            }
        }

        public void ShowToast(string message, float seconds = 2.5f)
        {
            if (_toastLabel == null) return;
            _toastLabel.text = message;
            _toastLabel.gameObject.SetActive(true);
            _toastTimer = seconds;
        }

        private void HandleHotkeys()
        {
            if (_inventory == null) return;

            for (int i = 0; i < 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    _inventory.SelectedIndex = i;
            }
            if (Input.GetKeyDown(KeyCode.Alpha0))
                _inventory.SelectedIndex = 9;

            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.05f && !Input.GetKey(KeyCode.LeftControl))
            {
                // Hold Shift + Scroll to cycle hotbar; normal scroll zooms camera in GameBootstrap
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    _inventory.SelectedIndex += scroll > 0 ? -1 : 1;
            }

            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.B) || Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.Escape))
            {
                ToggleBag();
            }
            if (Input.GetKeyDown(KeyCode.T))
            {
                _bootstrap?.ToggleDayNight();
            }
            if (Input.GetKeyDown(KeyCode.L))
            {
                _bootstrap?.BuildTestWaterPoolNearPlayer();
            }
        }

        private void ToggleBag()
        {
            _bagOpen = !_bagOpen;
            if (!_bagOpen) _swapSourceIndex = -1;
            if (_bagPanel != null) _bagPanel.SetActive(_bagOpen);
            if (_craftingPanel != null) _craftingPanel.SetActive(_bagOpen);
            RefreshSlots();
        }

        private void ToggleTouchControls()
        {
            _touchVisible = !_touchVisible;
            if (_touchPanel != null) _touchPanel.SetActive(_touchVisible);
        }

        private void OnSlotClicked(int index)
        {
            if (!_bagOpen && index < InventoryModel.HotbarCount)
            {
                _inventory.SelectedIndex = index;
                return;
            }

            if (_swapSourceIndex < 0)
            {
                _swapSourceIndex = index;
                if (index < InventoryModel.HotbarCount)
                    _inventory.SelectedIndex = index;
                RefreshSlots();
            }
            else
            {
                _inventory.SwapSlots(_swapSourceIndex, index);
                _swapSourceIndex = -1;
                RefreshSlots();
            }
        }

        private void RefreshSlots()
        {
            if (_inventory == null) return;

            for (int i = 0; i < InventoryModel.MainSlotCount; i++)
            {
                if (_slotBg[i] == null) continue;

                var slot = _inventory.GetSlot(i);
                bool isHotbarSel = i == _inventory.SelectedIndex;
                bool isSwapSel = i == _swapSourceIndex;

                _slotBg[i].color = isSwapSel ? SwapSelect : (isHotbarSel ? AmberSelect : SlateSlot);

                if (slot.IsEmpty)
                {
                    _slotIcon[i].enabled = false;
                    _slotCount[i].text = string.Empty;
                }
                else
                {
                    Sprite sp = _catalog != null ? _catalog.GetItemSprite(slot.itemId) : null;
                    _slotIcon[i].sprite = sp;
                    _slotIcon[i].enabled = sp != null;
                    _slotCount[i].text = slot.stack > 1 ? slot.stack.ToString() : string.Empty;
                }
            }

            if (_selectedLabel != null)
            {
                var def = _inventory.GetSelectedItemDef();
                if (def == null)
                {
                    _selectedLabel.text = "空手 | 按 1-0 切换快捷栏，Tab 打开背包";
                }
                else
                {
                    string displayName = !string.IsNullOrEmpty(def.nameZh) ? def.nameZh : def.internalName;
                    string stats = "";
                    if (def.pick > 0) stats += $" 镐力:{def.pick}";
                    if (def.axe > 0) stats += $" 斧力:{def.axe}";
                    if (def.hammer > 0) stats += $" 锤力:{def.hammer}";
                    if (def.damage > 0) stats += $" 伤害:{def.damage}";
                    if (def.createTile >= 0) stats += " [可放置方块]";
                    else if (def.createWall > 0) stats += " [可放置背景墙]";
                    _selectedLabel.text = $"{displayName} ({def.nameEn}){stats}";
                }
            }

            if (_bagOpen)
                RefreshCrafting(forceEnvScan: true);
        }

        private void UpdateStatusText()
        {
            if (_statusLabel == null || _bootstrap == null) return;
            var world = _bootstrap.ActiveWorld;
            var pSave = _bootstrap.ActivePlayerSave;
            int tx = _player != null ? World.WorldMap.WorldToTileX(_player.Center.x) : 0;
            int ty = _player != null ? World.WorldMap.WorldToTileY(_player.Center.y) : 0;
            string wName = world != null ? world.WorldName : "-";
            int hp = pSave != null ? pSave.StatLife : 100;
            int maxHp = pSave != null ? pSave.StatLifeMax : 100;
            int mana = pSave != null ? pSave.StatMana : 20;
            int maxMana = pSave != null ? pSave.StatManaMax : 20;

            string clock = WorldLighting.FormatClock(world);
            _statusLabel.text = $"HP {hp}/{maxHp}  MP {mana}/{maxMana}  {clock}\n{wName} ({tx}, {ty})";
        }

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("GameHUDCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            // Ensure EventSystem exists for clicks/hover checks
            if (EventSystem.current == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<EventSystem>();
                esGo.AddComponent<StandaloneInputModule>();
            }

            // 1. Top-Left Hotbar (slots 0..9) + Selected Item Label
            var hotbarPanel = CreatePanel("HotbarPanel", canvasGo.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(544f, 64f), SlateDark);
            for (int i = 0; i < InventoryModel.HotbarCount; i++)
                CreateSlotWidget(i, hotbarPanel.transform, new Vector2(8f + i * 52f, -8f), i == 9 ? "0" : (i + 1).ToString());

            _selectedLabel = CreateText("SelectedItemText", canvasGo.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -86f), new Vector2(640f, 24f), 14, TextPrimary, TextAnchor.MiddleLeft);

            // 2. Top-Left Expandable Bag Panel (slots 10..49)
            _bagPanel = CreatePanel("BagPanel", canvasGo.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -116f), new Vector2(544f, 224f), SlateDark);
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 10; col++)
                {
                    int idx = 10 + row * 10 + col;
                    CreateSlotWidget(idx, _bagPanel.transform, new Vector2(8f + col * 52f, -8f - row * 52f), null);
                }
            }
            _bagPanel.SetActive(false);

            // 2b. Crafting Panel (below BagPanel when open)
            BuildCraftingPanel(canvasGo.transform);

            // 3. Top-Right Status & Action Toolbar
            var topRight = CreatePanel("ToolbarPanel", canvasGo.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -16f), new Vector2(500f, 96f), SlateDark);
            _statusLabel = CreateText("StatusText", topRight.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -8f), new Vector2(476f, 40f), 13, TextPrimary, TextAnchor.UpperRight);
            _statusLabel.GetComponent<RectTransform>().offsetMax = new Vector2(-12f, -8f);

            CreateActionButton("BagBtn", topRight.transform, new Vector2(8f, -54f), new Vector2(64f, 32f), "背包", ToggleBag);
            CreateActionButton("SaveBtn", topRight.transform, new Vector2(78f, -54f), new Vector2(64f, 32f), "保存", () => _bootstrap.SaveSession());
            CreateActionButton("LoadBtn", topRight.transform, new Vector2(148f, -54f), new Vector2(64f, 32f), "重读", () => _bootstrap.ReloadSession());
            CreateActionButton("ResetBtn", topRight.transform, new Vector2(218f, -54f), new Vector2(64f, 32f), "重置", () => _bootstrap.ResetToTutorial());
            CreateActionButton("TouchBtn", topRight.transform, new Vector2(288f, -54f), new Vector2(64f, 32f), "触控", ToggleTouchControls);
            CreateActionButton("TimeBtn", topRight.transform, new Vector2(358f, -54f), new Vector2(64f, 32f), "昼夜", () => _bootstrap.ToggleDayNight());
            CreateActionButton("PoolBtn", topRight.transform, new Vector2(428f, -54f), new Vector2(64f, 32f), "水池", () => _bootstrap.BuildTestWaterPoolNearPlayer());

            // 4. Toast Banner (top-center)
            _toastLabel = CreateText("ToastText", canvasGo.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-200f, -16f), new Vector2(400f, 28f), 15, AmberSelect, TextAnchor.MiddleCenter);
            _toastLabel.gameObject.SetActive(false);

            // 5. Bottom Mobile Virtual Controls
            BuildTouchControls(canvasGo.transform);
        }

        private void BuildCraftingPanel(Transform canvasRoot)
        {
            _craftingPanel = CreatePanel("CraftingPanel", canvasRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -348f), new Vector2(544f, 224f), SlateDark);

            var header = CreateText("CraftHeader", _craftingPanel.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -6f), new Vector2(200f, 22f), 13, AmberSelect, TextAnchor.MiddleLeft);
            header.text = "可合成配方";

            CreateActionButton("PrevPageBtn", _craftingPanel.transform, new Vector2(204f, -6f), new Vector2(28f, 22f), "<", () => ChangeRecipePage(-1));
            _craftPageText = CreateText("CraftPageText", _craftingPanel.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(234f, -6f), new Vector2(48f, 22f), 11, TextMuted, TextAnchor.MiddleCenter);
            CreateActionButton("NextPageBtn", _craftingPanel.transform, new Vector2(284f, -6f), new Vector2(28f, 22f), ">", () => ChangeRecipePage(1));

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 6; col++)
                {
                    int slotIdx = row * 6 + col;
                    Vector2 pos = new Vector2(8f + col * 52f, -34f - row * 52f);
                    var slotGo = CreatePanel($"CraftSlot_{slotIdx}", _craftingPanel.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), pos, new Vector2(48f, 48f), SlateSlot);
                    _craftBg[slotIdx] = slotGo.GetComponent<Image>();

                    var btn = slotGo.AddComponent<Button>();
                    btn.navigation = new Navigation { mode = Navigation.Mode.None };
                    btn.targetGraphic = _craftBg[slotIdx];
                    int captured = slotIdx;
                    btn.onClick.AddListener(() => OnCraftSlotClicked(captured));

                    var iconGo = new GameObject("Icon");
                    iconGo.transform.SetParent(slotGo.transform, false);
                    var iconRt = iconGo.AddComponent<RectTransform>();
                    iconRt.anchorMin = new Vector2(0.15f, 0.15f);
                    iconRt.anchorMax = new Vector2(0.85f, 0.85f);
                    iconRt.offsetMin = Vector2.zero;
                    iconRt.offsetMax = Vector2.zero;
                    var icon = iconGo.AddComponent<Image>();
                    icon.preserveAspect = true;
                    icon.raycastTarget = false;
                    icon.enabled = false;
                    _craftIcon[slotIdx] = icon;

                    var countTxt = CreateText("Count", slotGo.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(4f, 2f), new Vector2(-8f, 18f), 12, TextPrimary, TextAnchor.LowerRight);
                    countTxt.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);
                    countTxt.GetComponent<RectTransform>().offsetMax = new Vector2(-4f, 20f);
                    countTxt.raycastTarget = false;
                    _craftCount[slotIdx] = countTxt;
                }
            }

            // Right-side recipe detail card
            var detailCard = CreatePanel("CraftDetailCard", _craftingPanel.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(324f, -8f), new Vector2(212f, 208f), new Color(0.11f, 0.15f, 0.22f, 0.96f));
            var resIconBg = CreatePanel("ResultIconBg", detailCard.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -8f), new Vector2(44f, 44f), SlateSlot);
            var resIconGo = new GameObject("Icon");
            resIconGo.transform.SetParent(resIconBg.transform, false);
            var resIconRt = resIconGo.AddComponent<RectTransform>();
            resIconRt.anchorMin = new Vector2(0.12f, 0.12f);
            resIconRt.anchorMax = new Vector2(0.88f, 0.88f);
            resIconRt.offsetMin = Vector2.zero;
            resIconRt.offsetMax = Vector2.zero;
            _craftResultIcon = resIconGo.AddComponent<Image>();
            _craftResultIcon.preserveAspect = true;
            _craftResultIcon.raycastTarget = false;
            _craftResultIcon.enabled = false;

            _craftTitleText = CreateText("ResultTitle", detailCard.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(58f, -8f), new Vector2(146f, 24f), 13, TextPrimary, TextAnchor.MiddleLeft);
            _craftStationText = CreateText("StationText", detailCard.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(58f, -32f), new Vector2(146f, 20f), 11, TextMuted, TextAnchor.MiddleLeft);
            _craftIngredientsText = CreateText("IngredientsText", detailCard.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -60f), new Vector2(196f, 102f), 12, TextPrimary, TextAnchor.UpperLeft);

            var craftBtnGo = CreatePanel("CraftBtn", detailCard.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -166f), new Vector2(196f, 34f), AmberSelect);
            _craftBtnBg = craftBtnGo.GetComponent<Image>();
            var craftBtn = craftBtnGo.AddComponent<Button>();
            craftBtn.navigation = new Navigation { mode = Navigation.Mode.None };
            craftBtn.targetGraphic = _craftBtnBg;
            craftBtn.onClick.AddListener(CraftSelectedRecipe);
            var craftBtnLabel = CreateText("Label", craftBtnGo.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 14, new Color(0.08f, 0.11f, 0.16f, 1f), TextAnchor.MiddleCenter);
            var cbrt = craftBtnLabel.GetComponent<RectTransform>();
            cbrt.offsetMin = Vector2.zero;
            cbrt.offsetMax = Vector2.zero;
            craftBtnLabel.text = "合成";
            craftBtnLabel.raycastTarget = false;

            _craftingPanel.SetActive(false);
        }

        private void ChangeRecipePage(int delta)
        {
            int totalPages = Mathf.Max(1, (_availableRecipes.Count + CraftPageSize - 1) / CraftPageSize);
            _recipePage = Mathf.Clamp(_recipePage + delta, 0, totalPages - 1);
            RefreshCrafting(forceEnvScan: false);
        }

        private void OnCraftSlotClicked(int slotIdx)
        {
            int availIdx = _recipePage * CraftPageSize + slotIdx;
            if (availIdx < 0 || availIdx >= _availableRecipes.Count) return;

            if (_selectedAvailableIndex == availIdx)
            {
                CraftSelectedRecipe();
            }
            else
            {
                _selectedAvailableIndex = availIdx;
                RefreshCrafting(forceEnvScan: false);
            }
        }

        private void CraftSelectedRecipe()
        {
            if (_recipeDb == null || _inventory == null) return;
            if (_selectedAvailableIndex < 0 || _selectedAvailableIndex >= _availableRecipes.Count) return;

            int recipeIdx = _availableRecipes[_selectedAvailableIndex];
            if (_recipeDb.CraftRecipe(recipeIdx, _inventory, _adjTiles, _nearWater, _nearHoney, _nearLava))
            {
                var rec = _recipeDb.AllRecipes[recipeIdx];
                ItemDefinition def = null;
                _itemDb?.TryGet(rec.result, out def);
                string name = def != null && !string.IsNullOrEmpty(def.nameZh) ? def.nameZh : $"物品#{rec.result}";
                ShowToast($"已合成 {name} ×{rec.stack}", 1.5f);
            }
            else
            {
                ShowToast("背包已满或材料不足", 1.5f);
            }
        }

        private void RefreshCrafting(bool forceEnvScan)
        {
            if (_recipeDb == null || _inventory == null || _craftingPanel == null) return;

            if (forceEnvScan && _world != null && _player != null)
            {
                _lastEnvTx = WorldMap.WorldToTileX(_player.Center.x);
                _lastEnvTy = WorldMap.WorldToTileY(_player.Center.y);
                RecipeDatabase.UpdateCraftingEnvironment(_world, _player.Center, _adjTiles, out _nearWater, out _nearHoney, out _nearLava);
            }

            _recipeDb.FindRecipes(_inventory, _adjTiles, _nearWater, _nearHoney, _nearLava, _availableRecipes);
            _inventory.PopulateCounts(_invCounts);

            int totalPages = Mathf.Max(1, (_availableRecipes.Count + CraftPageSize - 1) / CraftPageSize);
            _recipePage = Mathf.Clamp(_recipePage, 0, totalPages - 1);
            if (_craftPageText != null)
                _craftPageText.text = $"{_recipePage + 1}/{totalPages}";

            if (_availableRecipes.Count == 0)
                _selectedAvailableIndex = -1;
            else if (_selectedAvailableIndex < 0 || _selectedAvailableIndex >= _availableRecipes.Count)
                _selectedAvailableIndex = 0;

            int start = _recipePage * CraftPageSize;
            for (int i = 0; i < CraftPageSize; i++)
            {
                int availIdx = start + i;
                if (availIdx < _availableRecipes.Count)
                {
                    var rec = _recipeDb.AllRecipes[_availableRecipes[availIdx]];
                    Sprite sp = _catalog != null ? _catalog.GetItemSprite(rec.result) : null;
                    _craftIcon[i].sprite = sp;
                    _craftIcon[i].enabled = sp != null;
                    _craftCount[i].text = rec.stack > 1 ? rec.stack.ToString() : string.Empty;
                    _craftBg[i].color = availIdx == _selectedAvailableIndex ? AmberSelect : SlateSlot;
                }
                else
                {
                    _craftIcon[i].enabled = false;
                    _craftCount[i].text = string.Empty;
                    _craftBg[i].color = SlateSlot;
                }
            }

            if (_selectedAvailableIndex >= 0 && _selectedAvailableIndex < _availableRecipes.Count)
            {
                var rec = _recipeDb.AllRecipes[_availableRecipes[_selectedAvailableIndex]];
                ItemDefinition def = null;
                _itemDb?.TryGet(rec.result, out def);
                string name = def != null && !string.IsNullOrEmpty(def.nameZh) ? def.nameZh : (def != null ? def.internalName : $"#{rec.result}");
                Sprite sp = _catalog != null ? _catalog.GetItemSprite(rec.result) : null;
                _craftResultIcon.sprite = sp;
                _craftResultIcon.enabled = sp != null;
                _craftTitleText.text = rec.stack > 1 ? $"{name} ×{rec.stack}" : name;
                _craftStationText.text = $"需求: {RecipeDatabase.FormatRequirements(rec)}";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < rec.ingredients.Count; i++)
                {
                    var ing = rec.ingredients[i];
                    ItemDefinition ingDef = null;
                    _itemDb?.TryGet(ing.id, out ingDef);
                    string ingName = ingDef != null && !string.IsNullOrEmpty(ingDef.nameZh) ? ingDef.nameZh : $"#{ing.id}";
                    if (rec.groups != null && rec.groups.Contains("Wood") && ing.id == ItemID.Wood) ingName = "任意木材";
                    else if (rec.groups != null && rec.groups.Contains("IronBar") && (ing.id == ItemID.IronBar || ing.id == ItemID.LeadBar)) ingName = "铁锭/铅锭";
                    else if (rec.groups != null && rec.groups.Contains("Sand") && ing.id == ItemID.SandBlock) ingName = "任意沙块";
                    int have = _recipeDb.GetAvailableIngredientCount(rec, ing, _invCounts);
                    sb.AppendLine($"{ingName}: {have}/{ing.stack}");
                }
                _craftIngredientsText.text = sb.ToString();
                if (_craftBtnBg != null) _craftBtnBg.color = AmberSelect;
            }
            else
            {
                _craftResultIcon.enabled = false;
                _craftTitleText.text = "暂无可合成配方";
                _craftStationText.text = "收集木材/矿石或靠近工作台";
                _craftIngredientsText.text = string.Empty;
                if (_craftBtnBg != null) _craftBtnBg.color = SlateButton;
            }
        }

        private void BuildTouchControls(Transform canvasRoot)
        {
            _touchPanel = new GameObject("TouchControls");
            _touchPanel.transform.SetParent(canvasRoot, false);
            var rt = _touchPanel.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            CreateHoldButton("LeftBtn", _touchPanel.transform, new Vector2(0f, 0f), new Vector2(24f, 24f), new Vector2(72f, 72f), "◀",
                () => _player?.SetVirtualMove(-1f), () => _player?.SetVirtualMove(0f));
            CreateHoldButton("DownBtn", _touchPanel.transform, new Vector2(0f, 0f), new Vector2(104f, 24f), new Vector2(64f, 64f), "▼",
                () => _player?.SetVirtualDown(true), () => _player?.SetVirtualDown(false));
            CreateHoldButton("RightBtn", _touchPanel.transform, new Vector2(0f, 0f), new Vector2(176f, 24f), new Vector2(72f, 72f), "▶",
                () => _player?.SetVirtualMove(1f), () => _player?.SetVirtualMove(0f));

            CreateHoldButton("JumpBtn", _touchPanel.transform, new Vector2(1f, 0f), new Vector2(-104f, 24f), new Vector2(80f, 80f), "跳跃",
                () => _player?.SetVirtualJump(true), () => _player?.SetVirtualJump(false));

            _touchPanel.SetActive(_touchVisible);
        }

        private void CreateSlotWidget(int index, Transform parent, Vector2 pos, string keyHint)
        {
            var slotGo = new GameObject($"Slot_{index}");
            slotGo.transform.SetParent(parent, false);
            var rt = slotGo.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(48f, 48f);

            var bg = slotGo.AddComponent<Image>();
            bg.color = SlateSlot;
            _slotBg[index] = bg;

            var btn = slotGo.AddComponent<Button>();
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            btn.targetGraphic = bg;
            int captured = index;
            btn.onClick.AddListener(() => OnSlotClicked(captured));

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(slotGo.transform, false);
            var iconRt = iconGo.AddComponent<RectTransform>();
            iconRt.anchorMin = new Vector2(0.15f, 0.15f);
            iconRt.anchorMax = new Vector2(0.85f, 0.85f);
            iconRt.offsetMin = Vector2.zero;
            iconRt.offsetMax = Vector2.zero;
            var icon = iconGo.AddComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.enabled = false;
            _slotIcon[index] = icon;

            var countTxt = CreateText("Count", slotGo.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(4f, 2f), new Vector2(-8f, 18f), 12, TextPrimary, TextAnchor.LowerRight);
            countTxt.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0f);
            countTxt.GetComponent<RectTransform>().offsetMax = new Vector2(-4f, 20f);
            countTxt.raycastTarget = false;
            _slotCount[index] = countTxt;

            if (!string.IsNullOrEmpty(keyHint))
            {
                var hint = CreateText("Key", slotGo.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(4f, -2f), new Vector2(20f, 14f), 10, TextMuted, TextAnchor.UpperLeft);
                hint.text = keyHint;
                hint.raycastTarget = false;
            }
        }

        private GameObject CreatePanel(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = color;
            return go;
        }

        private Text CreateText(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, int fontSize, Color color, TextAnchor align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(anchorMin.x, anchorMax.y);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var txt = go.AddComponent<Text>();
            txt.font = _font;
            txt.fontSize = fontSize;
            txt.color = color;
            txt.alignment = align;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            return txt;
        }

        private void CreateActionButton(string name, Transform parent, Vector2 pos, Vector2 size, string label, UnityAction onClick)
        {
            var go = CreatePanel(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), pos, size, SlateButton);
            var btn = go.AddComponent<Button>();
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            btn.targetGraphic = go.GetComponent<Image>();
            btn.onClick.AddListener(onClick);

            var txt = CreateText("Label", go.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 13, TextPrimary, TextAnchor.MiddleCenter);
            var rt = txt.GetComponent<RectTransform>();
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            txt.text = label;
            txt.raycastTarget = false;
        }

        private void CreateHoldButton(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, string label, Action onDown, Action onUp)
        {
            var go = CreatePanel(name, parent, anchor, anchor, pos, size, SlateButton);
            var txt = CreateText("Label", go.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 16, TextPrimary, TextAnchor.MiddleCenter);
            var rt = txt.GetComponent<RectTransform>();
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            txt.text = label;
            txt.raycastTarget = false;

            var trigger = go.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => onDown?.Invoke());
            trigger.triggers.Add(down);

            var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            up.callback.AddListener(_ => onUp?.Invoke());
            trigger.triggers.Add(up);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => onUp?.Invoke());
            trigger.triggers.Add(exit);
        }

        private static Font ResolveFont()
        {
            Font dynamic = Font.CreateDynamicFontFromOSFont(
                new[] { "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC", "Arial Unicode MS", "Arial" }, 16);
            if (dynamic != null) return dynamic;
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }
}
