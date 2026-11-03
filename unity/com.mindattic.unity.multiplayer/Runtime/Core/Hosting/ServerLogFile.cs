using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Unity.Multiplayer.Core
{
    /// <summary>
    /// The server's log file in the package's <c>Logs/</c> folder: one file per run, named
    /// <c>server-yyyyMMdd-HHmmss.log</c> (UTC), each line <c>2026-10-20T12:00:00.000Z [Info] message</c>. Opening one
    /// deletes the oldest runs beyond the number kept. Thread-safe.
    /// </summary>
    public sealed class ServerLogFile : IDisposable
    {
        /// <summary>Every log file's name starts with this.</summary>
        public const string FilePrefix = "server-";

        /// <summary>Every log file's extension.</summary>
        public const string FileExtension = ".log";

        private readonly object _gate = new object();
        private StreamWriter _writer;

        private ServerLogFile(string path, StreamWriter writer)
        {
            Path = path;
            _writer = writer;
        }

        /// <summary>The file's full path.</summary>
        public string Path { get; }

        /// <summary>
        /// Creates <paramref name="directory"/> if needed, deletes the oldest log files so that, with the new one,
        /// at most <paramref name="keep"/> remain (0 keeps all), and opens a new file named for <paramref name="nowUtc"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or <paramref name="keep"/> is negative.</exception>
        public static ServerLogFile Open(string directory, DateTime nowUtc, int keep)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("The logs folder must not be empty.", nameof(directory));
            }

            if (keep < 0)
            {
                throw new ArgumentException("keep must not be negative.", nameof(keep));
            }

            Directory.CreateDirectory(directory);
            if (keep > 0)
            {
                Prune(directory, keep - 1);
            }

            string baseName = FilePrefix + nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(directory, baseName + FileExtension);
            for (int n = 2; File.Exists(path); n++)
            {
                path = System.IO.Path.Combine(directory, baseName + "-" + n.ToString(CultureInfo.InvariantCulture) + FileExtension);
            }

            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            return new ServerLogFile(path, writer);
        }

        /// <summary>
        /// The log files in <paramref name="directory"/>, oldest first: by last-write time, then by the run their name
        /// stands for (its timestamp, then its same-second suffix, the unsuffixed file being the first of that second).
        /// Names alone mis-order same-second runs ("-2" sorts before the unsuffixed file, "-10" before "-2"), and a
        /// clock that went back would mis-order them too (UM-T88).
        /// </summary>
        public static IReadOnlyList<string> List(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return new string[0];
            }

            var files = new List<(string Path, DateTime Written)>();
            foreach (string file in Directory.GetFiles(directory, FilePrefix + "*" + FileExtension))
            {
                DateTime written;
                try
                {
                    written = File.GetLastWriteTimeUtc(file);
                }
                catch (IOException)
                {
                    written = DateTime.MinValue;
                }
                catch (UnauthorizedAccessException)
                {
                    written = DateTime.MinValue;
                }

                files.Add((file, written));
            }

            files.Sort((a, b) =>
            {
                int byTime = a.Written.CompareTo(b.Written);
                return byTime != 0 ? byTime : CompareRunNames(a.Path, b.Path);
            });
            var paths = new List<string>(files.Count);
            foreach (var f in files)
            {
                paths.Add(f.Path);
            }

            return paths;
        }

        // "server-20261020-120000.log" is run 1 of that second, "server-20261020-120000-2.log" run 2, and so on.
        private static int CompareRunNames(string a, string b)
        {
            SplitRunName(a, out string stampA, out int runA);
            SplitRunName(b, out string stampB, out int runB);
            int byStamp = string.CompareOrdinal(stampA, stampB);
            if (byStamp != 0)
            {
                return byStamp;
            }

            int byRun = runA.CompareTo(runB);
            return byRun != 0 ? byRun : string.CompareOrdinal(a, b);
        }

        private static void SplitRunName(string path, out string stamp, out int run)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            const int StampLength = 15; // yyyyMMdd-HHmmss
            run = 1;
            if (name.Length > FilePrefix.Length + StampLength + 1
                && name[FilePrefix.Length + StampLength] == '-'
                && int.TryParse(name.Substring(FilePrefix.Length + StampLength + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                stamp = name.Substring(0, FilePrefix.Length + StampLength);
                run = n;
                return;
            }

            stamp = name;
        }

        /// <summary>Deletes the oldest log files until at most <paramref name="keep"/> remain. Returns the deleted paths.</summary>
        public static IReadOnlyList<string> Prune(string directory, int keep)
        {
            var deleted = new List<string>();
            IReadOnlyList<string> files = List(directory);
            for (int i = 0; i < files.Count - Math.Max(0, keep); i++)
            {
                try
                {
                    File.Delete(files[i]);
                    deleted.Add(files[i]);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return deleted;
        }

        /// <summary>One formatted line, without the newline.</summary>
        public static string FormatLine(DateTime timeUtc, string level, string message)
        {
            return timeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                + " [" + (string.IsNullOrEmpty(level) ? "Info" : level) + "] " + (message ?? string.Empty);
        }

        /// <summary>Appends one line. Does nothing after <see cref="Dispose"/>.</summary>
        public void Write(DateTime timeUtc, string level, string message)
        {
            lock (_gate)
            {
                if (_writer == null)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine(FormatLine(timeUtc, level, message));
                }
                catch (IOException)
                {
                    // A full disk must not take the server down with it.
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_writer != null)
                {
                    _writer.Dispose();
                    _writer = null;
                }
            }
        }
    }
}
