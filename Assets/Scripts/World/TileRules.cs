using System.Collections.Generic;
using TerrariaMod.Data;
using TerrariaMod.ID;

namespace TerrariaMod.World
{
    /// <summary>
    /// Collision, mining hardness, and item-drop rules for Terraria tiles & walls.
    /// Automatically builds reverse createTile/createWall drop mappings from ItemDatabase.
    /// </summary>
    public static class TileRules
    {
        private static readonly HashSet<int> NonSolidTiles = new HashSet<int>
        {
            TileID.Plants, TileID.Torches, TileID.Trees, TileID.OpenDoor, TileID.Bottles,
            TileID.Tables, TileID.Chairs, TileID.Anvils, TileID.Furnaces, TileID.WorkBenches,
            TileID.Platforms, TileID.Saplings, TileID.Containers, TileID.CorruptPlants,
            TileID.Sunflower, TileID.Statues, TileID.Signs, TileID.Banners, TileID.Rope,
            TileID.Chain, TileID.VineFlowers, TileID.Vines, TileID.CrimsonVines, TileID.JungleVines,
            TileID.JunglePlants, TileID.MushroomPlants, TileID.Plants2, TileID.JunglePlants2,
            TileID.HallowedPlants, TileID.HallowedPlants2, TileID.CrimsonPlants, TileID.OasisPlants,
            TileID.AshPlants, TileID.Cactus, TileID.Cobweb,
            TileID.Campfire, TileID.Beds, TileID.Bookcases, TileID.Pianos, TileID.Dressers,
            TileID.GrandfatherClocks, TileID.Painting3X3, TileID.Painting4X3, TileID.Painting6X4,
            TileID.Painting2X3, TileID.Painting3X2, TileID.WeaponsRack, TileID.WeaponsRack2, TileID.Mannequin,
            TileID.Womannequin, TileID.Candles, TileID.Chandeliers, TileID.Lamps, TileID.HangingLanterns,
            TileID.WaterCandle, TileID.PlatinumCandle, TileID.PeaceCandle, TileID.Heart,
            TileID.LifeFruit, TileID.Pots, TileID.ShadowOrbs, TileID.DemonAltar, TileID.Tombstones,
            TileID.Coral, TileID.DyePlants, TileID.Stalactite, TileID.BeachPiles, TileID.LargePiles,
            TileID.LargePiles2, TileID.SmallPiles, TileID.ExposedGems, TileID.PiggyBank,
            TileID.Safes, TileID.DefendersForge, TileID.Containers2, TileID.PlanterBox,
            TileID.MinecartTrack, TileID.Teleporter, TileID.Switches, TileID.PressurePlates,
            TileID.Timers, TileID.MusicBoxes, TileID.Loom, TileID.Sawmill, TileID.TinkerersWorkbench,
            TileID.ImbuingStation, TileID.Kegs, TileID.CookingPots, TileID.AlchemyTable,
            TileID.BewitchingTable, TileID.CrystalBall, TileID.DyeVat, TileID.HeavyWorkBench,
            TileID.SharpeningStation, TileID.AmmoBox, TileID.Solidifier, TileID.Blendomatic,
            TileID.MeatGrinder, TileID.Extractinator, TileID.ChlorophyteExtractinator,
            TileID.PalmTree, TileID.TreeAmber, TileID.TreeAmethyst, TileID.TreeDiamond,
            TileID.TreeEmerald, TileID.TreeRuby, TileID.TreeSapphire, TileID.TreeTopaz,
            TileID.TreeAsh, TileID.VanityTreeSakura, TileID.VanityTreeYellowWillow,
        };

        private static readonly HashSet<int> SolidTopTiles = new HashSet<int>
        {
            TileID.Platforms,
            TileID.PlanterBox,
            TileID.Tables,
            TileID.WorkBenches,
            TileID.Bookcases,
            TileID.Dressers,
            TileID.Pianos,
        };

        private static readonly Dictionary<int, int> TileToItemCache = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> WallToItemCache = new Dictionary<int, int>();
        private static bool _catalogIndexed;

