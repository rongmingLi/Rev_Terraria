using System;
using System.Collections.Generic;
using TerrariaMod.Data;
using TerrariaMod.ID;
using TerrariaMod.SaveIO;

namespace TerrariaMod.Player
{
    [Serializable]
    public struct InventorySlot
    {
        public int itemId;
        public int stack;
        public byte prefix;
        public bool favorited;

        public bool IsEmpty => itemId <= 0 || stack <= 0;

        public void Clear()
        {
            itemId = 0;
            stack = 0;
            prefix = 0;
            favorited = false;
        }
    }

    /// <summary>
    /// 50-slot main inventory (slots 0..9 = Hotbar), synced with Items.json maxStack
    /// and PlayerSaveData (.plr AES-128-CBC).
    /// </summary>
    public class InventoryModel
    {
        public const int MainSlotCount = 50;
        public const int HotbarCount = 10;

        private readonly InventorySlot[] _slots = new InventorySlot[MainSlotCount];
        private readonly ItemDatabase _db;
        private int _selectedIndex;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                int clamped = ((value % HotbarCount) + HotbarCount) % HotbarCount;
                if (_selectedIndex != clamped)
                {
                    _selectedIndex = clamped;
                    Changed?.Invoke();
                }
            }
        }

        public event Action Changed;

        public InventoryModel(ItemDatabase db)
        {
            _db = db;
        }

        public InventorySlot GetSlot(int index)
        {
            if (index < 0 || index >= MainSlotCount) return default(InventorySlot);
            return _slots[index];
        }

        public InventorySlot SelectedSlot => GetSlot(_selectedIndex);

        public ItemDefinition GetSelectedItemDef()
        {
            var slot = SelectedSlot;
            if (slot.IsEmpty || _db == null) return null;
            return _db.TryGet(slot.itemId, out var def) ? def : null;
        }

        public bool CanAcceptAny(int itemId)
        {
            if (itemId <= 0) return false;
            int maxStack = 9999;
            if (_db != null && _db.TryGet(itemId, out var def) && def.maxStack > 0)
                maxStack = def.maxStack;

            for (int i = 0; i < MainSlotCount; i++)
            {
                if (_slots[i].IsEmpty) return true;
                if (_slots[i].itemId == itemId && _slots[i].stack < maxStack) return true;
            }
            return false;
        }

        public int AddItem(int itemId, int amount, byte prefix = 0)
        {
            if (itemId <= 0 || amount <= 0) return amount;

            int maxStack = 9999;
            if (_db != null && _db.TryGet(itemId, out var def) && def.maxStack > 0)
                maxStack = def.maxStack;

            int remaining = amount;

            // Pass 1: merge with existing stacks
            for (int i = 0; i < MainSlotCount && remaining > 0; i++)
            {
                if (_slots[i].itemId == itemId && _slots[i].prefix == prefix && _slots[i].stack < maxStack)
                {
                    int add = Math.Min(remaining, maxStack - _slots[i].stack);
                    _slots[i].stack += add;
                    remaining -= add;
                }
            }

            // Pass 2: occupy empty slots
            for (int i = 0; i < MainSlotCount && remaining > 0; i++)
            {
                if (_slots[i].IsEmpty)
                {
                    int add = Math.Min(remaining, maxStack);
                    _slots[i] = new InventorySlot { itemId = itemId, stack = add, prefix = prefix };
                    remaining -= add;
                }
            }

            if (remaining != amount)
                Changed?.Invoke();

            return remaining;
        }

        public bool ConsumeSelected(int amount = 1)
        {
            if (_slots[_selectedIndex].IsEmpty || _slots[_selectedIndex].stack < amount)
                return false;

            _slots[_selectedIndex].stack -= amount;
            if (_slots[_selectedIndex].stack <= 0)
                _slots[_selectedIndex].Clear();
            Changed?.Invoke();
            return true;
        }

        public void SwapSlots(int a, int b)
        {
            if (a < 0 || a >= MainSlotCount || b < 0 || b >= MainSlotCount || a == b)
                return;
            var tmp = _slots[a];
            _slots[a] = _slots[b];
            _slots[b] = tmp;
            Changed?.Invoke();
        }

        public void ClearAll()
        {
            for (int i = 0; i < MainSlotCount; i++)
                _slots[i].Clear();
            Changed?.Invoke();
        }

        public void GrantStarterKit()
        {
            AddItem(ItemID.IronPickaxe, 1);
            AddItem(ItemID.IronAxe, 1);
            AddItem(ItemID.IronHammer, 1);
            AddItem(ItemID.IronBroadsword, 1);
            AddItem(ItemID.Torch, 99);
            AddItem(ItemID.Wood, 200);
            AddItem(ItemID.DirtBlock, 200);
            AddItem(ItemID.StoneBlock, 150);
            AddItem(ItemID.WoodPlatform, 50);
            AddItem(ItemID.WoodWall, 100);
        }

        public bool HasAnyItems()
        {
            for (int i = 0; i < MainSlotCount; i++)
                if (!_slots[i].IsEmpty) return true;
            return false;
        }

        public void ImportFromSave(PlayerSaveData save)
        {
            for (int i = 0; i < MainSlotCount; i++)
            {
                if (save != null && save.Inventory != null && i < save.Inventory.Length && save.Inventory[i].ItemId > 0 && save.Inventory[i].Stack > 0)
                {
                    _slots[i] = new InventorySlot
                    {
                        itemId = save.Inventory[i].ItemId,
                        stack = save.Inventory[i].Stack,
                        prefix = save.Inventory[i].Prefix,
                        favorited = save.Inventory[i].Favorited
                    };
                }
                else
                {
                    _slots[i].Clear();
                }
            }
            Changed?.Invoke();
        }

        public void ExportToSave(PlayerSaveData save)
        {
            if (save == null) return;
            if (save.Inventory == null || save.Inventory.Length < PlayerSaveData.InventorySlotCount)
                save.Inventory = new SavedItemSlot[PlayerSaveData.InventorySlotCount];

            for (int i = 0; i < MainSlotCount; i++)
            {
                save.Inventory[i] = new SavedItemSlot
                {
                    ItemId = _slots[i].itemId,
                    Stack = _slots[i].stack,
                    Prefix = _slots[i].prefix,
                    Favorited = _slots[i].favorited
                };
            }
        }

        public void PopulateCounts(Dictionary<int, int> counts)
        {
            counts.Clear();
            for (int i = 0; i < MainSlotCount; i++)
            {
                if (_slots[i].IsEmpty) continue;
                int id = _slots[i].itemId;
                counts[id] = counts.TryGetValue(id, out int cur) ? cur + _slots[i].stack : _slots[i].stack;
            }
        }

        public bool ConsumeItem(int itemId, int amount)
        {
            if (amount <= 0) return true;
            int remaining = amount;
            for (int i = 0; i < MainSlotCount && remaining > 0; i++)
            {
                if (_slots[i].IsEmpty || _slots[i].itemId != itemId) continue;
                int take = Math.Min(remaining, _slots[i].stack);
                _slots[i].stack -= take;
                remaining -= take;
                if (_slots[i].stack <= 0)
                    _slots[i].Clear();
            }
            if (remaining != amount)
                Changed?.Invoke();
            return remaining == 0;
        }

        public bool ConsumeAny(int[] validIds, int amount)
        {
            if (amount <= 0) return true;
            if (validIds == null || validIds.Length == 0) return false;
            int remaining = amount;
            for (int i = 0; i < MainSlotCount && remaining > 0; i++)
            {
                if (_slots[i].IsEmpty) continue;
                bool match = false;
                for (int k = 0; k < validIds.Length; k++)
                {
                    if (_slots[i].itemId == validIds[k]) { match = true; break; }
                }
                if (!match) continue;
                int take = Math.Min(remaining, _slots[i].stack);
                _slots[i].stack -= take;
                remaining -= take;
                if (_slots[i].stack <= 0)
                    _slots[i].Clear();
            }
            if (remaining != amount)
                Changed?.Invoke();
            return remaining == 0;
        }
    }
}
