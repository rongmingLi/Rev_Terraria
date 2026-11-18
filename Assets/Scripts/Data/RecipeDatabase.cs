using System;
using System.Collections.Generic;
using System.IO;
using TerrariaMod.ID;
using TerrariaMod.Player;
using TerrariaMod.World;
using UnityEngine;

namespace TerrariaMod.Data
{
    [Serializable]
    public class RecipeIngredient
    {
        public int id;
        public int stack = 1;
    }

    [Serializable]
    public class RecipeDefinition
    {
        public int result;
        public int stack = 1;
        public List<RecipeIngredient> ingredients = new List<RecipeIngredient>();
        public List<int> tiles = new List<int>();
        public List<string> groups = new List<string>();
        public bool needWater;
        public bool needHoney;
        public bool needLava;
    }

    [Serializable]
    public class RecipeDatabaseRoot
    {
        public string version;
        public int count;
        public List<RecipeDefinition> recipes = new List<RecipeDefinition>();
    }

    /// <summary>
    /// Vanilla-aligned crafting recipe catalog + Player.AdjTiles / liquid environment scanner.
    /// Loads Assets/Data/Recipes.json (extracted 1:1 from Terraria v1.4.5.6.4 Recipe.SetupRecipes).
    /// </summary>
    public class RecipeDatabase
    {
        public const int AdjRangeX = 4;
        public const int AdjRangeY = 3;

        private static readonly int[] WoodGroupIds = { 9, 619, 620, 621, 911, 1729, 2503, 2504, 5215 };
        private static readonly int[] IronBarGroupIds = { 22, 704 };
        private static readonly int[] SandGroupIds = { 169, 370, 408, 1246 };
        private static readonly int[] PressurePlateGroupIds = { 529, 541, 542, 543, 852, 853, 1151, 3625 };

        private readonly List<RecipeDefinition> _recipes = new List<RecipeDefinition>();
        private readonly Dictionary<int, int> _tempCounts = new Dictionary<int, int>();

        public IReadOnlyList<RecipeDefinition> AllRecipes => _recipes;
        public int Count => _recipes.Count;
        public string Version { get; private set; } = "unknown";

