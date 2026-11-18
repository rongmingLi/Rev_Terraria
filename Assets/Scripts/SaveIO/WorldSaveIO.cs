using System;
using System.IO;
using System.Text;
using TerrariaMod.World;

namespace TerrariaMod.SaveIO
{
    public class WorldHeaderSummary
    {
        public int FormatVersion;
        public FileMetadata Metadata;
        public int[] SectionOffsets;
        public bool[] TileFrameImportant;

        public string WorldName;
        public string SeedText;
        public ulong WorldGeneratorVersion;
        public byte[] UniqueId;
        public int WorldId;

        public int LeftWorld;
        public int RightWorld;
        public int TopWorld;
        public int BottomWorld;
        public int MaxTilesY;
        public int MaxTilesX;

        public int GameMode;
        public bool DrunkWorld;
        public bool ForTheWorthyWorld;
        public bool TenthAnniversaryWorld;
        public bool DontStarveWorld;
        public bool NotTheBeesWorld;
        public bool RemixWorld;
        public bool NoTrapsWorld;
        public bool ZenithWorld;

        public int SpawnTileX;
        public int SpawnTileY;
        public double SurfaceLayer;
        public double RockLayer;
        public bool FooterValid;
    }

    /// <summary>
    /// Unencrypted .wld reader/writer compatible with Terraria v2 format
    /// (verified against Terraria.IO.WorldFile.SaveWorld_Version2 @ 0x1A1A864 and tutorial/World.bytes).
    /// </summary>
    public static class WorldSaveIO
    {
        public static WorldHeaderSummary ReadSummary(string wldPath)
        {
            if (!File.Exists(wldPath))
                throw new FileNotFoundException(".wld file not found", wldPath);

            using (var fs = File.OpenRead(wldPath))
            using (var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
            {
                return ReadSummary(reader);
            }
        }

        public static WorldHeaderSummary ReadSummary(BinaryReader reader)
        {
            var summary = new WorldHeaderSummary();
            summary.FormatVersion = reader.ReadInt32();
            if (summary.FormatVersion < 135)
                throw new NotSupportedException($"Legacy .wld format {summary.FormatVersion} is not supported by v2 reader.");

            summary.Metadata = FileMetadata.Read(reader, SaveFileType.World);

            short sectionCount = reader.ReadInt16();
            summary.SectionOffsets = new int[sectionCount];
            for (int i = 0; i < sectionCount; i++)
                summary.SectionOffsets[i] = reader.ReadInt32();

            summary.TileFrameImportant = ReadImportanceBits(reader);

            if (summary.SectionOffsets.Length >= 2)
            {
                reader.BaseStream.Position = summary.SectionOffsets[0];
                ReadHeaderCore(reader, summary);

                int footerOffset = summary.SectionOffsets[summary.SectionOffsets.Length - 1];
                if (footerOffset > 0 && footerOffset < reader.BaseStream.Length)
                {
                    reader.BaseStream.Position = footerOffset;
                    bool marker = reader.ReadBoolean();
                    string footerName = reader.ReadString();
                    int footerId = reader.ReadInt32();
                    summary.FooterValid = marker && footerName == summary.WorldName && footerId == summary.WorldId;
                }
            }

            return summary;
        }

        public static WorldMap LoadWorld(string wldPath)
        {
            if (!File.Exists(wldPath))
                throw new FileNotFoundException(".wld file not found", wldPath);
            return LoadWorld(File.ReadAllBytes(wldPath));
        }

        public static WorldMap LoadWorld(byte[] wldBytes)
        {
            if (wldBytes == null || wldBytes.Length < 64)
                throw new InvalidDataException("Invalid .wld byte buffer.");

            using (var ms = new MemoryStream(wldBytes, writable: false))
            using (var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false))
            {
                WorldHeaderSummary summary = ReadSummary(reader);
                var world = new WorldMap(summary.MaxTilesX, summary.MaxTilesY, summary.TileFrameImportant)
                {
                    FormatVersion = summary.FormatVersion,
                    WorldName = summary.WorldName,
                    SeedText = summary.SeedText,
                    WorldGeneratorVersion = summary.WorldGeneratorVersion,
                    UniqueId = summary.UniqueId ?? new byte[16],
                    WorldId = summary.WorldId,
                    GameMode = summary.GameMode,
                    SpawnX = summary.SpawnTileX,
                    SpawnY = summary.SpawnTileY,
                    SurfaceLayer = summary.SurfaceLayer,
                    RockLayer = summary.RockLayer
                };

                if (summary.SectionOffsets.Length >= 2)
                {
                    ms.Position = summary.SectionOffsets[1];
                    LoadWorldTiles(reader, world, summary.FormatVersion, summary.TileFrameImportant);

                    if (summary.SectionOffsets.Length >= 3)
                    {
                        int sec2 = summary.SectionOffsets[2];
                        if (sec2 >= 0 && sec2 < wldBytes.Length)
                        {
                            // Store full trailing blob from section 2 onward AND preserve section 0 header
                            int headerLen = summary.SectionOffsets[1] - summary.SectionOffsets[0];
                            int tailLen = wldBytes.Length - sec2;
                            world.TrailingSectionsBlob = BuildPreservedBlob(wldBytes, summary.SectionOffsets[0], headerLen, summary.SectionOffsets, sec2, tailLen);
                        }
                    }
                }

                return world;
            }
        }

