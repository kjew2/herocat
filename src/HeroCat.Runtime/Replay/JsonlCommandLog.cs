using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeroCat.Sim.Commands;
using HeroCat.Sim.Replay;

namespace HeroCat.Runtime.Replay
{
    /// <summary>
    /// <c>saves/cmdlog.jsonl</c> (01 section 2.6; issue #67): every command Simulation.Apply runs, system commands
    /// included, appended as one <see cref="CommandCodec"/> line before it is applied (2.2 rule 4). When an append would
    /// take the file past <see cref="MaxBytes"/> (CMDLOG_MAX_BYTES), whole lines are dropped from the head until the file
    /// is at most three quarters full, so rotation is amortized and the file never exceeds the cap. Uploaded only when the
    /// server asks (08 spot check). Excluded from OS backup (02 section 8.4) by the client's platform layer, TODO(#151).
    /// Main thread only.
    /// </summary>
    public sealed class JsonlCommandLog : ICommandLog, IDisposable
    {
        public const string FileName = "cmdlog.jsonl";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        FileStream _file;

        /// <param name="path">Usually {dataDir}/saves/cmdlog.jsonl.</param>
        public JsonlCommandLog(string path, int maxBytes = RuntimeConfig.CMDLOG_MAX_BYTES)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            MaxBytes = maxBytes;
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Open();
        }

        public string Path { get; }
        public int MaxBytes { get; }
        public long Length => _file.Length;
        public int Rotations { get; private set; }

        public void Append(Command c)
        {
            var bytes = Utf8.GetBytes(CommandCodec.Encode(c) + "\n");
            if (bytes.Length > MaxBytes) return;   // a single line larger than the cap cannot be kept (never in practice)
            if (_file.Length + bytes.Length > MaxBytes) Rotate(MaxBytes * 3 / 4 - bytes.Length);
            _file.Write(bytes, 0, bytes.Length);
            _file.Flush();
        }

        /// <summary>All commands in the file, oldest first.</summary>
        public IReadOnlyList<Command> ReadAll()
        {
            _file.Flush();
            return Read(Path);
        }

        /// <summary>Parses a JSONL command log file (blank lines ignored).</summary>
        public static IReadOnlyList<Command> Read(string path)
        {
            var list = new List<Command>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs, Utf8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                    if (line.Length > 0) list.Add(CommandCodec.Decode(line));
            }
            return list;
        }

        /// <summary>Parses JSONL text (golden logs, uploads).</summary>
        public static IReadOnlyList<Command> Parse(string text)
        {
            var list = new List<Command>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length > 0) list.Add(CommandCodec.Decode(l));
            }
            return list;
        }

        /// <summary>Encodes commands as JSONL text, one line each, newline-terminated.</summary>
        public static string Format(IEnumerable<Command> commands)
        {
            var sb = new StringBuilder();
            foreach (var c in commands) sb.Append(CommandCodec.Encode(c)).Append('\n');
            return sb.ToString();
        }

        public void Dispose() => _file?.Dispose();

        void Open()
        {
            _file = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _file.Seek(0, SeekOrigin.End);
        }

        /// <summary>Keeps the newest whole lines that fit in <paramref name="keepBytes"/>; writes tmp then renames.</summary>
        void Rotate(int keepBytes)
        {
            _file.Flush();
            _file.Seek(0, SeekOrigin.Begin);
            var all = new byte[_file.Length];
            int read = 0;
            while (read < all.Length)
            {
                int n = _file.Read(all, read, all.Length - read);
                if (n <= 0) break;
                read += n;
            }
            _file.Dispose();

            int start = Math.Max(0, read - Math.Max(0, keepBytes));
            if (start > 0)
            {
                // Advance to the start of the next whole line.
                while (start < read && all[start - 1] != (byte)'\n') start++;
            }
            string tmp = Path + ".tmp";
            using (var t = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                t.Write(all, start, read - start);
                t.Flush(true);
            }
            File.Delete(Path);
            File.Move(tmp, Path);
            Rotations++;
            Open();
        }
    }

    /// <summary>The bot harness keeps the JSONL lines in memory (01 section 2.6); same format as the file.</summary>
    public sealed class MemoryJsonlCommandLog : ICommandLog
    {
        readonly List<string> _lines = new List<string>();

        public IReadOnlyList<string> Lines => _lines;

        public void Append(Command c) => _lines.Add(CommandCodec.Encode(c));

        public string Text => string.Join("\n", _lines) + (_lines.Count > 0 ? "\n" : "");

        public IReadOnlyList<Command> Commands() => JsonlCommandLog.Parse(Text);
    }
}
