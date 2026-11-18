using UnityEditor;
using UnityEngine;

namespace TerrariaMod.Editor
{
    /// <summary>
    /// Automatically configures unpacked Terraria sprites under Assets/Sprites/
    /// as pixel-art 2D Sprites (Point filtering, Uncompressed, PPU = 16).
    /// </summary>
    public class TerrariaSpritePostprocessor : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').StartsWith("Assets/Sprites/"))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 16f;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 2048;
            importer.alphaIsTransparency = true;
        }
    }
}
