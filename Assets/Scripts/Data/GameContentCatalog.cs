using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrariaMod.Data
{
    [Serializable]
    public struct IntTextureEntry
    {
        public int id;
        public Texture2D texture;
    }

    [Serializable]
    public struct IntSpriteEntry
    {
        public int id;
        public Sprite sprite;
    }

    [Serializable]
    public struct NamedTextureEntry
    {
        public string key;
        public Texture2D texture;
    }

    /// <summary>
    /// Pre-indexed runtime asset catalog built by Editor tooling (Phase3Setup).
    /// Allows runtime access to Assets/Sprites/* and Assets/Data/Items.json across Editor & builds
    /// without relocating 16,000+ sprites into Resources/.
    /// </summary>
    [CreateAssetMenu(fileName = "GameContentCatalog", menuName = "TerrariaMod/Content Catalog")]
    public class GameContentCatalog : ScriptableObject
    {
        public TextAsset itemsJson;
        public TextAsset recipesJson;
        public TextAsset tutorialWorldBytes;
        public TextAsset tutorialPlayerBytes;

        public List<IntTextureEntry> tileTextures = new List<IntTextureEntry>();
        public List<IntTextureEntry> wallTextures = new List<IntTextureEntry>();
        public List<IntSpriteEntry> itemSprites = new List<IntSpriteEntry>();
        public List<NamedTextureEntry> playerTextures = new List<NamedTextureEntry>();

        [NonSerialized] private Dictionary<int, Texture2D> _tileMap;
        [NonSerialized] private Dictionary<int, Texture2D> _wallMap;
        [NonSerialized] private Dictionary<int, Sprite> _itemMap;
        [NonSerialized] private Dictionary<string, Texture2D> _playerMap;

        public void EnsureIndex()
        {
            if (_tileMap != null) return;

            _tileMap = new Dictionary<int, Texture2D>(tileTextures.Count);
            for (int i = 0; i < tileTextures.Count; i++)
                if (tileTextures[i].texture != null)
                    _tileMap[tileTextures[i].id] = tileTextures[i].texture;

            _wallMap = new Dictionary<int, Texture2D>(wallTextures.Count);
            for (int i = 0; i < wallTextures.Count; i++)
                if (wallTextures[i].texture != null)
                    _wallMap[wallTextures[i].id] = wallTextures[i].texture;

            _itemMap = new Dictionary<int, Sprite>(itemSprites.Count);
            for (int i = 0; i < itemSprites.Count; i++)
                if (itemSprites[i].sprite != null)
                    _itemMap[itemSprites[i].id] = itemSprites[i].sprite;

            _playerMap = new Dictionary<string, Texture2D>(playerTextures.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < playerTextures.Count; i++)
                if (!string.IsNullOrEmpty(playerTextures[i].key) && playerTextures[i].texture != null)
                    _playerMap[playerTextures[i].key] = playerTextures[i].texture;
        }

        public Texture2D GetTileTexture(int tileId)
        {
            EnsureIndex();
            return _tileMap.TryGetValue(tileId, out var tex) ? tex : null;
        }

        public Texture2D GetWallTexture(int wallId)
        {
            EnsureIndex();
            return _wallMap.TryGetValue(wallId, out var tex) ? tex : null;
        }

        public Sprite GetItemSprite(int itemId)
        {
            EnsureIndex();
            return _itemMap.TryGetValue(itemId, out var sp) ? sp : null;
        }

        public Texture2D GetPlayerTexture(string key)
        {
            EnsureIndex();
            return _playerMap.TryGetValue(key, out var tex) ? tex : null;
        }
    }
}