        public static void BuildDropMappings(ItemDatabase db)
        {
            TileToItemCache.Clear();
            WallToItemCache.Clear();

            // Explicit vanilla overrides where block type differs from item's createTile
            TileToItemCache[TileID.Dirt] = ItemID.DirtBlock;
            TileToItemCache[TileID.Grass] = ItemID.DirtBlock;
            TileToItemCache[TileID.CorruptGrass] = ItemID.DirtBlock;
            TileToItemCache[TileID.CrimsonGrass] = ItemID.DirtBlock;
            TileToItemCache[TileID.HallowedGrass] = ItemID.DirtBlock;
            TileToItemCache[TileID.Stone] = ItemID.StoneBlock;
            TileToItemCache[TileID.Trees] = ItemID.Wood;
            TileToItemCache[TileID.PalmTree] = ItemID.PalmWood;
            TileToItemCache[TileID.JungleGrass] = ItemID.MudBlock;
            TileToItemCache[TileID.MushroomGrass] = ItemID.MudBlock;
            TileToItemCache[TileID.Mud] = ItemID.MudBlock;
            TileToItemCache[TileID.Sand] = ItemID.SandBlock;
            TileToItemCache[TileID.SnowBlock] = ItemID.SnowBlock;
            TileToItemCache[TileID.IceBlock] = ItemID.IceBlock;
            TileToItemCache[TileID.Ash] = ItemID.AshBlock;
            TileToItemCache[TileID.WoodBlock] = ItemID.Wood;
            TileToItemCache[TileID.Platforms] = ItemID.WoodPlatform;
            TileToItemCache[TileID.Torches] = ItemID.Torch;
            TileToItemCache[TileID.WorkBenches] = ItemID.WorkBench;
            TileToItemCache[TileID.Furnaces] = ItemID.Furnace;
            TileToItemCache[TileID.Anvils] = ItemID.IronAnvil;
            TileToItemCache[TileID.Chairs] = ItemID.WoodenChair;
            TileToItemCache[TileID.Tables] = ItemID.WoodenTable;
            TileToItemCache[TileID.ClosedDoor] = ItemID.WoodenDoor;
            TileToItemCache[TileID.OpenDoor] = ItemID.WoodenDoor;

            if (db != null)
            {
                // Populate reverse lookup across all 6147 Items.json entries
                for (int id = 1; id < 6300; id++)
                {
                    if (!db.TryGet(id, out var def)) continue;
                    if (def.createTile >= 0 && !TileToItemCache.ContainsKey(def.createTile))
                        TileToItemCache[def.createTile] = def.id;
                    if (def.createWall > 0 && !WallToItemCache.ContainsKey(def.createWall))
                        WallToItemCache[def.createWall] = def.id;
                }
            }

            _catalogIndexed = true;
        }

        public static bool IsSolid(WorldMap world, int tx, int ty)
        {
            if (world == null || !world.InBounds(tx, ty)) return true; // world border acts solid
            if (!world.IsActive(tx, ty)) return false;
            ushort type = world.GetTileType(tx, ty);
            return !NonSolidTiles.Contains(type);
        }

        public static bool IsSolidTop(WorldMap world, int tx, int ty)
        {
            if (world == null || !world.InBounds(tx, ty) || !world.IsActive(tx, ty)) return false;
            return SolidTopTiles.Contains(world.GetTileType(tx, ty));
        }

        public static bool IsSolidForFraming(ushort type) => !NonSolidTiles.Contains(type);

        public static bool IsDirtFamily(ushort type)
        {
            return type == TileID.Dirt || type == TileID.Grass ||
                   type == TileID.CorruptGrass || type == TileID.CrimsonGrass ||
                   type == TileID.HallowedGrass;
        }

        public static bool IsMergeDirtTile(ushort type)
        {
            return type == TileID.Stone || type == TileID.Iron || type == TileID.Copper ||
                   type == TileID.Gold || type == TileID.Silver || type == TileID.Demonite ||
                   type == TileID.ClayBlock || type == TileID.Sand || type == TileID.Mud ||
                   type == TileID.Ash || type == TileID.Silt || type == TileID.Tin ||
                   type == TileID.Lead || type == TileID.Tungsten || type == TileID.Platinum ||
                   type == TileID.Crimtane || type == TileID.Ebonstone || type == TileID.Crimstone ||
                   type == TileID.Pearlstone || type == TileID.SnowBlock || type == TileID.IceBlock;
        }

