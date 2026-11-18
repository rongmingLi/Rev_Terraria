using TerrariaMod.ID;

namespace TerrariaMod.World
{
    /// <summary>
    /// 4-neighbor cardinal auto-tiler matching Terraria's 18x18 tile sprite grid
    /// and 36x36 wall sprite grid.
    /// </summary>
    public static class TileFraming
    {
        // Map 4-bit cardinal mask (bit0=Up, bit1=Down, bit2=Left, bit3=Right) -> (col, row) in 18px grid.
        // Verified against Tiles_0 / Tiles_30 pixel outlines.
        private static readonly (short col, short row)[] CardinalFrameTable =
        {
            (9, 3), // 0:  isolated
            (6, 3), // 1:  U
            (6, 0), // 2:  D
            (5, 1), // 3:  U|D (vertical pillar)
            (12, 0),// 4:  L
            (1, 4), // 5:  U|L (bottom-right outer corner)
            (1, 3), // 6:  D|L (top-right outer corner)
            (4, 1), // 7:  U|D|L (exposed right edge)
            (9, 0), // 8:  R
            (0, 4), // 9:  U|R (bottom-left outer corner)
            (0, 3), // 10: D|R (top-left outer corner)
            (0, 1), // 11: U|D|R (exposed left edge)
            (6, 4), // 12: L|R (horizontal bar)
            (1, 2), // 13: U|L|R (exposed bottom edge)
            (1, 0), // 14: D|L|R (exposed top edge)
            (1, 1), // 15: U|D|L|R (interior solid)
        };

        public static void FrameTile(WorldMap world, int x, int y)
        {
            if (!world.InBounds(x, y) || !world.IsActive(x, y))
                return;

            ushort type = world.GetTileType(x, y);
            int idx = world.Index(x, y);
            if (type == TileID.Platforms)
            {
                bool connL = ConnectsToPlatform(world, x - 1, y);
                bool connR = ConnectsToPlatform(world, x + 1, y);
                if (connL && connR) world.FrameX[idx] = 0;
                else if (connL) world.FrameX[idx] = 18;
                else if (connR) world.FrameX[idx] = 36;
                else world.FrameX[idx] = 90;
                return;
            }
            if (type == TileID.Torches)
            {
                bool downSolid = TileRules.IsSolid(world, x, y + 1) || TileRules.IsSolidTop(world, x, y + 1);
                if (downSolid) world.FrameX[idx] = 0;
                else if (CanTorchAttachSide(world, x - 1, y)) world.FrameX[idx] = 22;
                else if (CanTorchAttachSide(world, x + 1, y)) world.FrameX[idx] = 44;
                else world.FrameX[idx] = 0;
                return;
            }

            if (world.IsImportantTile(type))
                return;

            int mask = 0;
            if (ConnectsTo(world, x, y - 1)) mask |= 1; // Up
            if (ConnectsTo(world, x, y + 1)) mask |= 2; // Down
            if (ConnectsTo(world, x - 1, y)) mask |= 4; // Left
            if (ConnectsTo(world, x + 1, y)) mask |= 8; // Right

            var cell = CardinalFrameTable[mask];
            int variant = ((x * 73856093) ^ (y * 19349663)) & 0x7fffffff;
            variant %= 3;
            short col = cell.col;
            short row = cell.row;
            if (mask == 15) col = (short)(1 + variant);
            else if (mask == 14) col = (short)(1 + variant);
            else if (mask == 13) col = (short)(1 + variant);
            else if (mask == 11) row = (short)variant;
            else if (mask == 7) row = (short)variant;

            // Vanilla Main.tileMergeDirt: organic dirt-merge borders for stone/ores/clay surrounded by dirt
            if (mask == 15 && TileRules.IsMergeDirtTile(type))
            {
                int dirtMask = 0;
                if (IsDirtAt(world, x, y - 1)) dirtMask |= 1; // Up
                if (IsDirtAt(world, x, y + 1)) dirtMask |= 2; // Down
                if (IsDirtAt(world, x - 1, y)) dirtMask |= 4; // Left
                if (IsDirtAt(world, x + 1, y)) dirtMask |= 8; // Right

                if (dirtMask != 0)
                {
                    switch (dirtMask)
                    {
                        case 1: col = (short)(8 + variant); row = 6; break;          // Dirt Up
                        case 2: col = (short)(8 + variant); row = 5; break;          // Dirt Down
                        case 4: col = 9; row = (short)(7 + variant); break;          // Dirt Left
                        case 8: col = 8; row = (short)(7 + variant); break;          // Dirt Right
                        case 5: col = 2; row = (short)(5 + variant * 2); break;      // Dirt Up+Left
                        case 9: col = 3; row = (short)(5 + variant * 2); break;      // Dirt Up+Right
                        case 6: col = 2; row = (short)(6 + variant * 2); break;      // Dirt Down+Left
                        case 10: col = 3; row = (short)(6 + variant * 2); break;     // Dirt Down+Right
                        case 3: col = (short)(8 + variant); row = 10; break;         // Dirt Up+Down
                        case 12: col = 10; row = (short)(7 + variant); break;        // Dirt Left+Right
                        case 7: col = 12; row = (short)(5 + variant); break;         // Dirt Up+Down+Left
                        case 11: col = 12; row = (short)(8 + variant); break;        // Dirt Up+Down+Right
                        case 13: col = 11; row = (short)(5 + variant); break;        // Dirt Up+Left+Right
                        case 14: col = 11; row = (short)(8 + variant); break;        // Dirt Down+Left+Right
                        case 15: col = (short)(6 + variant); row = 11; break;        // Isolated in Dirt
                    }
                }
            }

            world.FrameX[idx] = (short)(col * 18);
            world.FrameY[idx] = (short)(row * 18);
        }

