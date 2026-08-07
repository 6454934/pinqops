namespace PinqOps.Alerts;

/// <summary>
/// An append-only JSONL file with a bounded number of generations. Appending is
/// O(1), which is why the alert history and the metric history use this rather
/// than the capped-array shape <see cref="DeployHistoryStore"/> uses: at one
/// sample a minute, rewriting a whole file 1440 times a day would burn the disk
/// and leave a torn-write window open every 60 seconds.
///
/// Rotation is by size, by line count, or both — whichever limit is reached
/// first. Line count is what gives the metric history a predictable <em>time</em>
/// window, which a size cap alone cannot: a size-bounded file silently covers
/// less and less history as more containers are added.
/// </summary>
public sealed class RotatingJsonLog
{
    private readonly string _path;
    private readonly int _generations;
    private readonly long _maxBytes;
    private readonly int _maxLines;
    private readonly object _gate = new();

    private int? _lines;

    /// <param name="generations">
    /// How many <em>previous</em> files to keep beside the live one, as
    /// <c>.1</c>…<c>.N</c>, so the total on disk is this plus one. Same meaning
    /// the audit trail gives it — worth being explicit about, because reading it
    /// as "files kept in total" makes every retention sum come out one file short.
    /// </param>
    /// <param name="maxBytes">Rotate past this size. Zero disables the size limit.</param>
    /// <param name="maxLines">Rotate past this many lines. Zero disables the line limit.</param>
    public RotatingJsonLog(string path, int generations, long maxBytes = 0, int maxLines = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(generations, 1);

        _path = path;
        _generations = generations;
        _maxBytes = maxBytes;
        _maxLines = maxLines;
    }

    public string Path_ => _path;

