using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HeroCat.Runtime.Save
{
    public enum SaveSlot { Primary, Backup }

    /// <summary>
    /// The raw-bytes layer under <see cref="LocalSaveStore"/> (02 section 8; decision 1 of issue #77): two slots plus the
    /// quarantine and conflict archives. Deliberately dumb: header/CRC checks, migrations, validation, fallback and
    /// retention policy are LocalSaveStore's job, so the file store, the in-memory store and the cloud wrapper share them.
    ///
    /// Contract:
    /// - <see cref="Write"/> is atomic and rotates: after it returns, Primary holds the new bytes and Backup holds
    ///   what Primary held before. A crash at any instant leaves at least one complete previous file. Failures
    ///   throw <see cref="SaveWriteException"/>.
    /// - <see cref="Read"/> returns null when the slot is absent.
    /// - <see cref="Quarantine"/> moves a bad slot to <c>slot0.corrupt-&lt;unix&gt;.sav</c> (kept for support, never loaded).
    /// - <see cref="ArchiveConflict"/> keeps a rejected conflict candidate as <c>slot0.conflict-&lt;unix&gt;.sav</c>.
    /// - <see cref="Purge"/> deletes quarantined and archived files older than the given ages in seconds, judged by the
    ///   unix time in the name, so it follows the injected clock and not file mtimes.
    /// </summary>
    public interface ISaveSlotStore
    {
        byte[] Read(SaveSlot slot);
        void Write(byte[] bytes);
        void Quarantine(SaveSlot slot, long nowServerSec);
        void ArchiveConflict(byte[] bytes, long nowServerSec);
        void DeleteStrayTemp();
        void Purge(long nowServerSec, long corruptMaxAgeSec, long conflictMaxAgeSec);
        void DeleteAll();
    }

    /// <summary>
    /// 02 section 8.2. Files in <c>dir</c>: slot0.sav (newest), slot0.prev.sav (previous), slot0.tmp (in flight),
    /// slot0.corrupt-{unix}.sav (quarantine) and slot0.conflict-{unix}.sav (rejected conflict candidates); a name that is
    /// taken gets <c>-1</c>, <c>-2</c>, ... before <c>.sav</c>. Write = write tmp + fsync, then rename sav -&gt; prev,
    /// tmp -&gt; sav.
    /// </summary>
    public sealed class FileSaveStore : ISaveSlotStore
    {
        public FileSaveStore(string directory, string slotName = "slot0")
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            PrimaryPath = Path.Combine(directory, slotName + ".sav");
            BackupPath = Path.Combine(directory, slotName + ".prev.sav");
            TempPath = Path.Combine(directory, slotName + ".tmp");
            _slotName = slotName;
            System.IO.Directory.CreateDirectory(directory);
            DeleteStrayTemp();   // a crash mid-write left a partial tmp
        }

        readonly string _slotName;

        public string Directory { get; }
        public string PrimaryPath { get; }
        public string BackupPath { get; }
        public string TempPath { get; }

        /// <summary>Test hook: runs between the two renames, to simulate a crash there.</summary>
        internal Action AfterRotate;

        string PathOf(SaveSlot slot) => slot == SaveSlot.Primary ? PrimaryPath : BackupPath;

        public byte[] Read(SaveSlot slot)
        {
            var p = PathOf(slot);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public void Write(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            try
            {
                using (var fs = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(PrimaryPath))
                {
                    if (File.Exists(BackupPath)) File.Delete(BackupPath);   // primary is still intact here
                    File.Move(PrimaryPath, BackupPath);                     // rename(2)
                }
                AfterRotate?.Invoke();
                File.Move(TempPath, PrimaryPath);                           // rename(2)
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
                throw new SaveWriteException($"could not write {PrimaryPath}: {e.Message}", e);
            }
        }

        public void Quarantine(SaveSlot slot, long nowServerSec)
        {
            var p = PathOf(slot);
            if (!File.Exists(p)) return;
            try { File.Move(p, FreeName("corrupt", nowServerSec)); }
            catch (IOException) { }   // best effort; the loader already skips it
        }

        public void ArchiveConflict(byte[] bytes, long nowServerSec)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            try { File.WriteAllBytes(FreeName("conflict", nowServerSec), bytes); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new SaveWriteException($"could not archive a conflict candidate: {e.Message}", e);
            }
        }

        public void DeleteStrayTemp()
        {
            try { if (File.Exists(TempPath)) File.Delete(TempPath); }
            catch (IOException) { }
        }

        public void Purge(long nowServerSec, long corruptMaxAgeSec, long conflictMaxAgeSec)
        {
            PurgeKind("corrupt", nowServerSec, corruptMaxAgeSec);
            PurgeKind("conflict", nowServerSec, conflictMaxAgeSec);
        }

        void PurgeKind(string kind, long now, long maxAge)
        {
            foreach (var f in System.IO.Directory.GetFiles(Directory, $"{_slotName}.{kind}-*.sav"))
            {
                if (!SaveArchiveNames.TryParseUnix(Path.GetFileName(f), _slotName, kind, out long t)) continue;
                if (now - t <= maxAge) continue;
                try { File.Delete(f); } catch (IOException) { }
            }
        }

        string FreeName(string kind, long unix)
        {
            for (int n = 0; ; n++)
            {
                var p = Path.Combine(Directory, SaveArchiveNames.Name(_slotName, kind, unix, n));
                if (!File.Exists(p)) return p;
            }
        }

        public void DeleteAll()
        {
            foreach (var p in new[] { PrimaryPath, BackupPath, TempPath })
                if (File.Exists(p)) File.Delete(p);
        }

        /// <summary>Quarantined files currently on disk (diagnostics, tests).</summary>
        public string[] QuarantinedFiles() => System.IO.Directory.GetFiles(Directory, _slotName + ".corrupt-*.sav");

        /// <summary>Archived conflict candidates currently on disk (diagnostics, tests).</summary>
        public string[] ConflictFiles() => System.IO.Directory.GetFiles(Directory, _slotName + ".conflict-*.sav");
    }

    /// <summary>
    /// In-memory store for tests and the bot harness (bots write no files, 02 section 10.1): the same rotation, quarantine,
    /// archive and purge semantics as <see cref="FileSaveStore"/>, no filesystem.
    /// </summary>
    public sealed class MemorySaveStore : ISaveSlotStore
    {
        public sealed class Archived
        {
            public Archived(string kind, long serverTime, byte[] bytes) { Kind = kind; ServerTime = serverTime; Bytes = bytes; }

            /// <summary>"corrupt" or "conflict".</summary>
            public string Kind { get; }
            public long ServerTime { get; }
            public byte[] Bytes { get; }
        }

        public byte[] Primary { get; set; }
        public byte[] Backup { get; set; }

        /// <summary>A stray slot0.tmp; DeleteStrayTemp clears it.</summary>
        public byte[] Temp { get; set; }

        public List<Archived> Archive { get; } = new List<Archived>();
        public int WriteCount { get; private set; }

        /// <summary>Quarantined files' bytes, oldest first.</summary>
        public List<byte[]> Quarantined => Archive.FindAll(a => a.Kind == "corrupt").ConvertAll(a => a.Bytes);

        /// <summary>Archived conflict candidates' bytes, oldest first.</summary>
        public List<byte[]> Conflicts => Archive.FindAll(a => a.Kind == "conflict").ConvertAll(a => a.Bytes);

        /// <summary>Test hook: while true, Write throws SaveWriteException and changes nothing.</summary>
        public bool FailWrites { get; set; }

        /// <summary>Test hook: runs between the two "renames" (Backup rotated, Primary not yet written) to simulate a crash.</summary>
        public Action AfterRotate { get; set; }

        public byte[] Read(SaveSlot slot) => Copy(slot == SaveSlot.Primary ? Primary : Backup);

        public void Write(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (FailWrites) throw new SaveWriteException("MemorySaveStore.FailWrites is set");
            Temp = Copy(bytes);
            if (Primary != null) { Backup = Primary; Primary = null; }
            try { AfterRotate?.Invoke(); }
            catch (Exception e) { throw new SaveWriteException("crash between the renames: " + e.Message, e); }
            Primary = Temp;
            Temp = null;
            WriteCount++;
        }

        public void Quarantine(SaveSlot slot, long nowServerSec)
        {
            if (slot == SaveSlot.Primary)
            {
                if (Primary != null) Archive.Add(new Archived("corrupt", nowServerSec, Primary));
                Primary = null;
            }
            else
            {
                if (Backup != null) Archive.Add(new Archived("corrupt", nowServerSec, Backup));
                Backup = null;
            }
        }

        public void ArchiveConflict(byte[] bytes, long nowServerSec) =>
            Archive.Add(new Archived("conflict", nowServerSec, Copy(bytes ?? throw new ArgumentNullException(nameof(bytes)))));

        public void DeleteStrayTemp() => Temp = null;

        public void Purge(long nowServerSec, long corruptMaxAgeSec, long conflictMaxAgeSec) =>
            Archive.RemoveAll(a => nowServerSec - a.ServerTime > (a.Kind == "corrupt" ? corruptMaxAgeSec : conflictMaxAgeSec));

        public void DeleteAll() { Primary = null; Backup = null; Temp = null; }

        static byte[] Copy(byte[] b) => b == null ? null : (byte[])b.Clone();
    }

    /// <summary><c>{slot}.{kind}-{unix}[-{n}].sav</c>: the archive names of 02 section 8.2, with a counter when taken.</summary>
    public static class SaveArchiveNames
    {
        public static string Name(string slot, string kind, long unix, int n) =>
            $"{slot}.{kind}-{unix.ToString(CultureInfo.InvariantCulture)}" +
            (n > 0 ? "-" + n.ToString(CultureInfo.InvariantCulture) : "") + ".sav";

        public static bool TryParseUnix(string fileName, string slot, string kind, out long unix)
        {
            unix = 0;
            string prefix = slot + "." + kind + "-";
            if (fileName == null || !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                !fileName.EndsWith(".sav", StringComparison.Ordinal))
                return false;
            var mid = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - 4);
            int dash = mid.IndexOf('-');
            if (dash >= 0) mid = mid.Substring(0, dash);
            return long.TryParse(mid, NumberStyles.None, CultureInfo.InvariantCulture, out unix);
        }
    }
}
