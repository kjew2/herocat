using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HeroCat.Client.Editor
{
    /// <summary>
    /// Batch-mode build entry points (specs/07 section 13.2, specs/10 section 6.2). CI calls them through
    /// <c>-executeMethod</c>, for example from <c>.github/workflows/build-android.yml</c>:
    /// <code>Unity -batchmode -quit -projectPath unity/HeroCatClient -executeMethod HeroCat.Client.Editor.Builds.AndroidRelease
    ///   -androidVersionCode 345 -customBuildPath build/HeroCat.aab -androidKeystoreName herocat.keystore ...</code>
    /// The version comes from <c>unity/HeroCatClient/version.json</c>; the build number is the CI run number.
    /// Content is copied by <see cref="ContentPreBuild"/> and the Release cheat check by
    /// <see cref="HeroCatReleaseBuildCheck"/>, both of which run inside every player build.
    ///
    /// Arguments (game-ci/unity-builder names; each also read from the environment variable in brackets):
    /// <list type="bullet">
    /// <item><c>-customBuildPath</c>: output file. Default <c>Builds/Android/HeroCat.aab</c> (Release) or <c>.apk</c> (Dev).</item>
    /// <item><c>-androidVersionCode</c> [ANDROID_VERSION_CODE]: Android versionCode, the build number. Required for Release.</item>
    /// <item><c>-buildVersion</c> [VERSION]: optional; if given it must equal version.json.</item>
    /// <item><c>-androidKeystoreName</c>, <c>-androidKeystorePass</c>, <c>-androidKeyaliasName</c>, <c>-androidKeyaliasPass</c>
    ///   [ANDROID_KEYSTORE_NAME, ANDROID_KEYSTORE_PASS, ANDROID_KEYALIAS_NAME, ANDROID_KEYALIAS_PASS]: Release signing.
    ///   Required for Release; Dev uses the debug keystore.</item>
    /// </list>
    /// </summary>
    public static class Builds
    {
        public const string VersionFile = "version.json";
        const string ReleaseProfile = BuildProfiles.Folder + "/Android-Release.asset";
        const string DevProfile = BuildProfiles.Folder + "/Android-Dev.asset";
        static string ProjectDir => Path.GetDirectoryName(Application.dataPath) ?? ".";
        static readonly Regex SemVer = new Regex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");

        [Serializable]
        sealed class VersionJson
        {
            public string version;
        }

        /// <summary>Signed, IL2CPP, ARM64-only AAB for Play (the Android-Release profile).</summary>
        public static void AndroidRelease() => Run(release: true);

        /// <summary>Development APK signed with the debug keystore (the Android-Dev profile).</summary>
        public static void AndroidDev() => Run(release: false);

        static void Run(bool release)
        {
            try
            {
                BuildReport report = BuildAndroid(release);
                if (report.summary.result != BuildResult.Succeeded)
                    Fail("build " + report.summary.result + " with " + report.summary.totalErrors + " error(s)");
                Debug.Log("[HeroCat] built " + report.summary.outputPath + " (" + report.summary.totalSize + " bytes)");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[HeroCat] Android build failed: " + e.Message);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        /// <summary>Reads <c>version.json</c> next to <c>Assets/</c> and returns its MAJOR.MINOR.PATCH version.</summary>
        public static string ReadVersion()
        {
            string path = Path.Combine(ProjectDir, VersionFile);
            if (!File.Exists(path)) Fail(path + " not found (specs/10 section 6.3)");
            var parsed = JsonUtility.FromJson<VersionJson>(File.ReadAllText(path));
            string version = parsed?.version ?? "";
            if (!SemVer.IsMatch(version)) Fail(path + ": \"version\" must be MAJOR.MINOR.PATCH, got \"" + version + "\"");
            return version;
        }

        static BuildReport BuildAndroid(bool release)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                Fail("the Android build module is not installed in this editor");

            string version = ReadVersion();
            string requested = Arg("-buildVersion", "VERSION");
            if (!string.IsNullOrEmpty(requested) && requested != version && requested != "none")
                Fail("-buildVersion " + requested + " does not match " + VersionFile + " " + version);

            int versionCode = PlayerSettings.Android.bundleVersionCode;
            string codeArg = Arg("-androidVersionCode", "ANDROID_VERSION_CODE");
            if (!string.IsNullOrEmpty(codeArg) && (!int.TryParse(codeArg, out versionCode) || versionCode < 1))
                Fail("-androidVersionCode must be a positive integer, got \"" + codeArg + "\"");
            if (release && string.IsNullOrEmpty(codeArg))
                Fail("a Release build needs -androidVersionCode (the CI run number)");

            var android = NamedBuildTarget.Android;
            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(android, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            // Highest installed SDK: the store requirement (specs/07 section 13.2, specs/10 section 7 item 13).
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            // The Android package name defaults to the iOS bundle ID (ProjectSetup.cs) until one is registered.
            string id = PlayerSettings.GetApplicationIdentifier(android);
            if (string.IsNullOrEmpty(id) || id.StartsWith("com.Company.", StringComparison.Ordinal) ||
                id.StartsWith("com.DefaultCompany.", StringComparison.Ordinal))
                PlayerSettings.SetApplicationIdentifier(android, PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS));
            // The minimum API level is left to ProjectSettings (AndroidMinSdkVersion); it is logged below.

            ApplySigning(release);
            EditorUserBuildSettings.buildAppBundle = release;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;

            string output = Arg("-customBuildPath", null);
            if (string.IsNullOrEmpty(output)) output = "Builds/Android/HeroCat." + (release ? "aab" : "apk");
            output = Path.GetFullPath(Path.Combine(ProjectDir, output));
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");

            Debug.Log("[HeroCat] Android " + (release ? "Release" : "Dev") + " " + version + "+" + versionCode +
                      " package " + PlayerSettings.GetApplicationIdentifier(android) +
                      " minSdk " + PlayerSettings.Android.minSdkVersion + " -> " + output);

            var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(release ? ReleaseProfile : DevProfile);
            if (profile != null)
            {
                BuildProfile.SetActiveBuildProfile(profile);
                return BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions
                {
                    buildProfile = profile,
                    locationPathName = output,
                    options = BuildOptions.None,
                });
            }

            // No profile asset committed yet (README: Build profiles). Build from the global settings with the same
            // Dev/Release split the profiles would give.
            Debug.LogWarning("[HeroCat] " + (release ? ReleaseProfile : DevProfile) + " not found; building from Player Settings.");
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) Fail("no enabled scenes in File > Build Profiles > Scene List");
            return BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = release ? BuildOptions.None : BuildOptions.Development,
            });
        }

        static void ApplySigning(bool release)
        {
            string keystore = Arg("-androidKeystoreName", "ANDROID_KEYSTORE_NAME");
            if (string.IsNullOrEmpty(keystore))
            {
                if (release) Fail("a Release build must be signed: pass -androidKeystoreName and the passwords (never commit the keystore)");
                PlayerSettings.Android.useCustomKeystore = false;
                return;
            }

            // Relative paths are under the project folder, where game-ci decodes androidKeystoreBase64.
            string path = Path.GetFullPath(Path.Combine(ProjectDir, keystore));
            if (!File.Exists(path)) Fail("keystore " + path + " not found");
            string storePass = Arg("-androidKeystorePass", "ANDROID_KEYSTORE_PASS");
            string alias = Arg("-androidKeyaliasName", "ANDROID_KEYALIAS_NAME");
            string aliasPass = Arg("-androidKeyaliasPass", "ANDROID_KEYALIAS_PASS");
            if (string.IsNullOrEmpty(storePass) || string.IsNullOrEmpty(alias) || string.IsNullOrEmpty(aliasPass))
                Fail("keystore given without -androidKeystorePass, -androidKeyaliasName and -androidKeyaliasPass");

            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = path;
            PlayerSettings.Android.keystorePass = storePass;
            PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keyaliasPass = aliasPass;
        }

        /// <summary>Value after <paramref name="flag"/> on the command line, else the environment variable, else null.</summary>
        static string Arg(string flag, string envVar)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return envVar == null ? null : Environment.GetEnvironmentVariable(envVar);
        }

        static void Fail(string message) => throw new BuildFailedException("[HeroCat] " + message);
    }
}