    /// <summary>
    /// Appends one serialized JSON line. Failures are swallowed: losing a line of
    /// history is always better than failing the thing being recorded.
    /// </summary>
    public void Append(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();
                EnsureOwnerOnlyFile();

                // Counted BEFORE the append. RotateIfNeeded only fills the cache
                // when a line limit is set, so with one disabled (the alert trail
                // rotates by size) the count ran after the write — counting the line
                // just added and then adding one more on top of it. Harmless while
                // the limit is off and a silent off-by-one the moment it is not.
                var before = _lines ??= CountLines();
                File.AppendAllText(_path, line + "\n");
                _lines = before + 1;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Every line on file. A line that cannot be parsed by the caller should be
    /// skipped rather than fatal — a torn append costs one line, not the file.
    /// </summary>
    public IReadOnlyList<string> ReadLines(bool oldestFirst = true)
    {
        var lines = new List<string>();
        lock (_gate)
        {
            foreach (var file in Files(oldestFirst))
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var read = File.ReadAllLines(file);
                    if (!oldestFirst)
                    {
                        Array.Reverse(read);
                    }

                    foreach (var line in read)
                    {
                        if (line.Length > 0)
                        {
                            lines.Add(line);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// The same lines, one file at a time and only as far as the caller reads.
    ///
    /// <para><b>Why this exists beside <see cref="ReadLines"/>.</b> That one answers
    /// with every line in the whole archive before the caller has looked at the
    /// first, which is fine for a few hundred alert rows and is not fine for a log
    /// search: at the ceiling the log collector advertises, materialising every
    /// container's four files is gigabytes of strings on the request thread, so the
    /// caller's limit bounded the answer and not the work.</para>
    ///
    /// <para><b>No lock, deliberately.</b> The caller decides how long it takes to
    /// read, and a lock held across that would block every appender for as long as
    /// somebody is scrolling. A line torn by an append landing mid-read is one line
    /// the caller cannot parse, which is the case it already has to handle — the same
    /// trade <see cref="Append"/> makes when it swallows a failed write.</para>
    /// </summary>
    public IEnumerable<string> StreamLines(bool oldestFirst = true)
    {
        foreach (var file in Files(oldestFirst))
        {
            if (!File.Exists(file))
            {
                continue;
            }

            var lines = oldestFirst ? Forward(file) : Backward(file);
            foreach (var line in lines)
            {
                if (line.Length > 0)
                {
                    yield return line;
                }
            }
        }
    }

    private static IEnumerable<string> Forward(string path)
    {
        IEnumerator<string> reader;
        try
        {
            reader = File.ReadLines(path).GetEnumerator();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (reader)
        {
            while (true)
            {
                try
                {
                    if (!reader.MoveNext())
                    {
                        yield break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                yield return reader.Current;
            }
        }
    }

    /// <summary>
    /// One file's lines newest first, read in blocks from the end rather than by
    /// loading it.
    ///
    /// <para>This is what makes a bounded read bounded. The newest lines are at the
    /// end of the file, so answering "the last twenty" by reading the whole thing and
    /// reversing it costs the file — 64 MB per container, per search — to produce a
    /// few kilobytes.</para>
    ///
    /// <para>Split on the newline <em>byte</em> and decoded per line, not per block: a
    /// block boundary falls wherever it falls, and decoding two halves of a
    /// multi-byte character separately turns text into replacement marks. <c>0x0A</c>
    /// never appears inside a UTF-8 sequence, so the split itself is always safe.</para>
    /// </summary>
    private static IEnumerable<string> Backward(string path)
    {
        const int BlockSize = 64 * 1024;

        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using (stream)
        {
            var position = stream.Length;
            var block = new byte[BlockSize];
            var pending = Array.Empty<byte>();

            while (position > 0)
            {
                var size = (int)Math.Min(BlockSize, position);
                position -= size;

                byte[] combined;
                try
                {
                    stream.Position = position;
                    stream.ReadExactly(block, 0, size);

                    combined = new byte[size + pending.Length];
                    Buffer.BlockCopy(block, 0, combined, 0, size);
                    Buffer.BlockCopy(pending, 0, combined, size, pending.Length);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    yield break;
                }

                var end = combined.Length;
                for (var index = combined.Length - 1; index >= 0; index--)
                {
                    if (combined[index] != (byte)'\n')
                    {
                        continue;
                    }

                    yield return Decode(combined, index + 1, end - index - 1);
                    end = index;
                }

                pending = combined[..end];
            }

            yield return Decode(pending, 0, pending.Length);
        }
    }

    private static string Decode(byte[] bytes, int offset, int count) =>
        count <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes, offset, count).TrimEnd('\r');

    /// <summary>The live file and its generations, oldest generation first unless asked otherwise.</summary>
    private IEnumerable<string> Files(bool oldestFirst)
    {
        var files = new List<string> { _path };
        for (var generation = 1; generation <= _generations; generation++)
        {
            files.Add($"{_path}.{generation}");
        }

        // The list is newest-first as built (live file, then .1, .2 …).
        if (oldestFirst)
        {
            files.Reverse();
        }

        return files;
    }

    private int CountLines()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return 0;
            }

            var count = 0;
            foreach (var line in File.ReadLines(_path))
            {
                if (line.Length > 0)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            _lines = 0;
            return;
        }

        var overSize = _maxBytes > 0 && info.Length >= _maxBytes;
        var overLines = _maxLines > 0 && (_lines ??= CountLines()) >= _maxLines;
        if (!overSize && !overLines)
        {
            return;
        }

        for (var generation = _generations - 1; generation >= 1; generation--)
        {
            var source = $"{_path}.{generation}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{generation + 1}", overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
        _lines = 0;
    }

    /// <summary>
    /// Creates the file 0600 before anything is written to it. Alert history names
    /// containers and thresholds; metric history describes the machine. Neither
    /// belongs on a world-readable inode, and the rest of the state directory is
    /// owner-only already.
    /// </summary>
    private void EnsureOwnerOnlyFile()
    {
        if (File.Exists(_path))
        {
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using var stream = new FileStream(_path, options);
        }
        catch (IOException)
        {
            // Another writer won the race and created it; appending is still fine.
        }
    }
}