        public static bool CanKillTile(WorldMap world, int tx, int ty)
        {
            if (world == null || !world.InBounds(tx, ty) || !world.IsActive(tx, ty))
                return false;

            ushort type = world.GetTileType(tx, ty);
            bool selfIsTree = type == TileID.Trees || type == TileID.PalmTree;

            // Vanilla WorldGen.CanKillTile: protect blocks directly beneath active containers/altars or living trees
            if (world.InBounds(tx, ty - 1) && world.IsActive(tx, ty - 1))
            {
                ushort above = world.GetTileType(tx, ty - 1);
                if (above == TileID.Containers || above == TileID.Containers2 ||
                    above == TileID.Dressers || above == TileID.DemonAltar)
                    return false;
                if (!selfIsTree && (above == TileID.Trees || above == TileID.PalmTree))
                    return false;
            }
            return true;
        }

        public static bool GetMultiTileSize(ushort tileType, out int width, out int height, out int originX, out int originY)
        {
            switch (tileType)
            {
                case TileID.WorkBenches:
                case TileID.Anvils:
                case TileID.MythrilAnvil:
                case TileID.HeavyWorkBench:
                case TileID.Sinks:
                case TileID.PiggyBank:
                case TileID.CookingPots:
                    width = 2; height = 1; originX = 0; originY = 0;
                    return true;
                case TileID.Furnaces:
                case TileID.Hellforge:
                case TileID.AdamantiteForge:
                case TileID.Tables:
                case TileID.Loom:
                case TileID.Sawmill:
                case TileID.TinkerersWorkbench:
                case TileID.AlchemyTable:
                case TileID.DemonAltar:
                    width = 3; height = 2; originX = 1; originY = 1;
                    return true;
                case TileID.Containers:
                case TileID.Containers2:
                    width = 2; height = 2; originX = 0; originY = 1;
                    return true;
                case TileID.Chairs:
                    width = 1; height = 2; originX = 0; originY = 1;
                    return true;
                case TileID.ClosedDoor:
                    width = 1; height = 3; originX = 0; originY = 1;
                    return true;
                case TileID.OpenDoor:
                    width = 2; height = 3; originX = 0; originY = 1;
                    return true;
                default:
                    width = 1; height = 1; originX = 0; originY = 0;
                    return false;
            }
        }

        public static bool IsCuttablePlant(ushort type)
        {
            return type == TileID.Plants || type == TileID.Plants2 ||
                   type == TileID.CorruptPlants || type == TileID.JunglePlants ||
                   type == TileID.JunglePlants2 || type == TileID.MushroomPlants ||
                   type == TileID.HallowedPlants || type == TileID.HallowedPlants2 ||
                   type == TileID.CrimsonPlants;
        }

        public static bool IsGroundAnchoredProp(ushort type)
        {
            return IsCuttablePlant(type) ||
                   type == TileID.Saplings ||
                   type == TileID.Sunflower || type == TileID.Bottles;
        }

        public static int GetRequiredPickPower(ushort tileType)
        {
            switch (tileType)
            {
                case TileID.Demonite:
                case TileID.Crimtane:
                case TileID.Meteorite:
                    return 55;
                case TileID.Ebonstone:
                case TileID.Crimstone:
                case TileID.Pearlstone:
                case TileID.Obsidian:
                    return 65;
                case TileID.Hellstone:
                    return 65;
                case TileID.LihzahrdBrick:
                    return 210;
                default:
                    return 0;
            }
        }

        public static int GetTileMaxDamage(ushort tileType)
        {
            switch (tileType)
            {
                case TileID.Dirt:
                case TileID.Grass:
                case TileID.Mud:
                case TileID.Sand:
                case TileID.SnowBlock:
                case TileID.Ash:
                case TileID.ClayBlock:
                    return 50;
                case TileID.Torches:
                case TileID.Plants:
                case TileID.Platforms:
                    return 10;
                case TileID.Trees:
                case TileID.WoodBlock:
                    return 80;
                case TileID.Stone:
                case TileID.IceBlock:
                    return 100;
                default:
                    return 100;
            }
        }

        public static int GetDropItemForTile(ushort tileType)
        {
            if (!_catalogIndexed) BuildDropMappings(null);
            return TileToItemCache.TryGetValue(tileType, out int itemId) ? itemId : 0;
        }

        public static int GetDropItemForWall(ushort wallType)
        {
            if (!_catalogIndexed) BuildDropMappings(null);
            return WallToItemCache.TryGetValue(wallType, out int itemId) ? itemId : 0;
        }
    }
}
