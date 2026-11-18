using System;
using TerrariaMod.ID;

namespace TerrariaMod.World
{
    /// <summary>
    /// Vanilla-aligned cellular liquid simulation (Water, Lava, Honey, Shimmer):
    /// - Downward pouring into non-solid empty/matching tiles
    /// - Horizontal equalization across supported tiles
    /// - Cross-liquid tile generation (Water + Lava -> Obsidian, Water + Honey -> HoneyBlock, Lava + Honey -> CrispyHoneyBlock).
    /// </summary>
    public static class LiquidSim
    {
        public static bool StepRegion(WorldMap world, int minX, int maxX, int minY, int maxY)
        {
            if (world == null) return false;

            minX = Math.Max(1, minX);
            maxX = Math.Min(world.Width - 2, maxX);
            minY = Math.Max(1, minY);
            maxY = Math.Min(world.Height - 2, maxY);

            bool anyVisibleChange = false;

            // Bottom-up scan so falling liquid doesn't teleport multiple tiles per tick
            for (int y = maxY; y >= minY; y--)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = world.Index(x, y);
                    byte amt = world.Liquid[idx];
                    if (amt == 0) continue;

                    // If trapped inside a solid tile, evaporate/skip
                    if (TileRules.IsSolid(world, x, y))
                        continue;

                    byte lType = world.LiquidType[idx];

                    // 1. Try pouring downward
                    int downIdx = world.Index(x, y + 1);
                    if (!TileRules.IsSolid(world, x, y + 1))
                    {
                        byte downAmt = world.Liquid[downIdx];
                        byte downType = world.LiquidType[downIdx];

                        if (downAmt > 0 && downType != lType)
                        {
                            if (TryMergeLiquids(world, x, y, x, y + 1, lType, downType))
                                anyVisibleChange = true;
                            continue;
                        }

                        if (downAmt < 255)
                        {
                            int transfer = Math.Min(amt, 255 - downAmt);
                            if (transfer > 0)
                            {
                                world.Liquid[idx] = (byte)(amt - transfer);
                                world.Liquid[downIdx] = (byte)(downAmt + transfer);
                                world.LiquidType[downIdx] = lType;
                                if (world.Liquid[idx] == 0) world.LiquidType[idx] = 0;

                                if (transfer >= 6 || world.Liquid[idx] == 0)
                                {
                                    world.MarkTileAndNeighborsDirty(x, y);
                                    world.MarkTileAndNeighborsDirty(x, y + 1);
                                    anyVisibleChange = true;
                                }
                                amt = world.Liquid[idx];
                                if (amt == 0) continue;
                            }
                        }
                    }

                    // 2. Horizontal equalization
                    if (amt <= 1) continue;
                    int dir = ((x + y) & 1) == 0 ? 1 : -1;
                    if (TryFlowSide(world, x, y, x + dir, y, idx, lType))
                        anyVisibleChange = true;
                    if (world.Liquid[idx] > 1 && TryFlowSide(world, x, y, x - dir, y, idx, lType))
                        anyVisibleChange = true;
                }
            }

            return anyVisibleChange;
        }

        private static bool TryFlowSide(WorldMap world, int sx, int sy, int nx, int ny, int sIdx, byte lType)
        {
            if (TileRules.IsSolid(world, nx, ny)) return false;

            int nIdx = world.Index(nx, ny);
            byte sAmt = world.Liquid[sIdx];
            byte nAmt = world.Liquid[nIdx];
            byte nType = world.LiquidType[nIdx];

            if (nAmt > 0 && nType != lType)
                return TryMergeLiquids(world, sx, sy, nx, ny, lType, nType);

            int diff = sAmt - nAmt;
            if (diff <= 4) return false;

            int flow = diff / 2;
            if (flow <= 0) return false;

            world.Liquid[sIdx] = (byte)(sAmt - flow);
            world.Liquid[nIdx] = (byte)(nAmt + flow);
            world.LiquidType[nIdx] = lType;
            if (world.Liquid[sIdx] == 0) world.LiquidType[sIdx] = 0;

            if (flow >= 6 || nAmt == 0 || world.Liquid[sIdx] == 0)
            {
                world.MarkTileAndNeighborsDirty(sx, sy);
                world.MarkTileAndNeighborsDirty(nx, ny);
                return true;
            }
            return false;
        }

        private static bool TryMergeLiquids(WorldMap world, int sx, int sy, int tx, int ty, byte typeA, byte typeB)
        {
            ushort resultTile = 0;
            if ((typeA == 0 && typeB == 1) || (typeA == 1 && typeB == 0))
                resultTile = TileID.Obsidian;
            else if ((typeA == 0 && typeB == 2) || (typeA == 2 && typeB == 0))
                resultTile = TileID.HoneyBlock;
            else if ((typeA == 1 && typeB == 2) || (typeA == 2 && typeB == 1))
                resultTile = TileID.CrispyHoneyBlock;

            if (resultTile == 0) return false;

            int sIdx = world.Index(sx, sy);
            int tIdx = world.Index(tx, ty);
            world.Liquid[sIdx] = 0;
            world.LiquidType[sIdx] = 0;
            world.Liquid[tIdx] = 0;
            world.LiquidType[tIdx] = 0;
            world.SetTile(tx, ty, resultTile, active: true);
            TileFraming.FrameNeighborhood(world, tx, ty);
            world.MarkTileAndNeighborsDirty(sx, sy);
            return true;
        }
    }
}
