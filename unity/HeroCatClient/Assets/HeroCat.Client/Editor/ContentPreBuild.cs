using System;
using System.IO;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HeroCat.Client.Editor
{
    /// <summary>
    /// Before every player build, copies the repository's content/*.json into Assets/Resources/content so that
    /// ContentLocator's Resources fallback finds it on device (specs/05 section 10.1). The copy is git-ignored.
    /// Fails the build if content/manifest.json is missing or any table's sha256 does not match the manifest.
    /// </summary>
    public sealed class ContentPreBuild : IPreprocessBuildWithReport
    {
        public const string Destination = "Assets/Resources/content";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                CopyContent();
            }
            catch (Exception e) when (!(e is BuildFailedException))
            {
                throw new BuildFailedException(e.Message);
            }
        }

        [MenuItem("Tools/HeroCat/Copy Content into Resources")]
        static void CopyFromMenu()
        {
            try
            {
                CopyContent();
            }
            catch (Exception e)
            {
                Debug.LogError(e.Message);
            }
        }

        /// <summary>Validates content/, mirrors its *.json into Assets/Resources/content, then validates the copy.</summary>
        public static void CopyContent()
        {
            string src = FileContentSource.FindContentDirectory(Application.dataPath);
            if (src == null)
                throw new BuildFailedException(
                    "HeroCat: content/manifest.json not found above " + Application.dataPath +
                    ". Run python3 tools/export_content.py build.");
            Validate(src);

            string dst = Path.GetFullPath(Path.Combine(Application.dataPath, "Resources", "content"));
            Directory.CreateDirectory(dst);
            string[] srcFiles = Directory.GetFiles(src, "*.json");
            var names = srcFiles.Select(f => Path.GetFileName(f)).ToList();
            foreach (string stale in Directory.GetFiles(dst, "*.json"))
            {
                if (names.Contains(Path.GetFileName(stale))) continue;
                File.Delete(stale);
                if (File.Exists(stale + ".meta")) File.Delete(stale + ".meta");
            }
            foreach (string file in srcFiles)
            {
                string target = Path.Combine(dst, Path.GetFileName(file));
                byte[] bytes = File.ReadAllBytes(file);
                if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes)) continue;
                File.WriteAllBytes(target, bytes);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Validate(dst);
            Debug.Log("HeroCat: copied " + names.Count + " content files from " + src + " to " + Destination);
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
