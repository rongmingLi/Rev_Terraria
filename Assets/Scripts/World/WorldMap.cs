using System;
using System.Collections.Generic;

namespace TerrariaMod.World
{
    /// <summary>
    /// Compact column-major Terraria world tile storage.
    /// Zero per-tile heap allocations; supports 4200x1200+ vanilla worlds smoothly.
    /// Coordinate convention: tx in [0, Width), ty in [0, Height) where ty=0 is top of sky.
    /// Unity world-space mapping: wx = tx, wy = -ty (so +Y is up, 1 unit = 16 px = 1 tile).
    /// </summary>
    public class WorldMap
    {
        public const int ChunkSize = 32;

        public int FormatVersion = 230;
        public string WorldName = "New World";
        public string SeedText = "12345";
        public ulong WorldGeneratorVersion = 1;
        public byte[] UniqueId = new byte[16];
        public int WorldId = 1;
        public int GameMode;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int SpawnX;
        public int SpawnY;
        public double SurfaceLayer;
        public double RockLayer;
        public double Time = 13500.0;
        public bool DayTime = true;
        public bool[] TileFrameImportant;

        public readonly ushort[] TileType;
        public readonly ushort[] WallType;
        public readonly byte[] Flags;       // bit 0: Active, bit 1: HalfBrick
        public readonly short[] FrameX;
        public readonly short[] FrameY;
        public readonly byte[] TileColor;
        public readonly byte[] WallColor;
        public readonly byte[] Liquid;
        public readonly byte[] LiquidType;

        // Cached wall auto-frame coordinates (-1 = uncomputed)
        public readonly short[] WallFrameX;
        public readonly short[] WallFrameY;

        // Trailing sections preserved when resaving vanilla .wld files
        public byte[] TrailingSectionsBlob;

        public int ChunksX => (Width + ChunkSize - 1) / ChunkSize;
        public int ChunksY => (Height + ChunkSize - 1) / ChunkSize;

        public event Action<int, int> ChunkDirty;

        public WorldMap(int width, int height, bool[] importance = null)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            int total = Width * Height;

            TileType = new ushort[total];
            WallType = new ushort[total];
            Flags = new byte[total];
            FrameX = new short[total];
            FrameY = new short[total];
            TileColor = new byte[total];
            WallColor = new byte[total];
            Liquid = new byte[total];
            LiquidType = new byte[total];
            WallFrameX = new short[total];
            WallFrameY = new short[total];

            for (int i = 0; i < total; i++)
                WallFrameX[i] = -1;

            TileFrameImportant = importance ?? CreateDefaultImportance(800);
            SpawnX = Width / 2;
            SpawnY = Height / 3;
            SurfaceLayer = Height * 0.28;
            RockLayer = Height * 0.35;
        }

        public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

        public int Index(int x, int y) => x * Height + y;

        public bool IsActive(int x, int y)
        {
            if (!InBounds(x, y)) return false;
            return (Flags[x * Height + y] & 1) != 0;
        }

        public ushort GetTileType(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            return TileType[x * Height + y];
        }

        public ushort GetWallType(int x, int y)
        {
            if (!InBounds(x, y)) return 0;
            return WallType[x * Height + y];
        }

        public bool IsImportantTile(ushort type)
        {
            return TileFrameImportant != null && type < TileFrameImportant.Length && TileFrameImportant[type];
        }

        public void SetTile(int x, int y, ushort type, bool active = true, short frameX = 0, short frameY = 0)
        {
            if (!InBounds(x, y)) return;
            int idx = x * Height + y;
            if (active)
                Flags[idx] |= 1;
            else
                Flags[idx] = (byte)(Flags[idx] & ~1);
            TileType[idx] = active ? type : (ushort)0;
            FrameX[idx] = frameX;
            FrameY[idx] = frameY;
            MarkTileAndNeighborsDirty(x, y);
        }

        public void ClearTile(int x, int y)
        {
            SetTile(x, y, 0, active: false, frameX: 0, frameY: 0);
        }

        public void SetWall(int x, int y, ushort wallType)
        {
            if (!InBounds(x, y)) return;
            int idx = x * Height + y;
            WallType[idx] = wallType;
            WallFrameX[idx] = -1;
            MarkTileAndNeighborsDirty(x, y);
        }