        public static void SaveWorld(WorldMap world, string wldPath)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            string dir = Path.GetDirectoryName(wldPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            byte[] data = SerializeWorld(world);
            string tmpPath = wldPath + ".tmp";
            File.WriteAllBytes(tmpPath, data);
            if (File.Exists(wldPath))
            {
                string bakPath = wldPath + ".bak";
                File.Copy(wldPath, bakPath, overwrite: true);
                File.Delete(wldPath);
            }
            File.Move(tmpPath, wldPath);
        }

        public static byte[] SerializeWorld(WorldMap world)
        {
            using (var ms = new MemoryStream(256 * 1024))
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                int version = world.FormatVersion >= 135 ? world.FormatVersion : 230;
                writer.Write(version);
                new FileMetadata { Type = SaveFileType.World, Revision = 1 }.Write(writer);

                int[] oldSections;
                byte[] preservedHeader;
                byte[] preservedTail;
                TryUnpackPreservedBlob(world.TrailingSectionsBlob, out oldSections, out preservedHeader, out preservedTail);

                short sectionCount = oldSections != null && oldSections.Length >= 3 ? (short)oldSections.Length : (short)11;
                writer.Write(sectionCount);
                long sectionTablePos = ms.Position;
                for (int i = 0; i < sectionCount; i++)
                    writer.Write(0);

                WriteImportanceBits(writer, world.TileFrameImportant);

                int[] newSections = new int[sectionCount];
                newSections[0] = (int)ms.Position;

                if (preservedHeader != null && preservedHeader.Length > 0)
                    writer.Write(preservedHeader);
                else
                    WriteMinimalHeader(writer, world, version);

                newSections[1] = (int)ms.Position;
                SaveWorldTiles(writer, world, version);

                newSections[2] = (int)ms.Position;
                if (preservedTail != null && preservedTail.Length > 0 && oldSections != null && oldSections.Length == sectionCount)
                {
                    int delta = newSections[2] - oldSections[2];
                    for (int i = 3; i < sectionCount; i++)
                        newSections[i] = oldSections[i] + delta;
                    writer.Write(preservedTail);
                }
                else
                {
                    // Empty valid chests/signs/NPCs/tileEntities/footer sections
                    writer.Write((short)0); // chests
                    writer.Write((short)0); // signs
                    for (int i = 3; i < sectionCount - 1; i++)
                        newSections[i] = (int)ms.Position;
                    newSections[sectionCount - 1] = (int)ms.Position;
                    writer.Write(true);
                    writer.Write(world.WorldName ?? "World");
                    writer.Write(world.WorldId);
                }

                writer.Flush();
                ms.Position = sectionTablePos;
                for (int i = 0; i < sectionCount; i++)
                    writer.Write(newSections[i]);
                writer.Flush();
                return ms.ToArray();
            }
        }

        private static bool[] ReadImportanceBits(BinaryReader reader)
        {
            short importanceCount = reader.ReadInt16();
            bool[] importance = new bool[importanceCount];
            byte currentByte = 0;
            byte bitMask = 128;
            for (int i = 0; i < importanceCount; i++)
            {
                if (bitMask == 128)
                {
                    currentByte = reader.ReadByte();
                    bitMask = 1;
                }
                else
                {
                    bitMask <<= 1;
                }
                importance[i] = (currentByte & bitMask) != 0;
            }
            return importance;
        }

