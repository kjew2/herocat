using System.Collections.Generic;
using System.IO;
using HeroCat.Runtime.Content;
using HeroCat.Sim.Content;
using UnityEngine;

namespace HeroCat.Client.Boot
{
    /// <summary>
    /// Finds content/*.json and builds the ContentDb through Runtime's ContentLoader (09 section 2; the Sim never sees
    /// JSON). Order:
    ///   1. Editor / desktop dev: walk up from Assets/ to the repository's content/ folder.
    ///   2. StreamingAssets/content: the bundled floor (09 section 10.1) on iOS and desktop players.
    ///   3. Resources/content TextAssets: Android, whose StreamingAssets is inside the APK and not a folder.
    /// Editor/ContentPreBuild.cs verifies content/ and writes both copies before every player build (issue #133).
    /// TODO(#315): boot through Runtime's ContentCache.LoadPreferred over persistentDataPath/content.
    /// Unity-only: not compiled in cloud sessions.
    /// </summary>
    public static class ContentLocator
    {
        /// <summary>The tier this client is built for (09 section 5.32). TODO(#141): from the build profile.</summary>
        public const Tier BuildTier = Tier.M0;

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
                return ContentLoader.LoadFromDirectory(dir, BuildTier, Application.version);
            }
            Describe = "Resources/content";
            return ContentLoader.Load(new ResourcesContentSource(), BuildTier, Application.version);
        }

        /// <summary>
        /// The English localization table (09 <c>localization.en.json</c>) from the same places as the content, for the
        /// client's Localizer (07 section 12). Null when it cannot be found; the Localizer then renders keys.
        /// TODO(#57): the locale from ClientPrefs once a second locale ships.
        /// </summary>
        public static string LoadLocalizationJson(string locale = "en")
        {
            string file = "localization." + locale + ".json";
            string dir = FileContentSource.FindContentDirectory(Application.dataPath);
            if (dir == null)
            {
                string sa = Path.Combine(Application.streamingAssetsPath, "content");
                if (File.Exists(Path.Combine(sa, file))) dir = sa;
            }
            if (dir != null && File.Exists(Path.Combine(dir, file))) return File.ReadAllText(Path.Combine(dir, file));
            var asset = Resources.Load<TextAsset>("content/" + Path.GetFileNameWithoutExtension(file));
            return asset != null ? asset.text : null;
        }
    }

    /// <summary>Each table is a TextAsset under Assets/Resources/content/ (09 section 10.1).</summary>
    public sealed class ResourcesContentSource : IContentSource
    {
        static TextAsset Get(string fileName) =>
            Resources.Load<TextAsset>("content/" + Path.GetFileNameWithoutExtension(fileName));

        public bool Exists(string fileName) => Get(fileName) != null;

        public byte[] ReadBytes(string fileName) => Get(fileName).bytes;

        /// <summary>Resources cannot list a folder; E902's "present but not listed" half is skipped.</summary>
        public IReadOnlyList<string> ListJsonFiles() => null;
    }
}
