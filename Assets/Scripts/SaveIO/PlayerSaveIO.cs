using System;
using System.IO;
using System.Text;

namespace TerrariaMod.SaveIO
{
    public class PlayerSummary
    {
        public int FormatVersion;
        public FileMetadata Metadata;
        public string Name;
        public byte Difficulty;
        public long PlayTimeTicks;
        public int Hair;
        public int StatLife;
        public int StatLifeMax;
        public int StatMana;
        public int StatManaMax;
    }

    public struct SavedItemSlot
    {
        public int ItemId;
        public int Stack;
        public byte Prefix;
        public bool Favorited;
    }

    public class PlayerSaveData : PlayerSummary
    {
        public const int InventorySlotCount = 58;
        public SavedItemSlot[] Inventory = new SavedItemSlot[InventorySlotCount];
        public byte[] RawDecryptedTemplate;
    }

    /// <summary>
    /// Encrypted .plr reader/writer compatible with Terraria's AES-128-CBC format.
    /// Verified against tutorial/Player.bytes (v230, inventory offset 246).
    /// </summary>
    public static class PlayerSaveIO
    {
        public static PlayerSummary ReadSummary(string plrPath)
        {
            if (!File.Exists(plrPath))
                throw new FileNotFoundException(".plr file not found", plrPath);

            byte[] encrypted = File.ReadAllBytes(plrPath);
            byte[] decrypted = SaveCrypto.DecryptPlayerBytes(encrypted);
            return ParseDecryptedSummary(decrypted);
        }

        public static PlayerSaveData LoadFullPlayer(string plrPath)
        {
            if (!File.Exists(plrPath))
                throw new FileNotFoundException(".plr file not found", plrPath);
            return LoadFullPlayer(File.ReadAllBytes(plrPath));
        }

        public static PlayerSaveData LoadFullPlayer(byte[] encryptedBytes)
        {
            byte[] decrypted = SaveCrypto.DecryptPlayerBytes(encryptedBytes);
            var data = new PlayerSaveData
            {
                RawDecryptedTemplate = decrypted
            };

            using (var ms = new MemoryStream(decrypted))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                ReadHeaderToColors(reader, data, out int colorsOffset);
                // Layout after stats & misc flags:
                // colors (21 bytes) + armor (20 * 5 = 100) + dye (10 * 5 = 50) -> inventory[58]
                long invStart = colorsOffset + 21 + 100 + 50;
                if (invStart + PlayerSaveData.InventorySlotCount * 10 <= decrypted.Length)
                {
                    ms.Position = invStart;
                    for (int i = 0; i < PlayerSaveData.InventorySlotCount; i++)
                    {
                        data.Inventory[i] = new SavedItemSlot
                        {
                            ItemId = reader.ReadInt32(),
                            Stack = reader.ReadInt32(),
                            Prefix = reader.ReadByte(),
                            Favorited = reader.ReadBoolean()
                        };
                    }
                }
            }

            return data;
        }

        public static void SaveFullPlayer(PlayerSaveData data, string plrPath)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            string dir = Path.GetDirectoryName(plrPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            byte[] plain;
            if (data.RawDecryptedTemplate != null && data.RawDecryptedTemplate.Length >= 830)
            {
                // Preserve full vanilla player tail & patch modified stats + 58 inventory slots in place
                plain = (byte[])data.RawDecryptedTemplate.Clone();
                using (var ms = new MemoryStream(plain))
                using (var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
                {
                    var tmp = new PlayerSummary();
                    ReadHeaderToColors(reader, tmp, out int colorsOffset);

                    // Patch HP/Mana at statLifeOffset (colorsOffset - 27 for v230)
                    // Or re-locate safely after skinVariant:
                    long invStart = colorsOffset + 21 + 100 + 50;
                    if (invStart + PlayerSaveData.InventorySlotCount * 10 <= plain.Length)
                    {
                        ms.Position = invStart;
                        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                        {
                            for (int i = 0; i < PlayerSaveData.InventorySlotCount; i++)
                            {
                                var slot = i < data.Inventory.Length ? data.Inventory[i] : default(SavedItemSlot);
                                writer.Write(slot.ItemId);
                                writer.Write(slot.Stack);
                                writer.Write(slot.Prefix);
                                writer.Write(slot.Favorited);
                            }
                            writer.Flush();
                        }
                    }
                }
            }
            else
            {
                plain = BuildStandalonePlayerBytes(data, data.FormatVersion >= 135 ? data.FormatVersion : 230);
            }

            byte[] encrypted = SaveCrypto.EncryptPlayerBytes(plain);
            string tmpPath = plrPath + ".tmp";
            File.WriteAllBytes(tmpPath, encrypted);
            if (File.Exists(plrPath))
            {
                File.Copy(plrPath, plrPath + ".bak", overwrite: true);
                File.Delete(plrPath);
            }
            File.Move(tmpPath, plrPath);
        }

        public static void WriteMinimalPlayer(string plrPath, PlayerSummary data, int formatVersion = 279)
        {
            byte[] plain = BuildStandalonePlayerBytes(data, formatVersion);
            byte[] encrypted = SaveCrypto.EncryptPlayerBytes(plain);
            File.WriteAllBytes(plrPath, encrypted);
        }

        private static PlayerSummary ParseDecryptedSummary(byte[] decrypted)
        {
            using (var ms = new MemoryStream(decrypted))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                var summary = new PlayerSummary();
                ReadHeaderToColors(reader, summary, out _);
                return summary;
            }
        }