        private static void WriteImportanceBits(BinaryWriter writer, bool[] importance)
        {
            short count = (short)(importance != null ? importance.Length : 0);
            writer.Write(count);
            byte currentByte = 0;
            byte bitMask = 1;
            for (int i = 0; i < count; i++)
            {
                if (importance[i])
                    currentByte |= bitMask;
                if (bitMask == 128)
                {
                    writer.Write(currentByte);
                    currentByte = 0;
                    bitMask = 1;
                }
                else
                {
                    bitMask <<= 1;
                }
            }
            if (bitMask != 1)
                writer.Write(currentByte);
        }

        private static void ReadHeaderCore(BinaryReader reader, WorldHeaderSummary summary)
        {
            int ver = summary.FormatVersion;
            summary.WorldName = reader.ReadString();
            summary.SeedText = reader.ReadString();
            summary.WorldGeneratorVersion = reader.ReadUInt64();
            summary.UniqueId = reader.ReadBytes(16);
            summary.WorldId = reader.ReadInt32();

            summary.LeftWorld = reader.ReadInt32();
            summary.RightWorld = reader.ReadInt32();
            summary.TopWorld = reader.ReadInt32();
            summary.BottomWorld = reader.ReadInt32();
            summary.MaxTilesY = reader.ReadInt32();
            summary.MaxTilesX = reader.ReadInt32();

            summary.GameMode = ver >= 209 ? reader.ReadInt32() : (reader.ReadBoolean() ? 1 : 0);
            if (ver >= 222) summary.DrunkWorld = reader.ReadBoolean();
            if (ver >= 227) summary.ForTheWorthyWorld = reader.ReadBoolean();
            if (ver >= 238) summary.TenthAnniversaryWorld = reader.ReadBoolean();
            if (ver >= 239) summary.DontStarveWorld = reader.ReadBoolean();
            if (ver >= 241) summary.NotTheBeesWorld = reader.ReadBoolean();
            if (ver >= 249) summary.RemixWorld = reader.ReadBoolean();
            if (ver >= 266) summary.NoTrapsWorld = reader.ReadBoolean();
            if (ver >= 267) summary.ZenithWorld = reader.ReadBoolean();

            reader.ReadInt64(); // creationTime
            reader.ReadByte();  // moonType
            for (int i = 0; i < 3; i++) reader.ReadInt32(); // treeX
            for (int i = 0; i < 4; i++) reader.ReadInt32(); // treeStyle
            for (int i = 0; i < 3; i++) reader.ReadInt32(); // caveBackX
            for (int i = 0; i < 4; i++) reader.ReadInt32(); // caveBackStyle
            reader.ReadInt32(); // iceBackStyle
            reader.ReadInt32(); // jungleBackStyle
            reader.ReadInt32(); // hellBackStyle

            summary.SpawnTileX = reader.ReadInt32();
            summary.SpawnTileY = reader.ReadInt32();
            summary.SurfaceLayer = reader.ReadDouble();
            summary.RockLayer = reader.ReadDouble();
        }

        private static void WriteMinimalHeader(BinaryWriter writer, WorldMap world, int ver)
        {
            writer.Write(world.WorldName ?? "World");
            writer.Write(world.SeedText ?? "12345");
            writer.Write(world.WorldGeneratorVersion);
            byte[] uid = world.UniqueId != null && world.UniqueId.Length == 16 ? world.UniqueId : new byte[16];
            writer.Write(uid);
            writer.Write(world.WorldId);

            writer.Write(0);
            writer.Write(world.Width * 16);
            writer.Write(0);
            writer.Write(world.Height * 16);
            writer.Write(world.Height);
            writer.Write(world.Width);

            writer.Write(world.GameMode);
            if (ver >= 222) writer.Write(false);
            if (ver >= 227) writer.Write(false);
            if (ver >= 238) writer.Write(false);
            if (ver >= 239) writer.Write(false);
            if (ver >= 241) writer.Write(false);
            if (ver >= 249) writer.Write(false);
            if (ver >= 266) writer.Write(false);
            if (ver >= 267) writer.Write(false);

            writer.Write(DateTime.UtcNow.ToBinary());
            writer.Write((byte)0);
            for (int i = 0; i < 3; i++) writer.Write(world.Width);
            for (int i = 0; i < 4; i++) writer.Write(0);
            for (int i = 0; i < 3; i++) writer.Write(world.Width);
            for (int i = 0; i < 4; i++) writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            writer.Write(world.SpawnX);
            writer.Write(world.SpawnY);
            writer.Write(world.SurfaceLayer);
            writer.Write(world.RockLayer);
        }