        public static void FrameNeighborhood(WorldMap world, int x, int y)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    FrameTile(world, x + dx, y + dy);
                }
            }
            world.InvalidateWallFrameNeighborhood(x, y);
        }

        public static void EnsureWallFrame(WorldMap world, int x, int y, out short fx, out short fy)
        {
            int idx = world.Index(x, y);
            if (world.WallFrameX[idx] >= 0)
            {
                fx = world.WallFrameX[idx];
                fy = world.WallFrameY[idx];
                return;
            }

            int mask = 0;
            if (world.GetWallType(x, y - 1) > 0) mask |= 1; // Up
            if (world.GetWallType(x, y + 1) > 0) mask |= 2; // Down
            if (world.GetWallType(x - 1, y) > 0) mask |= 4; // Left
            if (world.GetWallType(x + 1, y) > 0) mask |= 8; // Right

            // Map wall cardinal mask onto standard 13x5 (36x36 step) Terraria wall sheet
            var cell = CardinalFrameTable[mask];
            int variant = (((x * 73856093) ^ (y * 19349663)) & 0x7fffffff) % 3;
            short col = cell.col;
            short row = cell.row;
            if (mask == 15 || mask == 14 || mask == 13) col = (short)(1 + variant);
            else if (mask == 11 || mask == 7) row = (short)variant;

            fx = (short)(col * 36);
            fy = (short)(row * 36);
            world.WallFrameX[idx] = fx;
            world.WallFrameY[idx] = fy;
        }

        private static bool ConnectsTo(WorldMap world, int nx, int ny)
        {
            if (!world.InBounds(nx, ny)) return true;
            if (!world.IsActive(nx, ny)) return false;
            return TileRules.IsSolidForFraming(world.GetTileType(nx, ny));
        }

        private static bool IsDirtAt(WorldMap world, int nx, int ny)
        {
            if (!world.InBounds(nx, ny) || !world.IsActive(nx, ny)) return false;
            return TileRules.IsDirtFamily(world.GetTileType(nx, ny));
        }

        private static bool CanTorchAttachSide(WorldMap world, int nx, int ny)
        {
            if (!world.InBounds(nx, ny) || !world.IsActive(nx, ny)) return false;
            ushort t = world.GetTileType(nx, ny);
            return TileRules.IsSolidForFraming(t) || t == TileID.Trees || t == TileID.PalmTree;
        }

        private static bool ConnectsToPlatform(WorldMap world, int nx, int ny)
        {
            if (!world.InBounds(nx, ny)) return true;
            if (!world.IsActive(nx, ny)) return false;
            ushort t = world.GetTileType(nx, ny);
            return t == TileID.Platforms || TileRules.IsSolidForFraming(t);
        }
    }
}
