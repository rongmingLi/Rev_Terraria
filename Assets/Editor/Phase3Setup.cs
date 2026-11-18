using System;
using System.IO;
using System.Text.RegularExpressions;
using TerrariaMod.Core;
using TerrariaMod.Data;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TerrariaMod.Editor
{
    /// <summary>
    /// Builds Assets/Resources/GameContentCatalog.asset and Assets/Scenes/PlayableLoop.unity
    /// cleanly for Phase 3 playable loop verification in Editor and runtime builds.
    /// </summary>
    [InitializeOnLoad]
    public static class Phase3Setup
    {
        private const string CatalogPath = "Assets/Resources/GameContentCatalog.asset";
        private const string ScenePath = "Assets/Scenes/PlayableLoop.unity";

        static Phase3Setup()
        {
            EditorApplication.delayCall += EnsureSetupOnFirstOpen;
        }

        private static void EnsureSetupOnFirstOpen()
        {
            if (Application.isBatchMode) return;
            bool needCatalog = !File.Exists(CatalogPath);
            if (!needCatalog)
            {
                var existing = AssetDatabase.LoadAssetAtPath<GameContentCatalog>(CatalogPath);
                if (existing == null ||
                    existing.GetPlayerTexture("Player_Hair_1") == null ||
                    existing.GetPlayerTexture("Tree_Tops_0") == null ||
                    existing.GetPlayerTexture("Background_7") == null ||
                    existing.GetPlayerTexture("TileCracks") == null)
                    needCatalog = true;
            }
            bool needScene = !File.Exists(ScenePath);
            if (!needCatalog && !needScene) return;

            if (needCatalog) BuildCatalog();
            if (needScene)
            {
                BuildPlayableScene();
                EditorSceneManager.OpenScene(ScenePath);
            }
        }

        [MenuItem("TerrariaMod/Build Phase 3 Scene & Catalog")]
        public static void BuildAllMenu()
        {
            BuildCatalog();
            BuildPlayableScene();
            Debug.Log("[TerrariaMod] Phase 3 Catalog and PlayableLoop scene built successfully.");
        }

        public static void BuildAllBatch()
        {
            BuildCatalog();
            BuildPlayableScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[TerrariaMod] Batchmode Phase 3 setup completed.");
        }

        public static GameContentCatalog BuildCatalog()
        {
            if (!Directory.Exists("Assets/Resources"))
                Directory.CreateDirectory("Assets/Resources");

            var catalog = AssetDatabase.LoadAssetAtPath<GameContentCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<GameContentCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            catalog.itemsJson = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/Items.json");
            catalog.recipesJson = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Data/Recipes.json");
            catalog.tutorialWorldBytes = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/tutorial/World.bytes");
            catalog.tutorialPlayerBytes = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Resources/tutorial/Player.bytes");

            catalog.tileTextures.Clear();
            foreach (string path in Directory.GetFiles("Assets/Sprites/Tiles", "Tiles_*.png"))
            {
                string file = Path.GetFileName(path);
                var m = Regex.Match(file, @"^Tiles_(\d+)\.png$");
                if (!m.Success) continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path.Replace('\\', '/'));
                if (tex != null)
                    catalog.tileTextures.Add(new IntTextureEntry { id = int.Parse(m.Groups[1].Value), texture = tex });
            }

            catalog.wallTextures.Clear();
            foreach (string path in Directory.GetFiles("Assets/Sprites/Walls", "Wall_*.png"))
            {
                string file = Path.GetFileName(path);
                var m = Regex.Match(file, @"^Wall_(\d+)\.png$");
                if (!m.Success) continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path.Replace('\\', '/'));
                if (tex != null)
                    catalog.wallTextures.Add(new IntTextureEntry { id = int.Parse(m.Groups[1].Value), texture = tex });
            }

            catalog.itemSprites.Clear();
            foreach (string path in Directory.GetFiles("Assets/Sprites/Item", "Item_*.png"))
            {
                string file = Path.GetFileName(path);
                var m = Regex.Match(file, @"^Item_(\d+)(?:_.*)?\.png$");
                if (!m.Success) continue;
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path.Replace('\\', '/'));
                if (sprite != null)
                    catalog.itemSprites.Add(new IntSpriteEntry { id = int.Parse(m.Groups[1].Value), sprite = sprite });
            }

            catalog.playerTextures.Clear();
            foreach (string path in Directory.GetFiles("Assets/Sprites/Player", "Player_0_*.png"))
            {
                string key = Path.GetFileNameWithoutExtension(path);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path.Replace('\\', '/'));
                if (tex != null)
                    catalog.playerTextures.Add(new NamedTextureEntry { key = key, texture = tex });
            }
            foreach (string path in Directory.GetFiles("Assets/Sprites/Player", "Player_Hair_*.png"))
            {
                string key = Path.GetFileNameWithoutExtension(path);
                if (!Regex.IsMatch(key, @"^Player_Hair_([1-9]|1[0-9])$")) continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path.Replace('\\', '/'));
                if (tex != null)
                    catalog.playerTextures.Add(new NamedTextureEntry { key = key, texture = tex });
            }
            if (Directory.Exists("Assets/Sprites/Trees"))
            {
                foreach (string path in Directory.GetFiles("Assets/Sprites/Trees", "Tree_*.png"))
                {
                    string key = Path.GetFileNameWithoutExtension(path);
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path.Replace('\\', '/'));
                    if (tex != null)
                        catalog.playerTextures.Add(new NamedTextureEntry { key = key, texture = tex });
                }
            }
            if (Directory.Exists("Assets/Sprites/Background"))
            {
                int[] bgIds = { 0, 1, 2, 3, 4, 5, 7, 9, 11, 55 };
                for (int i = 0; i < bgIds.Length; i++)
                {
                    string key = $"Background_{bgIds[i]}";
                    string path = $"Assets/Sprites/Background/{key}.png";
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (tex != null)
                        catalog.playerTextures.Add(new NamedTextureEntry { key = key, texture = tex });
                }
            }
            var cracksTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Sprites/Standalone/TileCracks.png");
            if (cracksTex != null)
                catalog.playerTextures.Add(new NamedTextureEntry { key = "TileCracks", texture = cracksTex });

            string[] liquidKeys = { "Liquid_0", "Liquid_1", "Liquid_11", "Liquid_14" };
            for (int i = 0; i < liquidKeys.Length; i++)
            {
                var ltex = AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/Sprites/Standalone/{liquidKeys[i]}.png");
                if (ltex != null)
                    catalog.playerTextures.Add(new NamedTextureEntry { key = liquidKeys[i], texture = ltex });
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TerrariaMod] Catalog indexed: tiles={catalog.tileTextures.Count}, walls={catalog.wallTextures.Count}, items={catalog.itemSprites.Count}, playerLayers={catalog.playerTextures.Count}");
            return catalog;
        }

        public static void BuildPlayableScene()
        {
            if (!Directory.Exists("Assets/Scenes"))
                Directory.CreateDirectory("Assets/Scenes");

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 14f;
            cam.backgroundColor = new Color(0.39f, 0.62f, 0.94f, 1f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(0f, 0f, -10f);

            var bootGo = new GameObject("GameBootstrap");
            var boot = bootGo.AddComponent<GameBootstrap>();
            boot.catalog = AssetDatabase.LoadAssetAtPath<GameContentCatalog>(CatalogPath);
            boot.mainCamera = cam;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log($"[TerrariaMod] Saved scene to {ScenePath} and registered in Build Settings.");
        }
    }
}
