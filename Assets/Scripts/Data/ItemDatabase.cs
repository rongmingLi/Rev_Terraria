using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TerrariaMod.Data
{
    [Serializable]
    public class ItemDefinition
    {
        public int id;
        public string internalName;
        public string nameZh;
        public string nameEn;
        public string tooltipZh;

        public int holdStyle;
        public int useStyle;
        public bool channel;
        public bool accessory;
        public int useAnimation;
        public int useTime;
        public int maxStack = 1;

        public int pick;
        public int axe;
        public int hammer;
        public int tileBoost;
        public int createTile = -1;
        public int createWall;
        public int placeStyle;

        public int damage;
        public float knockBack;
        public int healLife;
        public int healMana;
        public bool potion;
        public bool consumable;
        public bool autoReuse;
        public bool useTurn;
        public float scale = 1f;

        public int defense;
        public int headSlot = -1;
        public int bodySlot = -1;
        public int legSlot = -1;

        public int rare;
        public int shoot;
        public float shootSpeed;
        public int ammo;
        public int useAmmo;
        public int mana;
        public bool noUseGraphic;
        public bool noMelee;
        public int value;
        public bool vanity;
        public bool material;

        public int buffType;
        public int buffTime;
        public int crit;
        public int armorPenetration;

        public bool melee;
        public bool magic;
        public bool ranged;
        public bool summon;
        public bool sentry;
    }

    [Serializable]
    public class ItemDatabaseRoot
    {
        public string version;
        public int count;
        public List<ItemDefinition> items = new List<ItemDefinition>();
    }

    /// <summary>
    /// Data-driven item catalog loaded from Assets/Data/Items.json.
    /// Replaces hardcoded SetDefaults1-5 switch tables for easy gameplay modding.
    /// </summary>
    public class ItemDatabase
    {
        private readonly Dictionary<int, ItemDefinition> _byId = new Dictionary<int, ItemDefinition>();
        private readonly Dictionary<string, ItemDefinition> _byName = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);

        public string SourceVersion { get; private set; }
        public int Count => _byId.Count;

        public void LoadFromJson(string jsonText)
        {
            _byId.Clear();
            _byName.Clear();

            ItemDatabaseRoot root = JsonUtility.FromJson<ItemDatabaseRoot>(jsonText);
            if (root == null || root.items == null)
                return;

            SourceVersion = root.version;
            for (int i = 0; i < root.items.Count; i++)
            {
                ItemDefinition item = root.items[i];
                if (item == null || item.id <= 0)
                    continue;

                // Items.json omits maxStack for most items; Terraria 1.4.4+ uses 9999 for stackables and 1 for gear.
                if (item.maxStack <= 1)
                {
                    bool nonStackableGear =
                        (item.damage > 0 && !item.consumable) ||
                        item.pick > 0 || item.axe > 0 || item.hammer > 0 ||
                        item.accessory || item.headSlot >= 0 || item.bodySlot >= 0 || item.legSlot >= 0;
                    if (!nonStackableGear)
                        item.maxStack = 9999;
                }

                _byId[item.id] = item;
                if (!string.IsNullOrEmpty(item.internalName))
                    _byName[item.internalName] = item;
            }
        }

        public void LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Items.json not found", filePath);

            LoadFromJson(File.ReadAllText(filePath));
        }

        public bool TryGet(int id, out ItemDefinition def) => _byId.TryGetValue(id, out def);

        public bool TryGetByInternalName(string internalName, out ItemDefinition def)
        {
            if (string.IsNullOrEmpty(internalName))
            {
                def = null;
                return false;
            }
            return _byName.TryGetValue(internalName, out def);
        }

        public void RegisterOrOverride(ItemDefinition customItem)
        {
            if (customItem == null || customItem.id <= 0)
                throw new ArgumentException("ItemDefinition must have a positive id.");

            _byId[customItem.id] = customItem;
            if (!string.IsNullOrEmpty(customItem.internalName))
                _byName[customItem.internalName] = customItem;
        }
    }
}