        public void InvalidateWallFrameNeighborhood(int x, int y)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if (InBounds(nx, ny))
                        WallFrameX[nx * Height + ny] = -1;
                }
            }
        }

        public void MarkTileAndNeighborsDirty(int x, int y)
        {
            if (ChunkDirty == null) return;
            int minCx = Math.Max(0, (x - 1) / ChunkSize);
            int maxCx = Math.Min(ChunksX - 1, (x + 1) / ChunkSize);
            int minCy = Math.Max(0, (y - 1) / ChunkSize);
            int maxCy = Math.Min(ChunksY - 1, (y + 1) / ChunkSize);
            for (int cx = minCx; cx <= maxCx; cx++)
                for (int cy = minCy; cy <= maxCy; cy++)
                    ChunkDirty.Invoke(cx, cy);
        }

        public static float TileToWorldX(int tx) => tx + 0.5f;
        public static float TileToWorldY(int ty) => -ty - 0.5f;
        public static int WorldToTileX(float wx) => (int)Math.Floor(wx);
        public static int WorldToTileY(float wy) => (int)Math.Floor(-wy);

        /// <summary>
        /// Fallback flat/hilly surface world if the user chooses a quick test world.
        /// </summary>
        public static WorldMap CreateStarterSurface(int width = 420, int height = 180)
        {
            var world = new WorldMap(width, height)
            {
                WorldName = "Mod Starter Surface",
                SeedText = "phase3-starter",
                SpawnX = width / 2,
                SurfaceLayer = 62,
                RockLayer = 85
            };

            int surfaceBase = 58;
            for (int x = 0; x < width; x++)
            {
                int hill = (int)Math.Round(Math.Sin(x * 0.08) * 3.0 + Math.Cos(x * 0.21) * 1.5);
                int sy = surfaceBase + hill;
                if (Math.Abs(x - world.SpawnX) <= 6)
                    sy = surfaceBase;

                for (int y = sy; y < height; y++)
                {
                    int idx = x * height + y;
                    world.Flags[idx] = 1;
                    if (y == sy)
                    {
                        world.TileType[idx] = 2; // Grass
                    }
                    else if (y < sy + 14 + (x % 3))
                    {
                        world.TileType[idx] = 0; // Dirt
                        if (y > sy + 1) world.WallType[idx] = 2; // Dirt wall
                    }
                    else
                    {
                        world.TileType[idx] = (y > sy + 35 && ((x * 31 + y * 17) % 97 == 0))
                            ? (ushort)6  // Iron ore vein
                            : (ushort)1; // Stone
                        world.WallType[idx] = 1; // Stone wall
                    }
                }
            }

            world.SpawnY = surfaceBase - 1;
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    TileFraming.FrameTile(world, x, y);

            return world;
        }

        private static bool[] CreateDefaultImportance(int size)
        {
            var arr = new bool[size];
            ushort[] important = { 3, 4, 5, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 21, 24, 26, 27, 28, 29, 31, 33, 49, 50, 55, 61, 71, 73, 74, 77, 78, 79, 81, 82, 83, 84, 85, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 114, 125, 126, 128, 129, 132, 133, 134, 135, 136, 137, 138, 139, 141, 142, 143, 144, 149, 165, 171, 172, 173, 174, 178, 184, 185, 186, 187, 201, 207, 209, 210, 212, 215, 216, 217, 218, 219, 220, 227, 228, 231, 235, 236, 237, 238, 239, 240, 241, 242, 243, 244, 245, 246, 247, 254, 269, 270, 271, 275, 276, 277, 278, 279, 280, 281, 282, 283, 285, 286, 287, 288, 289, 290, 291, 292, 293, 294, 295, 296, 297, 300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 316, 317, 318, 319, 320, 324, 334, 335, 337, 338, 339, 349, 354, 355, 356, 358, 359, 360, 361, 362, 363, 364, 372, 373, 374, 375, 376, 377, 378, 380, 386, 387, 388, 389, 390, 391, 392, 393, 394, 405, 406, 410, 411, 412, 419, 420, 423, 424, 425, 428, 429, 440, 441, 442, 443, 444, 445, 452, 453, 454, 455, 456, 457, 463, 464, 465, 467, 468, 469, 470, 471, 475, 476, 480, 484, 485, 486, 487, 488, 489, 490, 491, 493, 494, 497, 499, 505, 506, 509, 510, 511, 518, 519, 521, 522, 523, 524, 525, 526, 527, 530, 531, 532, 533, 542, 543, 544, 545, 547, 548, 549, 550, 551, 552, 553, 554, 555, 556, 557, 558, 559, 560, 564, 565, 568, 569, 570, 572, 573, 574, 575, 576, 577, 578, 579, 580, 581, 582, 583, 584, 585, 586, 587, 588, 589, 590, 591, 592, 593, 594, 595, 596, 597, 598, 599, 600, 601, 602, 603, 604, 605, 606, 607, 608, 609, 610, 611, 612, 613, 614, 615, 616, 617, 619, 621, 622, 623, 624, 629, 630, 631, 632, 633, 634, 635, 636, 637, 638, 639, 640, 641, 642, 643, 644, 645, 646, 647, 648, 649, 650, 651, 652, 653, 654, 655, 656 };
            for (int i = 0; i < important.Length; i++)
                if (important[i] < arr.Length)
                    arr[important[i]] = true;
            return arr;
        }
    }
}