        public void LoadFromFile(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"[RecipeDatabase] File not found: {path}");
                return;
            }
            LoadFromJson(File.ReadAllText(path));
        }

        public void LoadFromJson(string json)
        {
            _recipes.Clear();
            var root = JsonUtility.FromJson<RecipeDatabaseRoot>(json);
            if (root == null || root.recipes == null)
            {
                Debug.LogError("[RecipeDatabase] Failed to parse Recipes.json");
                return;
            }
            Version = root.version;
            _recipes.AddRange(root.recipes);
        }

        public static void UpdateCraftingEnvironment(
            WorldMap world,
            Vector2 playerCenter,
            HashSet<int> adjTiles,
            out bool nearWater,
            out bool nearHoney,
            out bool nearLava)
        {
            adjTiles.Clear();
            nearWater = false;
            nearHoney = false;
            nearLava = false;

            if (world == null) return;

            int cx = WorldMap.WorldToTileX(playerCenter.x);
            int cy = WorldMap.WorldToTileY(playerCenter.y);

            for (int y = cy - AdjRangeY; y <= cy + AdjRangeY; y++)
            {
                for (int x = cx - AdjRangeX; x <= cx + AdjRangeX; x++)
                {
                    if (!world.InBounds(x, y)) continue;

                    int idx = world.Index(x, y);
                    if (world.Liquid[idx] > 0)
                    {
                        byte lt = world.LiquidType[idx];
                        if (lt == 0 || lt == 1) nearWater = true; // 0/1 = Water in WorldMap
                        else if (lt == 2) nearLava = true;
                        else if (lt == 3) nearHoney = true;
                    }

                    if (!world.IsActive(x, y)) continue;
                    ushort type = world.TileType[idx];
                    adjTiles.Add(type);

                    // Vanilla station equivalences (Player.AdjTiles)
                    if (type == TileID.Hellforge)
                    {
                        adjTiles.Add(TileID.Furnaces);
                    }
                    else if (type == TileID.AdamantiteForge)
                    {
                        adjTiles.Add(TileID.Hellforge);
                        adjTiles.Add(TileID.Furnaces);
                    }
                    else if (type == TileID.MythrilAnvil)
                    {
                        adjTiles.Add(TileID.Anvils);
                    }
                    else if (type == TileID.HeavyWorkBench)
                    {
                        adjTiles.Add(TileID.WorkBenches);
                    }
                    else if (type == TileID.AlchemyTable)
                    {
                        adjTiles.Add(TileID.Bottles);
                    }
                    else if (type == TileID.Sinks)
                    {
                        nearWater = true;
                    }
                }
            }
        }

        public int[] GetGroupIdsForIngredient(RecipeDefinition recipe, int ingredientId)
        {
            if (recipe.groups != null && recipe.groups.Count > 0)
            {
                if (ingredientId == ItemID.Wood && recipe.groups.Contains("Wood")) return WoodGroupIds;
                if ((ingredientId == ItemID.IronBar || ingredientId == ItemID.LeadBar) && recipe.groups.Contains("IronBar")) return IronBarGroupIds;
                if (ingredientId == ItemID.SandBlock && recipe.groups.Contains("Sand")) return SandGroupIds;
                if (recipe.groups.Contains("PressurePlate") && Array.IndexOf(PressurePlateGroupIds, ingredientId) >= 0) return PressurePlateGroupIds;
            }
            return null;
        }

        public int GetAvailableIngredientCount(RecipeDefinition recipe, RecipeIngredient ing, Dictionary<int, int> invCounts)
        {
            int[] groupIds = GetGroupIdsForIngredient(recipe, ing.id);
            if (groupIds != null)
            {
                int sum = 0;
                for (int i = 0; i < groupIds.Length; i++)
                {
                    if (invCounts.TryGetValue(groupIds[i], out int c))
                        sum += c;
                }
                return sum;
            }
            return invCounts.TryGetValue(ing.id, out int count) ? count : 0;
        }

        public bool MeetsConditions(RecipeDefinition recipe, HashSet<int> adjTiles, bool nearWater, bool nearHoney, bool nearLava)
        {
            if (recipe.needWater && !nearWater) return false;
            if (recipe.needHoney && !nearHoney) return false;
            if (recipe.needLava && !nearLava) return false;

            if (recipe.tiles != null)
            {
                for (int i = 0; i < recipe.tiles.Count; i++)
                {
                    if (!adjTiles.Contains(recipe.tiles[i]))
                        return false;
                }
            }
            return true;
        }

        public bool CanCraft(RecipeDefinition recipe, Dictionary<int, int> invCounts, HashSet<int> adjTiles, bool nearWater, bool nearHoney, bool nearLava)
        {
            if (recipe == null || !MeetsConditions(recipe, adjTiles, nearWater, nearHoney, nearLava))
                return false;

            if (recipe.ingredients == null || recipe.ingredients.Count == 0)
                return false;

            for (int i = 0; i < recipe.ingredients.Count; i++)
            {
                var ing = recipe.ingredients[i];
                if (GetAvailableIngredientCount(recipe, ing, invCounts) < ing.stack)
                    return false;
            }
            return true;
        }

        public void FindRecipes(
            InventoryModel inventory,
            HashSet<int> adjTiles,
            bool nearWater,
            bool nearHoney,
            bool nearLava,
            List<int> outAvailableIndices)
        {
            outAvailableIndices.Clear();
            if (inventory == null) return;

            inventory.PopulateCounts(_tempCounts);
            for (int i = 0; i < _recipes.Count; i++)
            {
                if (CanCraft(_recipes[i], _tempCounts, adjTiles, nearWater, nearHoney, nearLava))
                    outAvailableIndices.Add(i);
            }
        }

        public bool CraftRecipe(int recipeIndex, InventoryModel inventory, HashSet<int> adjTiles, bool nearWater, bool nearHoney, bool nearLava)
        {
            if (inventory == null || recipeIndex < 0 || recipeIndex >= _recipes.Count)
                return false;

            var recipe = _recipes[recipeIndex];
            inventory.PopulateCounts(_tempCounts);
            if (!CanCraft(recipe, _tempCounts, adjTiles, nearWater, nearHoney, nearLava))
                return false;

            if (!inventory.CanAcceptAny(recipe.result))
                return false;

            // Consume ingredients
            for (int i = 0; i < recipe.ingredients.Count; i++)
            {
                var ing = recipe.ingredients[i];
                int[] groupIds = GetGroupIdsForIngredient(recipe, ing.id);
                if (groupIds != null)
                    inventory.ConsumeAny(groupIds, ing.stack);
                else
                    inventory.ConsumeItem(ing.id, ing.stack);
            }

            int leftover = inventory.AddItem(recipe.result, recipe.stack);
            return leftover == 0;
        }

        public static string FormatRequirements(RecipeDefinition recipe)
        {
            if (recipe == null) return "徒手";
            var parts = new List<string>();
            if (recipe.tiles != null)
            {
                for (int i = 0; i < recipe.tiles.Count; i++)
                    parts.Add(GetTileStationName(recipe.tiles[i]));
            }
            if (recipe.needWater) parts.Add("水边");
            if (recipe.needHoney) parts.Add("蜂蜜旁");
            if (recipe.needLava) parts.Add("熔岩旁");
            return parts.Count > 0 ? string.Join(" + ", parts) : "徒手";
        }

        private static string GetTileStationName(int tileId)
        {
            switch (tileId)
            {
                case TileID.WorkBenches: return "工作台";
                case TileID.Furnaces: return "熔炉";
                case TileID.Anvils: return "铁/铅砧";
                case TileID.Bottles: return "放置的瓶子/炼药桌";
                case TileID.Tables: return "桌子";
                case TileID.Chairs: return "椅子";
                case TileID.Sawmill: return "锯木机";
                case TileID.Loom: return "织布机";
                case TileID.CookingPots: return "烹饪锅";
                case TileID.DemonAltar: return "恶魔/猩红祭坛";
                case TileID.Hellforge: return "地狱熔炉";
                case TileID.MythrilAnvil: return "秘银/山铜砧";
                case TileID.AdamantiteForge: return "精金/钛金熔炉";
                case TileID.TinkerersWorkbench: return "工匠作坊";
                case TileID.HeavyWorkBench: return "重型工作台";
                case TileID.DyeVat: return "染缸";
                case TileID.Kegs: return "酒桶";
                default: return $"制作台#{tileId}";
            }
        }
    }
}
