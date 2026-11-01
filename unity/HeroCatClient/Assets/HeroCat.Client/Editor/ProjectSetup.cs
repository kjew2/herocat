using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HeroCat.Client.Editor
{
    /// <summary>
    /// Applies the project settings from specs/07 section 2 through the editor API, so they do not have to be
    /// hand-edited in the ProjectSettings YAML. Run <c>Tools > HeroCat > Apply iOS Settings</c> once after the first
    /// open, then commit the changed <c>ProjectSettings/*</c>. Safe to re-run.
    /// Build profiles (Dev/Release) are not set here.
    /// </summary>
    public static class ProjectSetup
    {
        // Placeholder: change it to the bundle ID registered in the Apple developer account.
        const string BundleId = "com.kjew2.herocat";
        const string ProductName = "HeroCat";
        const string Version = "0.1.0";
        const string BuildNumber = "1";
        const string MinIosVersion = "15.0";
        const string IconPath = "Assets/HeroCat.Client/Art/AppIcon.png";
        const string UrpAssetPath = "Assets/Settings/HeroCat_URP.asset";
        const string Renderer2DPath = "Assets/Settings/HeroCat_2DRenderer.asset";
        static readonly Color SplashBackground = new Color32(0x12, 0x12, 0x12, 0xFF); // Palette.Background

        [MenuItem("Tools/HeroCat/Apply iOS Settings")]
        public static void ApplyIosSettings()
        {
            ApplyPlayerSettings();
            ApplyIcon();
            bool inputChanged = SetActiveInputHandlerToNew();
            ConfigureUrp();
            AssetDatabase.SaveAssets();

            Debug.Log("[HeroCat] iOS player settings applied. Commit ProjectSettings/*.");
            if (inputChanged)
                EditorUtility.DisplayDialog("HeroCat",
                    "Active Input Handling was switched to the Input System Package (New). Restart the editor for it to take effect.",
                    "OK");
        }

        /// <summary>
        /// URP for the low-end target (specs/07 section 2): 2D Renderer, no HDR, no MSAA, render scale 1, no
        /// post-processing, no depth or opaque texture, SRP Batcher on. Assigned in Graphics and every Quality level.
        /// </summary>
        [MenuItem("Tools/HeroCat/Configure URP")]
        public static void ConfigureUrp()
        {
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Settings")) AssetDatabase.CreateFolder("Assets", "Settings");
                var renderer = AssetDatabase.LoadAssetAtPath<Renderer2DData>(Renderer2DPath);
                if (renderer == null)
                {
                    renderer = ScriptableObject.CreateInstance<Renderer2DData>();
                    AssetDatabase.CreateAsset(renderer, Renderer2DPath);
                }
                urp = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(urp, UrpAssetPath);
            }

            urp.supportsHDR = false;
            urp.msaaSampleCount = 1;
            urp.renderScale = 1f;
            urp.supportsCameraDepthTexture = false;
            urp.supportsCameraOpaqueTexture = false;
            urp.useSRPBatcher = true;
            EditorUtility.SetDirty(urp);
            DisablePostProcessing(AssetDatabase.LoadAssetAtPath<Renderer2DData>(Renderer2DPath));

            GraphicsSettings.defaultRenderPipeline = urp;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(current, false);

            AssetDatabase.SaveAssets();
            Debug.Log($"[HeroCat] URP configured ({UrpAssetPath}). Commit Assets/Settings and ProjectSettings/*.");
        }

        /// <summary>
        /// Same as unticking Post-processing on the renderer: clear its post-process data. Cameras created at runtime
        /// also default to no post-processing.
        /// </summary>
        static void DisablePostProcessing(Renderer2DData renderer)
        {
            if (renderer == null) return;
            var so = new SerializedObject(renderer);
            var prop = so.FindProperty("m_PostProcessData");
            if (prop == null || prop.objectReferenceValue == null) return;
            prop.objectReferenceValue = null;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ApplyPlayerSettings()
        {
            var ios = NamedBuildTarget.iOS;

            PlayerSettings.productName = ProductName;
            PlayerSettings.SetApplicationIdentifier(ios, BundleId);
            PlayerSettings.bundleVersion = Version;
            PlayerSettings.iOS.buildNumber = BuildNumber;

            PlayerSettings.SetScriptingBackend(ios, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(ios, ApiCompatibilityLevel.NET_Standard); // .NET Standard 2.1
            PlayerSettings.iOS.targetOSVersionString = MinIosVersion;
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Portrait only. A portrait-only app must opt out of iPad multitasking or App Store validation rejects it.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.useAnimatedAutorotation = false;
            PlayerSettings.iOS.requiresFullScreen = true;

            // Plain launch: no Unity splash, so iOS shows only the default launch screen on a flat background.
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;
            PlayerSettings.SplashScreen.backgroundColor = SplashBackground;

            // Signing team stays empty; it is filled in on the Mac.
            PlayerSettings.iOS.appleDeveloperTeamID = "";
        }

        static void ApplyIcon()
        {
            if (AssetImporter.GetAtPath(IconPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.alphaSource = TextureImporterAlphaSource.None; // App Store icons must be opaque.
                importer.maxTextureSize = 1024;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon == null)
            {
                Debug.LogError($"[HeroCat] App icon not found at {IconPath}.");
                return;
            }
            // The default icon: Unity scales it to every iOS size, including the 1024x1024 marketing icon.
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
        }

        /// <summary>
        /// Active Input Handling has no public API; set it on the serialized PlayerSettings asset.
        /// 0 = Input Manager (old), 1 = Input System Package (new), 2 = both.
        /// </summary>
        static bool SetActiveInputHandlerToNew()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0) return Warn();
            var so = new SerializedObject(assets[0]);
            var prop = so.FindProperty("activeInputHandler");
            if (prop == null) return Warn();
            if (prop.intValue == 1) return false;
            prop.intValue = 1;
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;

            static bool Warn()
            {
                Debug.LogWarning("[HeroCat] Could not set Active Input Handling. Set it to 'Input System Package (New)' in Player Settings.");
                return false;
            }
        }
    }
}
