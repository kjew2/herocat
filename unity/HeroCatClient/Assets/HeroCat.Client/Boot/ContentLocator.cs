using System.IO;
using HeroCat.Sim.Content;
using UnityEngine;

namespace HeroCat.Client.Boot
{
    /// <summary>
    /// Finds content/*.json without copying it into the project (specs/05 section 10). Order:
    ///   1. Editor / desktop dev: walk up from Assets/ to the repository's content/ folder.
    ///   2. StreamingAssets/content (desktop players, if someone copies it there).
    ///   3. Resources/content TextAssets (device builds: Editor/ContentPreBuild.cs copies them in before each build).
    /// </summary>
    public static class ContentLocator
    {
        public static string Describe { get; private set; } = "";

        public static ContentDb Load()
        {
            string dir = FileContentSource.FindContentDirectory(Application.dataPath);
            if (dir == null)
            {
                string sa = Path.Combine(Application.streamingAssetsPath, "content");
                if (File.Exists(Path.Combine(sa, "manifest.json"))) dir = sa;
            }
            if (dir != null)
            {
                Describe = dir;
                return ContentDb.LoadFromDirectory(dir);
            }
            Describe = "Resources/content";
            return ContentDb.Load(new ResourcesContentSource());
        }
    }

    /// <summary>specs/05 section 10.1: each table is a TextAsset under Assets/Resources/content/.</summary>
    public sealed class ResourcesContentSource : IContentSource
    {
        static TextAsset Get(string fileName) =>
            Resources.Load<TextAsset>("content/" + Path.GetFileNameWithoutExtension(fileName));

        public bool Exists(string fileName) => Get(fileName) != null;

        public string ReadText(string fileName) => Get(fileName).text;
    }
}