        private static void LoadWorldTiles(BinaryReader reader, WorldMap world, int ver, bool[] importance)
        {
            int maxX = world.Width;
            int maxY = world.Height;
            for (int x = 0; x < maxX; x++)
            {
                int colBase = x * maxY;
                for (int y = 0; y < maxY; y++)
                {
                    byte b1 = reader.ReadByte();
                    byte b2 = 0, b3 = 0, b4 = 0;
                    if ((b1 & 1) != 0)
                    {
                        b2 = reader.ReadByte();
                        if ((b2 & 1) != 0)
                        {
                            b3 = reader.ReadByte();
                            if (ver >= 269 && (b3 & 1) != 0)
                                b4 = reader.ReadByte();
                        }
                    }

                    bool active = (b1 & 2) != 0;
                    ushort tileType = 0;
                    short fx = 0, fy = 0;
                    byte tColor = 0;
                    if (active)
                    {
                        tileType = (b1 & 32) != 0 ? reader.ReadUInt16() : reader.ReadByte();
                        if (importance != null && tileType < importance.Length && importance[tileType])
                        {
                            fx = reader.ReadInt16();
                            fy = reader.ReadInt16();
                        }
                        if ((b3 & 8) != 0)
                            tColor = reader.ReadByte();
                    }

                    ushort wallType = 0;
                    byte wColor = 0;
                    if ((b1 & 4) != 0)
                    {
                        wallType = reader.ReadByte();
                        if ((b3 & 16) != 0)
                            wColor = reader.ReadByte();
                    }

                    byte liquid = 0;
                    byte liqType = 0;
                    byte liqBits = (byte)((b1 & 24) >> 3);
                    if (liqBits != 0)
                    {
                        liquid = reader.ReadByte();
                        if (liqBits == 2) liqType = 1;
                        else if (liqBits == 3) liqType = 2;
                        else if ((b3 & 128) != 0) liqType = 3;
                    }

                    if ((b3 & 64) != 0)
                        wallType |= (ushort)(reader.ReadByte() << 8);

                    byte flags = (byte)(active ? 1 : 0);
                    int rleMode = (b1 & 192) >> 6;
                    int rle = rleMode == 0 ? 0 : (rleMode == 1 ? reader.ReadByte() : reader.ReadUInt16());

                    int endY = Math.Min(maxY - 1, y + rle);
                    for (int ry = y; ry <= endY; ry++)
                    {
                        int idx = colBase + ry;
                        world.Flags[idx] = flags;
                        world.TileType[idx] = tileType;
                        world.FrameX[idx] = fx;
                        world.FrameY[idx] = fy;
                        world.TileColor[idx] = tColor;
                        world.WallType[idx] = wallType;
                        world.WallColor[idx] = wColor;
                        world.Liquid[idx] = liquid;
                        world.LiquidType[idx] = liqType;
                    }
                    y = endY;
                }
            }
        }

