using System;
using System.IO;
using System.Text;

namespace HeroCat.Runtime.Profile
{
    /// <summary>
    /// The <c>install_id</c> file (02 section 8.4): this install's <c>DeviceId</c>, a GUID generated at first launch and
    /// reused on every later one. It is lineage for cloud-save conflicts only, never a seed input (3.4). A missing or
    /// unreadable file means a new install (an OS-restored install is a new device), so a new id is written.
    /// The client excludes the file from OS backup (TODO(#151): NSURLIsExcludedFromBackupKey / Android backup rules).
    /// </summary>
    public static class InstallId
    {
        public const string FileName = "install_id";

        /// <summary>Reads <c>{dataDir}/install_id</c>, creating it with a fresh GUID if absent or unreadable.</summary>
        public static Guid LoadOrCreate(string dataDir)
        {
            if (dataDir == null) throw new ArgumentNullException(nameof(dataDir));
            var path = Path.Combine(dataDir, FileName);
            try
            {
                if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id) && id != Guid.Empty) return id;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var fresh = Guid.NewGuid();
            try
            {
                Directory.CreateDirectory(dataDir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, fresh.ToString("D"), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (IOException) { }                  // still usable for this session; the next launch retries
            catch (UnauthorizedAccessException) { }
            return fresh;
        }
    }
}
