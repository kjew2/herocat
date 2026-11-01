using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HeroCat.Client.Core;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HeroCat.Client.Editor
{
    /// <summary>
    /// Before every player build (09 section 10.1, 07 section 17; issue #133):
    /// <list type="number">
    /// <item>verifies the repository's content/ (manifest, every table's sha256, ContentDb.Build) so a stale or broken
    /// bundle cannot ship; build-android.yml / build-ios.yml also run <c>export_content.py check</c> first;</item>
    /// <item>mirrors content/*.json into Assets/StreamingAssets/content (09's bundled floor; iOS and desktop players read it
    /// with File) and Assets/Resources/content (TextAssets; the Android player reads these, since Android's
    /// StreamingAssets is inside the APK), then verifies both copies;</item>
    /// <item>checks every <c>asset</c> address in the content (09 section 3.2) against the project, listing the missing
    /// ones (<see cref="CheckAssetAddresses"/>).</item>
    /// </list>
    /// Both copies are git-ignored. Unity-only: type-checked against stubs in cloud sessions (tools/unity_typecheck), not run.
    /// </summary>
    public sealed class ContentPreBuild : IPreprocessBuildWithReport
    {
        public const string Destination = "Assets/" + ContentBundle.ResourcesDir;
        public const string StreamingDestination = "Assets/" + ContentBundle.StreamingAssetsDir;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                CopyContent();
                CheckAssetAddresses();
            }
            catch (Exception e) when (!(e is BuildFailedException))
            {
                throw new BuildFailedException(e.Message);
            }
        }

        [MenuItem("Tools/HeroCat/Copy Content into the Build")]
        static void CopyFromMenu()
        {
            try
            {
                CopyContent();
                CheckAssetAddresses();
            }
            catch (Exception e)
            {
                Debug.LogError(e.Message);
            }
        }

        static string RepoContent()
        {
            string src = FileContentSource.FindContentDirectory(Application.dataPath);
            if (src == null)
                throw new BuildFailedException(
                    "HeroCat: content/manifest.json not found above " + Application.dataPath +
                    ". Run python3 tools/export_content.py build.");
            return src;
        }

        static string[] Targets => new[]
        {
            Path.GetFullPath(Path.Combine(Application.dataPath, ContentBundle.StreamingAssetsDir)),
            Path.GetFullPath(Path.Combine(Application.dataPath, ContentBundle.ResourcesDir)),
        };

        /// <summary>Validates content/, mirrors its *.json into both bundled locations, then validates each copy.</summary>
        public static void CopyContent()
        {
            string src = RepoContent();
            Validate(src);
            int n = 0;
            foreach (string dst in Targets) n = ContentBundle.Mirror(src, dst);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string dst in Targets) Validate(dst);
            Debug.Log("HeroCat: bundled " + n + " content files from " + src + " into " + StreamingDestination + " and " + Destination);
        }

        /// <summary>
        /// 09 section 3.2: every asset address must resolve. The project has no Addressables package yet (07 section 16), so
        /// an address resolves when a file of that name (any extension but .meta) is in a Resources folder or under
        /// Assets/HeroCat.Client/Art. M0 has no art: its screens draw code-built placeholders (07 section 15.1), so for the
        /// M0 tier the missing list is a warning; any other tier fails the build. TODO(#133): fail M0 builds too once
        /// placeholder art is addressable (the art and Addressables decision, issue #2).
        /// </summary>
        public static void CheckAssetAddresses()
        {
            var known = AddressCatalog();
            var missing = ContentAssets.Missing(RepoContent(), known.Contains);
            if (missing.Count == 0)
            {
                Debug.Log("HeroCat: every content asset address resolves");
                return;
            }
            string list = string.Join("\n  ", missing.Select(m => m.ToString()));
            string msg = "HeroCat: " + missing.Count + " content asset address(es) do not resolve (09 section 3.2):\n  " + list;
            if (HeroCat.Client.Boot.ContentLocator.BuildTier == Tier.M0) Debug.LogWarning(msg + "\n(M0: a warning; the screens draw code-built placeholders)");
            else throw new BuildFailedException(msg);
        }

        /// <summary>The addresses the project resolves today (see <see cref="CheckAssetAddresses"/>).</summary>
        static HashSet<string> AddressCatalog()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            string assets = Application.dataPath;
            foreach (string f in Directory.GetFiles(assets, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".meta", StringComparison.Ordinal)) continue;
                string rel = f.Substring(assets.Length).Replace('\\', '/');
                if (rel.Contains("/Resources/") || rel.StartsWith("/HeroCat.Client/Art/", StringComparison.Ordinal))
                    names.Add(Path.GetFileNameWithoutExtension(f));
            }
            return names;
        }

        static void Validate(string dir)
        {
            try
            {
                ContentLoader.LoadFromDirectory(dir, HeroCat.Client.Boot.ContentLocator.BuildTier);   // E900-E902 plus ContentDb.Build
            }
            catch (ContentBuildException e)
            {
                throw new BuildFailedException("HeroCat: " + dir + ": " + e.Message);
            }
        }
    }
}
