using UnityEditor;

namespace HeroCat.Client.Editor
{
    /// <summary>
    /// Import rules for <c>Assets/Resources/Art/</c> (07 section 15.3, issue #291): every file is a single sprite with no
    /// mipmaps; pixel art (<c>Pixel/</c>, the <c>spr_</c> battle sprites) gets point filtering and no compression so a sprite
    /// pixel never blurs at integer scale; illustrated art keeps bilinear filtering and the platform's compression.
    /// </summary>
    public sealed class ArtImportSettings : AssetPostprocessor
    {
        const string Root = "Assets/Resources/Art/";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root, System.StringComparison.Ordinal)) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            bool pixel = assetPath.StartsWith(Root + "Pixel/", System.StringComparison.Ordinal);
            ti.filterMode = pixel ? UnityEngine.FilterMode.Point : UnityEngine.FilterMode.Bilinear;
            ti.textureCompression = pixel ? TextureImporterCompression.Uncompressed : TextureImporterCompression.Compressed;
        }
    }
}