        private static void SaveWorldTiles(BinaryWriter writer, WorldMap world, int ver)
        {
            int maxX = world.Width;
            int maxY = world.Height;
            bool[] imp = world.TileFrameImportant;
            byte[] buf = new byte[16];

            for (int x = 0; x < maxX; x++)
            {
                int colBase = x * maxY;
                int y = 0;
                while (y < maxY)
                {
                    int idx = colBase + y;
                    bool active = (world.Flags[idx] & 1) != 0;
                    ushort tType = world.TileType[idx];
                    short fx = world.FrameX[idx];
                    short fy = world.FrameY[idx];
                    byte tCol = world.TileColor[idx];
                    ushort wType = world.WallType[idx];
                    byte wCol = world.WallColor[idx];
                    byte liq = world.Liquid[idx];
                    byte liqType = world.LiquidType[idx];

                    int rle = 0;
                    while (y + rle + 1 < maxY && rle < ushort.MaxValue)
                    {
                        int n = colBase + y + rle + 1;
                        bool nActive = (world.Flags[n] & 1) != 0;
                        if (nActive != active) break;
                        if (active && (world.TileType[n] != tType || world.FrameX[n] != fx || world.FrameY[n] != fy || world.TileColor[n] != tCol))
                            break;
                        if (world.WallType[n] != wType || world.WallColor[n] != wCol || world.Liquid[n] != liq || world.LiquidType[n] != liqType)
                            break;
                        rle++;
                    }

                    byte b1 = 0, b2 = 0, b3 = 0;
                    int bodyPos = 3;

                    if (active)
                    {
                        b1 |= 2;
                        if (tType > 255)
                        {
                            b1 |= 32;
                            buf[bodyPos++] = (byte)(tType & 0xFF);
                            buf[bodyPos++] = (byte)(tType >> 8);
                        }
                        else
                        {
                            buf[bodyPos++] = (byte)tType;
                        }

                        if (imp != null && tType < imp.Length && imp[tType])
                        {
                            buf[bodyPos++] = (byte)(fx & 0xFF);
                            buf[bodyPos++] = (byte)(fx >> 8);
                            buf[bodyPos++] = (byte)(fy & 0xFF);
                            buf[bodyPos++] = (byte)(fy >> 8);
                        }

                        if (tCol > 0)
                        {
                            b3 |= 8;
                            buf[bodyPos++] = tCol;
                        }
                    }

                    if (wType > 0)
                    {
                        b1 |= 4;
                        buf[bodyPos++] = (byte)(wType & 0xFF);
                        if (wCol > 0)
                        {
                            b3 |= 16;
                            buf[bodyPos++] = wCol;
                        }
                    }

                    if (liq > 0)
                    {
                        if (liqType == 1) b1 |= 16;
                        else if (liqType == 2) b1 |= 24;
                        else b1 |= 8;
                        if (liqType == 3) b3 |= 128;
                        buf[bodyPos++] = liq;
                    }

                    if (wType > 255)
                    {
                        b3 |= 64;
                        buf[bodyPos++] = (byte)(wType >> 8);
                    }

                    if (rle > 0)
                    {
                        if (rle <= 255)
                        {
                            b1 |= 64;
                            buf[bodyPos++] = (byte)rle;
                        }
                        else
                        {
                            b1 |= 128;
                            buf[bodyPos++] = (byte)(rle & 0xFF);
                            buf[bodyPos++] = (byte)(rle >> 8);
                        }
                    }

                    if (b3 != 0) b2 |= 1;
                    if (b2 != 0) b1 |= 1;

                    int startPos = 2;
                    if (b3 != 0) buf[startPos] = b3;
                    else startPos = 3;

                    // Write headers cleanly right before payload
                    if (b3 != 0)
                    {
                        buf[0] = b1;
                        buf[1] = b2;
                        buf[2] = b3;
                        writer.Write(buf, 0, bodyPos);
                    }
                    else if (b2 != 0)
                    {
                        buf[1] = b1;
                        buf[2] = b2;
                        writer.Write(buf, 1, bodyPos - 1);
                    }
                    else
                    {
                        buf[2] = b1;
                        writer.Write(buf, 2, bodyPos - 2);
                    }

                    y += 1 + rle;
                }
            }
        }

        private static byte[] BuildPreservedBlob(byte[] src, int sec0, int headerLen, int[] sections, int sec2, int tailLen)
        {
            using (var ms = new MemoryStream(12 + sections.Length * 4 + headerLen + tailLen))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(sections.Length);
                for (int i = 0; i < sections.Length; i++)
                    bw.Write(sections[i]);
                bw.Write(headerLen);
                bw.Write(src, sec0, headerLen);
                bw.Write(tailLen);
                bw.Write(src, sec2, tailLen);
                return ms.ToArray();
            }
        }

        private static void TryUnpackPreservedBlob(byte[] blob, out int[] sections, out byte[] header, out byte[] tail)
        {
            sections = null;
            header = null;
            tail = null;
            if (blob == null || blob.Length < 12) return;

            using (var ms = new MemoryStream(blob))
            using (var br = new BinaryReader(ms))
            {
                int sc = br.ReadInt32();
                sections = new int[sc];
                for (int i = 0; i < sc; i++)
                    sections[i] = br.ReadInt32();
                int hLen = br.ReadInt32();
                header = br.ReadBytes(hLen);
                int tLen = br.ReadInt32();
                tail = br.ReadBytes(tLen);
            }
        }
    }
}