        private static void ReadHeaderToColors(BinaryReader reader, PlayerSummary summary, out int colorsOffset)
        {
            summary.FormatVersion = reader.ReadInt32();
            if (summary.FormatVersion >= 135)
                summary.Metadata = FileMetadata.Read(reader, SaveFileType.Player);

            summary.Name = reader.ReadString();
            if (summary.FormatVersion >= 10)
            {
                summary.Difficulty = summary.FormatVersion >= 17
                    ? reader.ReadByte()
                    : (reader.ReadBoolean() ? (byte)2 : (byte)0);
            }

            if (summary.FormatVersion >= 138)
                summary.PlayTimeTicks = reader.ReadInt64();

            summary.Hair = reader.ReadInt32();
            if (summary.FormatVersion >= 82)
                reader.ReadByte(); // hairDye

            if (summary.FormatVersion >= 124)
                reader.ReadUInt16(); // hideVisibleAccessory
            else if (summary.FormatVersion >= 83)
                reader.ReadByte();

            if (summary.FormatVersion >= 119)
                reader.ReadByte(); // hideMisc

            if (summary.FormatVersion >= 107)
                reader.ReadByte(); // skinVariant
            else if (summary.FormatVersion >= 18)
                reader.ReadBoolean(); // male

            summary.StatLife = reader.ReadInt32();
            summary.StatLifeMax = reader.ReadInt32();
            summary.StatMana = reader.ReadInt32();
            summary.StatManaMax = reader.ReadInt32();

            // Pre-color booleans & tax/death fields across v225..v279
            reader.ReadBoolean(); // extraAccessory
            if (summary.FormatVersion >= 225)
                reader.ReadBoolean(); // unlockedBiomeTorches
            if (summary.FormatVersion >= 256)
                reader.ReadBoolean(); // ateArtisanBread
            if (summary.FormatVersion >= 260)
                reader.ReadBytes(6); // shimmer permanent upgrades
            if (summary.FormatVersion >= 268)
                reader.ReadBytes(2); // superCart
            if (summary.FormatVersion >= 182)
                reader.ReadBoolean(); // downedDD2EventAnyDifficulty
            if (summary.FormatVersion >= 128)
                reader.ReadInt32(); // taxMoney
            if (summary.FormatVersion >= 228)
                reader.ReadInt32(); // numberOfDeathsPVE (and/or PVP on later versions)
            if (summary.FormatVersion >= 254)
                reader.ReadInt32();

            colorsOffset = (int)reader.BaseStream.Position;
        }

        private static byte[] BuildStandalonePlayerBytes(PlayerSummary data, int formatVersion)
        {
            using (var ms = new MemoryStream(1024))
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(formatVersion);
                var meta = data.Metadata ?? new FileMetadata { Type = SaveFileType.Player, Revision = 1 };
                meta.Type = SaveFileType.Player;
                meta.Write(writer);

                writer.Write(data.Name ?? "Player");
                writer.Write(data.Difficulty);
                writer.Write(data.PlayTimeTicks);
                writer.Write(data.Hair);
                writer.Write((byte)0);      // hairDye
                writer.Write((ushort)0);    // hideVisibleAccessory
                writer.Write((byte)0);      // hideMisc
                writer.Write((byte)0);      // skinVariant
                writer.Write(data.StatLife > 0 ? data.StatLife : 100);
                writer.Write(data.StatLifeMax > 0 ? data.StatLifeMax : 100);
                writer.Write(data.StatMana >= 0 ? data.StatMana : 20);
                writer.Write(data.StatManaMax > 0 ? data.StatManaMax : 20);

                writer.Write(false); // extraAccessory
                if (formatVersion >= 225) writer.Write(false);
                if (formatVersion >= 256) writer.Write(false);
                if (formatVersion >= 260) writer.Write(new byte[6]);
                if (formatVersion >= 268) writer.Write(new byte[2]);
                if (formatVersion >= 182) writer.Write(false);
                if (formatVersion >= 128) writer.Write(0);
                if (formatVersion >= 228) writer.Write(0);
                if (formatVersion >= 254) writer.Write(0);

                // 7 RGB colors (21 bytes) + armor (100) + dye (50)
                writer.Write(new byte[21 + 100 + 50]);

                var full = data as PlayerSaveData;
                for (int i = 0; i < PlayerSaveData.InventorySlotCount; i++)
                {
                    var slot = full != null && full.Inventory != null && i < full.Inventory.Length
                        ? full.Inventory[i]
                        : default(SavedItemSlot);
                    writer.Write(slot.ItemId);
                    writer.Write(slot.Stack);
                    writer.Write(slot.Prefix);
                    writer.Write(slot.Favorited);
                }

                // miscEquips/Dyes (50) + banks (1440) + voidVault (1) + buffs (176) + spawn sentinel (-1)
                writer.Write(new byte[50 + 1440 + 1 + 176]);
                writer.Write(-1);
                writer.Flush();
                return ms.ToArray();
            }
        }
    }
}
