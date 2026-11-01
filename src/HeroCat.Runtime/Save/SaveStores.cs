using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HeroCat.Runtime.Save
{
    public enum SaveSlot { Primary, Backup }

    /// <summary>
    /// Where save bytes live (specs/08 section 5). Deliberately dumb: raw bytes in, raw bytes out, two slots.
    /// Header/CRC checks, migrations, validation and fallback are SaveSystem's job, so every store gets them for free.
    ///
    /// Contract:
    /// - <see cref="Write"/> is atomic and rotates: after it returns, Primary holds the new bytes and Backup holds
    ///   what Primary held before. A crash at any instant leaves at least one complete previous file. Failures
    ///   throw <see cref="SaveWriteException"/>.
    /// - <see cref="Read"/> returns null when the slot is absent.
    /// - <see cref="Quarantine"/> moves a bad slot out of the way (kept for diagnosis, never loaded again).
    /// </summary>
    public interface ISaveStore
    {
        byte[] Read(SaveSlot slot);
        void Write(byte[] bytes);
        void Quarantine(SaveSlot slot, long nowServerSec);
        void DeleteAll();
    }

    /// <summary>
    /// specs/06 section 5.2. Files in <c>dir</c>: slot0.sav (newest), slot0.prev.sav (previous), slot0.tmp (in flight),
    /// slot0.corrupt-{unix}-{slot}.sav (quarantine). Write = write tmp + fsync, then rename sav -> prev, tmp -> sav.
    /// </summary>
    public sealed class FileSaveStore : ISaveStore
    {
        public FileSaveStore(string directory, string slotName = "slot0")
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            PrimaryPath = Path.Combine(directory, slotName + ".sav");
            BackupPath = Path.Combine(directory, slotName + ".prev.sav");
            TempPath = Path.Combine(directory, slotName + ".tmp");
            _slotName = slotName;
            System.IO.Directory.CreateDirectory(directory);
            try { if (File.Exists(TempPath)) File.Delete(TempPath); }   // a crash mid-write left a partial tmp
            catch (IOException) { }
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
            var dest = Path.Combine(Directory,
                $"{_slotName}.corrupt-{nowServerSec.ToString(CultureInfo.InvariantCulture)}-{slot.ToString().ToLowerInvariant()}.sav");
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(p, dest);
            }
            catch (IOException) { }   // best effort; the loader already skips it
        }

        public void DeleteAll()
        {
            foreach (var p in new[] { PrimaryPath, BackupPath, TempPath })
                if (File.Exists(p)) File.Delete(p);
        }

        /// <summary>Quarantined files currently on disk (diagnostics, tests).</summary>
        public string[] QuarantinedFiles() => System.IO.Directory.GetFiles(Directory, _slotName + ".corrupt-*.sav");
    }

    /// <summary>In-memory store for tests and the bot harness: same rotation semantics, no filesystem.</summary>
    public sealed class MemorySaveStore : ISaveStore
    {
        public byte[] Primary { get; set; }
        public byte[] Backup { get; set; }
        public List<byte[]> Quarantined { get; } = new List<byte[]>();
        public int WriteCount { get; private set; }

        /// <summary>Test hook: while true, Write throws SaveWriteException and changes nothing.</summary>
        public bool FailWrites { get; set; }

        public byte[] Read(SaveSlot slot) => Copy(slot == SaveSlot.Primary ? Primary : Backup);

        public void Write(byte[] bytes)
        {
            if (FailWrites) throw new SaveWriteException("MemorySaveStore.FailWrites is set");
            if (Primary != null) Backup = Primary;
            Primary = Copy(bytes);
            WriteCount++;
        }

        public void Quarantine(SaveSlot slot, long nowServerSec)
        {
            if (slot == SaveSlot.Primary) { if (Primary != null) Quarantined.Add(Primary); Primary = null; }
            else { if (Backup != null) Quarantined.Add(Backup); Backup = null; }
        }

        public void DeleteAll() { Primary = null; Backup = null; }

        static byte[] Copy(byte[] b) => b == null ? null : (byte[])b.Clone();
    }
}
