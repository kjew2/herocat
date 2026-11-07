using System;
using System.Collections.Generic;
using System.IO;
using HeroCat.Sim.Content;

namespace HeroCat.Runtime.Content
{
    /// <summary>
    /// The on-device content cache of 09 section 10.3 (issue #212), rooted at <c>persistentDataPath/content/</c>. A download
    /// lands in <c>&lt;version&gt;.tmp/</c> (<see cref="BeginStaging"/>); a verified one is renamed to <c>&lt;version&gt;/</c>
    /// (<see cref="Install"/>, step 6), which is the "ready" mark: cold boot reads only directories without the suffix, so a
    /// download killed half way is never booted. Cold boot (<see cref="LoadPreferred"/>, step 8) picks the newest cached set
    /// this client loads cleanly, else the bundle, then keeps the CONTENT_CACHE_KEEP newest versions (plus the one in use)
    /// and deletes the rest and every stray <c>.tmp</c>. Thread-safe; the file work runs on the caller's thread (the
    /// staging on the thread pool, cold boot during SCR_BOOT).
    /// </summary>
    public sealed class ContentCache
    {
        public const string TempSuffix = ".tmp";

        readonly string _root;
        readonly object _lock = new object();
        string _inUse;

        /// <param name="root">The cache root, <c>persistentDataPath/content</c>. Created on first write.</param>
        public ContentCache(string root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string Root => _root;

        /// <summary>The cached version the running sim booted from (never pruned); null when it booted the bundle.</summary>
        public string InUse
        {
            get { lock (_lock) return _inUse; }
        }

        /// <summary><c>&lt;root&gt;/&lt;version&gt;</c>. Throws for anything but a valid content_version (it is a path).</summary>
        public string VersionDir(string version) => Path.Combine(_root, Checked(version));

        public string TempDir(string version) => Path.Combine(_root, Checked(version) + TempSuffix);

        /// <summary>Installed versions (directories named by a valid content_version), newest first.</summary>
        public IReadOnlyList<string> Versions()
        {
            var r = new List<string>();
            lock (_lock)
            {
                if (!Directory.Exists(_root)) return r;
                foreach (var d in Directory.GetDirectories(_root))
                {
                    string name = Path.GetFileName(d);
                    if (ContentVersion.IsValid(name)) r.Add(name);
                }
            }
            r.Sort((a, b) => ContentVersion.Compare(b, a));
            return r;
        }

        /// <summary>The installed sets, newest first, for <see cref="ContentSelection.LoadPreferred"/>.</summary>
        public IReadOnlyList<IContentSource> Sources()
        {
            var r = new List<IContentSource>();
            foreach (var v in Versions()) r.Add(new FileContentSource(Path.Combine(_root, v)));
            return r;
        }

        /// <summary>Step 4: a fresh, empty <c>&lt;version&gt;.tmp/</c> (a leftover from an earlier attempt is deleted).</summary>
        public string BeginStaging(string version)
        {
            string tmp = TempDir(version);
            lock (_lock)
            {
                DeleteDir(tmp);
                Directory.CreateDirectory(tmp);
            }
            return tmp;
        }

        /// <summary>
        /// Step 6: renames <c>&lt;version&gt;.tmp</c> to <c>&lt;version&gt;</c> (one directory rename, atomic on both
        /// platforms), then prunes. An installed copy of the same version (one cold boot rejected) is replaced: it is
        /// deleted first, so a crash in between leaves no copy rather than a mixed one. Throws when nothing is staged.
        /// </summary>
        public void Install(string version)
        {
            string tmp = TempDir(version), dir = VersionDir(version);
            lock (_lock)
            {
                if (!Directory.Exists(tmp)) throw new DirectoryNotFoundException("no staged content " + version);
                DeleteDir(dir);
                Directory.Move(tmp, dir);
                PruneLocked(version, deleteTemp: false);
            }
        }

        /// <summary>Step 5: deletes the staged <c>&lt;version&gt;.tmp</c>; an installed copy of that version stays.</summary>
        public void Discard(string version)
        {
            string tmp = TempDir(version);
            lock (_lock) DeleteDir(tmp);
        }

        /// <summary>
        /// Keeps the CONTENT_CACHE_KEEP newest installed versions, plus <see cref="InUse"/> and <paramref name="keep"/>,
        /// and deletes the others; <paramref name="deleteTemp"/> also deletes every <c>.tmp</c> (only at cold boot, when
        /// no download is running). Best effort: a directory that cannot be deleted is tried again next time.
        /// </summary>
        public void Prune(string keep = null, bool deleteTemp = false)
        {
            lock (_lock) PruneLocked(keep, deleteTemp);
        }

        /// <summary>
        /// Cold boot (step 8): the newest installed set that loads cleanly for this client (hashes, schema, E903 and
        /// ContentDb.Build, via <see cref="ContentSelection.LoadPreferred"/>), else the bundle; records the choice as
        /// <see cref="InUse"/> and prunes. Throws when the bundle itself fails (Fatal CONTENT_CORRUPT).
        /// </summary>
        public ContentChoice LoadPreferred(IContentSource bundled, Tier buildTier, string clientVersion, Action<ContentError> log = null)
        {
            var choice = ContentSelection.LoadPreferred(bundled, Sources(), buildTier, clientVersion, log);
            string used = choice.IsBundled ? null : (choice.Source as FileContentSource)?.Directory;
            lock (_lock)
            {
                _inUse = used == null ? null : Path.GetFileName(used);
                PruneLocked(null, deleteTemp: true);
            }
            return choice;
        }

        void PruneLocked(string keep, bool deleteTemp)
        {
            if (!Directory.Exists(_root)) return;
            var installed = new List<string>();
            foreach (var d in Directory.GetDirectories(_root))
            {
                string name = Path.GetFileName(d);
                if (ContentVersion.IsValid(name)) installed.Add(name);
                else if (deleteTemp && name.EndsWith(TempSuffix, StringComparison.Ordinal) &&
                         ContentVersion.IsValid(name.Substring(0, name.Length - TempSuffix.Length)))
                    TryDelete(d);   // a download killed half way (never booted: not renamed)
            }
            installed.Sort((a, b) => ContentVersion.Compare(b, a));
            for (int i = RuntimeConfig.CONTENT_CACHE_KEEP; i < installed.Count; i++)
            {
                string v = installed[i];
                if (v == keep || v == _inUse) continue;
                TryDelete(Path.Combine(_root, v));
            }
        }

        static string Checked(string version) =>
            ContentVersion.IsValid(version) ? version : throw new ArgumentException("not a content_version: " + version, nameof(version));

        static void DeleteDir(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        static void TryDelete(string dir)
        {
            try { DeleteDir(dir); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
