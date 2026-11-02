using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HeroCat.Runtime.Account
{
    /// <summary>One archived <c>saves/</c> directory: whose it was and when it was archived (server unix seconds).</summary>
    public sealed record ArchivedSaves(Guid PlayerId, long ArchivedAt, string Directory);

    /// <summary>
    /// The logout archive of 01 section 8.13 step 2: <c>{dataDir}/saves/</c> moves to
    /// <c>{dataDir}/saves.&lt;playerId&gt;.archived/</c> (not deleted) so a mistaken logout is recoverable from
    /// <c>SCR_TITLE</c> ("Found a local save for another account"), and archives older than <c>ARCHIVE_KEEP_DAYS</c> are
    /// pruned at boot. The archive time is a file inside the archive (<see cref="StampFile"/>) holding the injected
    /// clock's unix seconds, so pruning follows that clock and not file mtimes (as <c>FileSaveStore.Purge</c> does). A
    /// local guest that never reached the server archives under <c>Guid.Empty</c>. A second archive of the same player
    /// replaces the first (it is the newer progress).
    /// Callers stop the session first (<c>SessionRunner.OnQuit</c>): nothing may write into <c>saves/</c> while it moves.
    /// </summary>
    public sealed class SaveArchive
    {
        public const string SavesDirName = "saves";
        public const string StampFile = "archived_at";
        const string Prefix = SavesDirName + ".";
        const string Suffix = ".archived";
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public SaveArchive(string dataDir)
        {
            DataDir = dataDir ?? throw new ArgumentNullException(nameof(dataDir));
        }

        public string DataDir { get; }
        public string SavesDir => Path.Combine(DataDir, SavesDirName);

        public string ArchiveDirOf(Guid playerId) => Path.Combine(DataDir, Prefix + playerId.ToString("D") + Suffix);

        /// <summary>True when <c>saves/</c> holds no file.</summary>
        public bool SavesEmpty => !Directory.Exists(SavesDir) || Directory.GetFileSystemEntries(SavesDir).Length == 0;

        /// <summary>
        /// Moves <c>saves/</c> to the player's archive and leaves an empty <c>saves/</c>. False (and nothing changes) when
        /// <c>saves/</c> is empty or absent. Throws IOException when the move fails.
        /// </summary>
        public bool Archive(Guid playerId, long nowServerSec)
        {
            if (SavesEmpty) return false;
            var target = ArchiveDirOf(playerId);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(SavesDir, target);   // one rename(2): the save is either here or there
            File.WriteAllText(Path.Combine(target, StampFile), nowServerSec.ToString(CultureInfo.InvariantCulture), Utf8);
            Directory.CreateDirectory(SavesDir);
            return true;
        }

        /// <summary>Every archive on disk, oldest first. An archive with no readable stamp is stamped <paramref name="nowServerSec"/>.</summary>
        public IReadOnlyList<ArchivedSaves> List(long nowServerSec)
        {
            var list = new List<ArchivedSaves>();
            if (!Directory.Exists(DataDir)) return list;
            foreach (var dir in Directory.GetDirectories(DataDir, Prefix + "*" + Suffix))
            {
                var name = Path.GetFileName(dir);
                var mid = name.Substring(Prefix.Length, name.Length - Prefix.Length - Suffix.Length);
                if (!Guid.TryParseExact(mid, "D", out var player)) continue;
                list.Add(new ArchivedSaves(player, ReadStamp(dir, nowServerSec), dir));
            }
            list.Sort((a, b) => a.ArchivedAt.CompareTo(b.ArchivedAt));
            return list;
        }

        /// <summary>The player's archive, or null.</summary>
        public ArchivedSaves Find(Guid playerId, long nowServerSec)
        {
            foreach (var a in List(nowServerSec))
                if (a.PlayerId == playerId) return a;
            return null;
        }

        /// <summary>Deletes archives older than <paramref name="keepDays"/> days (boot, 01 section 8.13). Returns how many.</summary>
        public int Prune(long nowServerSec, int keepDays = RuntimeConfig.ARCHIVE_KEEP_DAYS)
        {
            long maxAge = keepDays * 86400L;
            int n = 0;
            foreach (var a in List(nowServerSec))
            {
                if (nowServerSec - a.ArchivedAt <= maxAge) continue;
                try
                {
                    Directory.Delete(a.Directory, recursive: true);
                    n++;
                }
                catch (IOException) { }                  // best effort; the next boot retries
                catch (UnauthorizedAccessException) { }
            }
            return n;
        }

        /// <summary>
        /// Moves the player's archive back to <c>saves/</c> ("Found a local save for another account", after signing in to
        /// that account). False when there is no archive or <c>saves/</c> is not empty (it is never overwritten).
        /// </summary>
        public bool Restore(Guid playerId)
        {
            var source = ArchiveDirOf(playerId);
            if (!Directory.Exists(source) || !SavesEmpty) return false;
            if (Directory.Exists(SavesDir)) Directory.Delete(SavesDir, recursive: false);
            var stamp = Path.Combine(source, StampFile);
            if (File.Exists(stamp)) File.Delete(stamp);
            Directory.Move(source, SavesDir);
            return true;
        }

        /// <summary>Account deletion (01 section 8.13 step 5): empties <c>saves/</c> and deletes the player's archive.</summary>
        public void Wipe(Guid playerId)
        {
            if (Directory.Exists(SavesDir)) Directory.Delete(SavesDir, recursive: true);
            Directory.CreateDirectory(SavesDir);
            var archive = ArchiveDirOf(playerId);
            if (Directory.Exists(archive)) Directory.Delete(archive, recursive: true);
        }

        static long ReadStamp(string dir, long now)
        {
            var p = Path.Combine(dir, StampFile);
            try
            {
                if (File.Exists(p) && long.TryParse(File.ReadAllText(p).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                    return t;
                File.WriteAllText(p, now.ToString(CultureInfo.InvariantCulture), Utf8);   // start its clock; never prune early
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return now;
        }
    }
}
