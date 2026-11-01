using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HeroCat.Client.Editor
{
    /// <summary>
    /// Dev and Release Build Profiles (specs/07 section 13). Build Profile assets are Unity-generated, so they are not
    /// hand-written in the repo: create one profile per platform in File > Build Profiles, select it, then run
    /// Tools > HeroCat > Build Profiles > Create Dev + Release From Selected. Profiles named "*-Dev" get HEROCAT_CHEATS,
    /// "*-Release" never do. The build check below refuses a non-development build that would still carry cheats.
    /// </summary>
    public static class BuildProfiles
    {
        public const string Folder = "Assets/Settings/BuildProfiles";
        public const string CheatsDefine = "HEROCAT_CHEATS";
        const string DevSuffix = "-Dev";
        const string ReleaseSuffix = "-Release";

        [MenuItem("Tools/HeroCat/Build Profiles/Create Dev + Release From Selected")]
        static void CreateFromSelected()
        {
            var source = Selection.activeObject as BuildProfile;
            if (source == null)
            {
                EditorUtility.DisplayDialog("HeroCat build profiles",
                    "Select a Build Profile asset first (File > Build Profiles > Add Build Profile, e.g. iOS), then run this again.", "OK");
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(source);
            string platform = StripSuffix(source.name);
            EnsureFolder(Folder);
            foreach (string suffix in new[] { DevSuffix, ReleaseSuffix })
            {
                string path = Folder + "/" + platform + suffix + ".asset";
                if (AssetDatabase.LoadAssetAtPath<BuildProfile>(path) != null) continue;
                if (!AssetDatabase.CopyAsset(sourcePath, path)) Debug.LogError("[HeroCat] could not create " + path);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ApplyDefines();
            Debug.Log("[HeroCat] Created " + platform + DevSuffix + " and " + platform + ReleaseSuffix + " in " + Folder +
                      ". Now open " + platform + DevSuffix + " and tick Development Build, Autoconnect Profiler and Script Debugging;" +
                      " leave them off in " + platform + ReleaseSuffix + ".");
        }

        /// <summary>Adds HEROCAT_CHEATS to every *-Dev profile, removes it from every *-Release profile and from the
        /// iOS/Android Player Settings defines (which every profile on that platform would inherit).</summary>
        [MenuItem("Tools/HeroCat/Build Profiles/Apply Cheat Defines")]
        public static void ApplyDefines()
        {
            int dev = 0, release = 0;
            foreach (var profile in FindProfiles())
            {
                bool isDev = profile.name.EndsWith(DevSuffix, StringComparison.Ordinal);
                bool isRelease = profile.name.EndsWith(ReleaseSuffix, StringComparison.Ordinal);
                if (!isDev && !isRelease) continue;
                var defines = new List<string>(profile.scriptingDefines ?? Array.Empty<string>());
                defines.RemoveAll(d => d == CheatsDefine);
                if (isDev) defines.Add(CheatsDefine);
                profile.scriptingDefines = defines.ToArray();
                EditorUtility.SetDirty(profile);
                if (isDev) dev++; else release++;
            }

            foreach (var target in new[] { NamedBuildTarget.iOS, NamedBuildTarget.Android })
            {
                PlayerSettings.GetScriptingDefineSymbols(target, out string[] global);
                if (!global.Contains(CheatsDefine)) continue;
                PlayerSettings.SetScriptingDefineSymbols(target, global.Where(d => d != CheatsDefine).ToArray());
                Debug.Log("[HeroCat] removed " + CheatsDefine + " from Player Settings defines for " + target.TargetName);
            }

            AssetDatabase.SaveAssets();
            if (dev == 0 || release == 0)
                Debug.LogWarning("[HeroCat] found " + dev + " *-Dev and " + release + " *-Release profiles in " + Folder +
                                 ". Use Tools > HeroCat > Build Profiles > Create Dev + Release From Selected.");
            else
                Debug.Log("[HeroCat] " + CheatsDefine + " set on " + dev + " Dev profile(s), cleared on " + release + " Release profile(s).");
        }

        static IEnumerable<BuildProfile> FindProfiles()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) yield break;
            foreach (string guid in AssetDatabase.FindAssets("t:BuildProfile", new[] { Folder }))
            {
                var p = AssetDatabase.LoadAssetAtPath<BuildProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (p != null) yield return p;
            }
        }

        static string StripSuffix(string name)
        {
            if (name.EndsWith(DevSuffix, StringComparison.Ordinal)) return name.Substring(0, name.Length - DevSuffix.Length);
            if (name.EndsWith(ReleaseSuffix, StringComparison.Ordinal)) return name.Substring(0, name.Length - ReleaseSuffix.Length);
            return name;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }

    /// <summary>Fails a player build that would ship the Debug tab outside a development build: a *-Release profile
    /// marked Development, or a non-development build whose active profile or Player Settings define HEROCAT_CHEATS.</summary>
    sealed class HeroCatReleaseBuildCheck : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            bool development = (report.summary.options & BuildOptions.Development) != 0;
            var profile = BuildProfile.GetActiveBuildProfile();
            string name = profile != null ? profile.name : "(no build profile)";

            if (profile != null && development && name.EndsWith("-Release", StringComparison.Ordinal))
                throw new BuildFailedException("[HeroCat] " + name + " must not be a Development Build (specs/07 section 13).");
            if (development) return;

            bool cheats = profile != null && profile.scriptingDefines != null &&
                          profile.scriptingDefines.Contains(BuildProfiles.CheatsDefine);
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup), out string[] global);
            cheats |= global.Contains(BuildProfiles.CheatsDefine);
            if (cheats)
                throw new BuildFailedException("[HeroCat] non-development build of " + name + " defines " +
                                               BuildProfiles.CheatsDefine + "; use a *-Release profile (specs/07 section 13).");
        }
    }
}
