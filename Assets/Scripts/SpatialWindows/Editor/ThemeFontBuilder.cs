using System.IO;
using SpatialLauncher.SpatialWindows.Theme;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace SpatialLauncher.SpatialWindows.Editor
{
    /// <summary>
    /// Builds the dynamic TMP SDF font assets <see cref="ThemeApplier"/> loads from Resources/Fonts (Tools > Spatial
    /// Windows > Build Theme Fonts): Inter Regular, Medium and SemiBold, and Material Icons Round (icons by codepoint, see UIIcons). Safe to run again: fonts whose asset already exists are left alone.
    /// </summary>
    static class ThemeFontBuilder
    {
        const string OutputFolder = "Assets/Resources/Fonts";

        static readonly (string source, string asset)[] Fonts =
        {
            ("Assets/Fonts/Inter/Inter-Regular.ttf", ThemeApplier.RegularName),
            ("Assets/Fonts/Inter/Inter-Medium.ttf", ThemeApplier.MediumName),
            ("Assets/Fonts/Inter/Inter-SemiBold.ttf", ThemeApplier.SemiBoldName),
            ("Assets/Fonts/MaterialIcons/MaterialIconsRound-Regular.otf", ThemeApplier.IconName),
        };

        [MenuItem("Tools/Spatial Windows/Build Theme Fonts")]
        static void Build()
        {
            Directory.CreateDirectory(OutputFolder);
            AssetDatabase.Refresh();
            foreach (var (source, name) in Fonts)
            {
                string path = $"{OutputFolder}/{name}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (existing != null)
                {
                    ClearDynamicDataOnBuild(existing);
                    Debug.Log($"[ThemeFonts] {name} already built.");
                    continue;
                }
                var font = AssetDatabase.LoadAssetAtPath<Font>(source);
                if (font == null)
                {
                    Debug.LogWarning($"[ThemeFonts] {source} not found or not imported; skipped {name}.");
                    continue;
                }

                var asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic);
                if (asset == null)
                {
                    Debug.LogError($"[ThemeFonts] TextMeshPro could not read {source}.");
                    continue;
                }
                asset.name = name;
                AssetDatabase.CreateAsset(asset, path);
                if (asset.atlasTexture != null)
                {
                    asset.atlasTexture.name = name + " Atlas";
                    AssetDatabase.AddObjectToAsset(asset.atlasTexture, asset);
                }
                if (asset.material != null)
                {
                    asset.material.name = name + " Material";
                    AssetDatabase.AddObjectToAsset(asset.material, asset);
                }
                ClearDynamicDataOnBuild(asset);
                EditorUtility.SetDirty(asset);
                Debug.Log($"[ThemeFonts] Built {path}.");
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // The flag is internal in TMP, so set it through the serialized field. Stops editor-time glyphs from
        // churning the committed asset: the atlas is emptied again on build.
        static void ClearDynamicDataOnBuild(TMP_FontAsset asset)
        {
            var so = new SerializedObject(asset);
            var prop = so.FindProperty("m_ClearDynamicDataOnBuild");
            if (prop == null || prop.boolValue) return;
            prop.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
