using System;
using System.IO;

namespace TerrariaMod.SaveIO
{
    public enum SaveFileType : byte
    {
        None = 0,
        Map = 1,
        World = 2,
        Player = 3
    }

    /// <summary>
    /// 20-byte Terraria file metadata header verified from Terraria.IO.FileMetadata (TypeDefIndex 1590):
    /// [0..6] "relogic", [7] SaveFileType, [8..11] uint Revision, [12..19] ulong Flags (bit0 = IsFavorite).
    /// </summary>
    public class FileMetadata
    {
        public const ulong MagicMask = 0x00FFFFFFFFFFFFFFUL;
        public const ulong MagicNumber = 27981915666277746UL; // "relogic\0" little-endian
        public const int HeaderSize = 20;

        public SaveFileType Type { get; set; }
        public uint Revision { get; set; }
        public bool IsFavorite { get; set; }

        public void Write(BinaryWriter writer)
        {
            ulong magicWithType = MagicNumber | ((ulong)Type << 56);
            writer.Write(magicWithType);
            writer.Write(Revision);
            ulong flags = IsFavorite ? 1UL : 0UL;
            writer.Write(flags);
        }

        public static FileMetadata Read(BinaryReader reader, SaveFileType expectedType)
        {
            ulong rawMagic = reader.ReadUInt64();
            if ((rawMagic & MagicMask) != MagicNumber)
                throw new InvalidDataException($"Invalid Terraria magic header: 0x{rawMagic:X16}");

            var type = (SaveFileType)((rawMagic >> 56) & 0xFF);
            if (expectedType != SaveFileType.None && type != expectedType)
                throw new InvalidDataException($"Unexpected FileType {type}, expected {expectedType}");

            uint revision = reader.ReadUInt32();
            ulong flags = reader.ReadUInt64();

            return new FileMetadata
            {
                Type = type,
                Revision = revision,
                IsFavorite = (flags & 1UL) != 0
            };
        }
    }
}
