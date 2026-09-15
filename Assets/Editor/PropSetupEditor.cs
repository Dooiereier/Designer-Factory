using UnityEditor;
using UnityEngine;

namespace DesignerBackgroundTest.EditorTools
{
    // Turns any static (non-rigged) model file dropped into Assets/Models/Props/
    // into a ready-to-use prefab - the same idea as DroodSetupEditor, but simpler
    // since props don't need a Humanoid rig/Avatar/Animator, just correct textures
    // and a saved prefab. Meant for free CC0/CC-BY models (e.g. from Poly Pizza) used
    // to replace a hand-built primitive prop (like the block couches) with something
    // more detailed, added into the F2 structure editor's catalog.
    //
    // Run via Tools > Designer Factory > Setup Custom Props after dropping one or
    // more .fbx/.obj files into that folder - it processes every model file found
    // there and is safe to re-run (e.g. after replacing a file).
    public static class PropSetupEditor
    {
        private const string PropsFolder = "Assets/Models/Props";

        [MenuItem("Tools/Designer Factory/Setup Custom Props")]
        public static void SetupAll()
        {
            if (!AssetDatabase.IsValidFolder(PropsFolder))
            {
                Debug.LogError("[PropSetup] " + PropsFolder + " doesn't exist yet - create it and drop a model file (.fbx/.obj) in first.");
                return;
            }

            string[] files = System.IO.Directory.GetFiles(PropsFolder);
            int count = 0;
            foreach (string file in files)
            {
                string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".fbx" && ext != ".obj")
                {
                    continue;
                }

                string assetPath = file.Replace('\\', '/');
                if (SetupOne(assetPath))
                {
                    count++;
                }
            }

            Debug.Log($"[PropSetup] Done - built {count} prop prefab(s) from {PropsFolder}.");
        }

        private static bool SetupOne(string modelPath)
        {
            ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[PropSetup] Could not get a ModelImporter for " + modelPath);
                return false;
            }

            // No rig needed for a static prop - explicitly None rather than leaving
            // whatever Unity guessed by default.
            importer.animationType = ModelImporterAnimationType.None;

            // Same fix as DroodSetupEditor: extract embedded textures to real assets
            // and re-link them, since Unity's default "keep embedded" material mode
            // doesn't reliably wire the diffuse map to the generated material.
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.SaveAndReimport();

            string baseName = System.IO.Path.GetFileNameWithoutExtension(modelPath);
            string textureDir = PropsFolder + "/" + baseName + ".Textures";
            importer.ExtractTextures(textureDir);
            importer.SaveAndReimport();

            MarkNormalMapTextures(textureDir);

            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (modelAsset == null)
            {
                Debug.LogError("[PropSetup] Could not load the model at " + modelPath);
                return false;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            string prefabPath = PropsFolder + "/" + baseName + ".prefab";
            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Object.DestroyImmediate(instance);

            if (savedPrefab != null)
            {
                Debug.Log("[PropSetup] Built " + prefabPath);
            }
            return savedPrefab != null;
        }

        // Same reasoning as DroodSetupEditor.MarkNormalMapTextures - avoids the
        // interactive "NormalMap settings" dialog by relying on the common *_Normal
        // naming convention instead.
        private static void MarkNormalMapTextures(string textureDir)
        {
            if (!AssetDatabase.IsValidFolder(textureDir))
            {
                return;
            }
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { textureDir }))
            {
                string texturePath = AssetDatabase.GUIDToAssetPath(guid);
                if (texturePath.IndexOf("_Normal", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    texturePath.IndexOf("_normal", System.StringComparison.Ordinal) < 0)
                {
                    continue;
                }
                TextureImporter textureImporter = AssetImporter.GetAtPath(texturePath) as TextureImporter;
                if (textureImporter != null && textureImporter.textureType != TextureImporterType.NormalMap)
                {
                    textureImporter.textureType = TextureImporterType.NormalMap;
                    textureImporter.SaveAndReimport();
                }
            }
        }
    }
}
