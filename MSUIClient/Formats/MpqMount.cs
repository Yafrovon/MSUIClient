using System.Threading;
using System.Collections.Concurrent;
using System.Text;
using MSUIClient.Formats.Mpq;

namespace MSUIClient.Formats;

/// <summary>
/// Opens the client's MPQ archives once and keeps them open for the life of the
/// process.
///
/// WHY THIS EXISTS
///   AdtTerrainReader.ReadFileFromMpqs was written for a web app where a read
///   happens now and then, so it did this per call:
///
///       foreach (var mpq in GetMpqLoadOrder(dataPath))
///           using var archive = MpqArchive.Open(mpq);   // header + hash + block
///
///   That is a directory enumeration plus up to fifteen archive opens, each
///   parsing a header, hash table and block table, for EVERY file read — and a
///   miss walks all fifteen. Loading Northshire reads roughly 660 files
///   (190 models, 474 textures), so startup was doing on the order of ten
///   thousand archive opens. That was the 27 seconds each for buildings and
///   doodads, not the ADT parsing.
///
/// HOW IT HOOKS IN
///   AdtTerrainReader already has a `StormLibExtractor` delegate it tries
///   before the reopen loop. Pointing that at this mount routes every existing
///   call site through it with no changes to AdtTerrainReader at all:
///   ReadFileFromMpqs, ReadBlpPixels and ReadFromMpq all benefit at once.
///
///   Load order follows the 1.12 rule: numbered patches in descending numeric
///   priority, then the unnumbered patch tier, then base archives. Locale
///   patches outrank the global patch within the same tier.
///
/// THREADING (2026-07-26 — this is the change that unblocks parallel streaming)
///   MpqArchive.ReadFile is FULLY concurrent-safe: it reads only immutable
///   fields (hash/block tables, the file handle), allocates every working buffer
///   locally per call, and uses positioned RandomAccess I/O with no shared file
///   cursor (see MpqArchive.cs's own THREAD SAFETY note). The old global read
///   lock here was therefore over-conservative — it serialized every archive
///   extraction, so eight worker threads decoding a zone's models and textures
///   all funnelled through one lock and streamed in one file at a time (the ~30 s
///   of doodads popping in after a zone load). Reads now run in PARALLEL under a
///   shared read-lock; only Dispose takes the write lock (so a shutdown can't
///   free a handle mid-read). Returned byte arrays are independent; parsing and
///   BLP decoding continue to run concurrently on the workers.
/// </summary>
public sealed class MpqMount : IDisposable
{
    private readonly List<(string Name, MpqArchive Archive)> _archives = [];
    private readonly ConcurrentDictionary<string, byte> _negative =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In-memory files that win over every archive: the creator's imported art
    /// (SPELL_CREATOR_IDE §2.7). Keyed by internal path, case-insensitive.</summary>
    private readonly ConcurrentDictionary<string, byte[]> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    // Reads take the read lock (concurrent); Dispose takes the write lock.
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // Interlocked because reads now run on many threads at once.
    private int _reads, _hits, _misses;

    public int ArchiveCount => _archives.Count;
    public int Reads => _reads;
    public int Hits => _hits;
    public int Misses => _misses;

    public MpqMount(string clientDataPath)
    {
        foreach (var path in LoadOrder(clientDataPath))
        {
            try
            {
                var archive = MpqArchive.Open(path);
                if (archive is not null)
                    _archives.Add((Path.GetFileName(path), archive));
            }
            catch (Exception ex)
            {
                // A bad archive is worth naming, but not worth refusing to
                // start over — the file may simply not be one we need.
                Console.WriteLine($"[mpq] could not open {Path.GetFileName(path)} - {ex.Message}");
            }
        }

        Console.WriteLine($"[mpq] priority: {string.Join(" > ", _archives.Select(x => x.Name))}");
        Console.WriteLine($"[mpq] mounted {_archives.Count} archive(s), held open (parallel reads)");
    }

    /// <summary>
    /// Read a file by its internal MPQ path, or null if no archive has it.
    /// Signature matches AdtTerrainReader.StormLibExtractor deliberately.
    /// Safe to call concurrently from the worker pool.
    /// </summary>
    public byte[]? ReadFile(string internalPath)
    {
        _lock.EnterReadLock();
        try
        {
            Interlocked.Increment(ref _reads);

            if (_overrides.TryGetValue(internalPath, out byte[]? overridden))
            {
                Interlocked.Increment(ref _hits);
                return overridden;
            }

            if (_negative.ContainsKey(internalPath))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            foreach (var (_, archive) in _archives)
            {
                try
                {
                    var data = archive.ReadFile(internalPath);
                    if (data is not null) { Interlocked.Increment(ref _hits); return data; }
                }
                catch
                {
                    // Wrong archive, or a read this one cannot satisfy. Next.
                }
            }

            Interlocked.Increment(ref _misses);
            _negative.TryAdd(internalPath, 0);
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Serve <paramref name="bytes"/> for <paramref name="internalPath"/> ahead of
    /// every archive (a never-archived path works too). Null clears the override.</summary>
    public void SetOverride(string internalPath, byte[]? bytes)
    {
        if (bytes is null) _overrides.TryRemove(internalPath, out _);
        else _overrides[internalPath] = bytes;
        _negative.TryRemove(internalPath, out _);
    }

    public void ClearOverride(string internalPath) => SetOverride(internalPath, null);

    /// <summary>
    /// Hot (un)mount one archive by file name — the World Builder's patch-7.MPQ download
    /// (shared_docs/WORLD_BUILDER.md §4). Null <paramref name="path"/> closes and drops the named
    /// archive (so the file can be replaced on disk); a path opens it and re-sorts the chain with
    /// <see cref="OrderArchives"/>. The negative cache is cleared either way. Returns false when
    /// the new file is not a readable MPQ (the archive then stays unmounted).
    /// </summary>
    public bool ReplaceArchive(string fileName, string? path)
    {
        _lock.EnterWriteLock();
        try
        {
            for (int i = _archives.Count - 1; i >= 0; i--)
            {
                if (!_archives[i].Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
                _archives[i].Archive.Dispose();
                _archives.RemoveAt(i);
            }
            _negative.Clear();
            if (path is null) return true;

            var opened = MpqArchive.Open(path);
            if (opened is null) return false;
            _archives.Add((fileName, opened));
            var order = OrderArchives(_archives.Select(a => a.Name)).ToList();
            _archives.Sort((a, b) => order.IndexOf(a.Name).CompareTo(order.IndexOf(b.Name)));
            Console.WriteLine($"[mpq] hot-mounted {fileName}; priority: {string.Join(" > ", _archives.Select(x => x.Name))}");
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool IsMounted(string fileName)
    {
        _lock.EnterReadLock();
        try { return _archives.Any(a => a.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Read through the normal priority chain but skipping one archive (overrides and the
    /// negative cache are bypassed). The World Builder reads STOCK terrain this way while patch-7
    /// is mounted.</summary>
    public byte[]? ReadFileExcluding(string internalPath, string excludedArchive)
    {
        _lock.EnterReadLock();
        try
        {
            foreach (var (name, archive) in _archives)
            {
                if (name.Equals(excludedArchive, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var data = archive.ReadFile(internalPath);
                    if (data is not null) return data;
                }
                catch { }
            }
            return null;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Pure 1.12 archive-priority computation. Highest priority is returned
    /// first because ReadFile returns the first matching file.
    /// </summary>
    public static IReadOnlyList<string> OrderArchives(IEnumerable<string> names)
    {
        var archives = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (Name: name, Patch: ParsePatchName(Path.GetFileName(name))))
            .ToArray();

        var patches = archives
            .Where(x => x.Patch is not null)
            .OrderByDescending(x => x.Patch!.Value.Number.HasValue)
            .ThenByDescending(x => x.Patch!.Value.Number ?? -1)
            .ThenByDescending(x => x.Patch!.Value.Locale.Length > 0)
            .ThenBy(x => x.Patch!.Value.Locale, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => Path.GetFileName(x.Name), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name);

        var bases = archives
            .Where(x => x.Patch is null)
            .OrderBy(x => BaseArchiveRank(Path.GetFileName(x.Name)))
            .ThenBy(x => Path.GetFileName(x.Name), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name);

        return patches.Concat(bases).ToArray();
    }

    /// <summary>
    /// Diagnostic sibling of <see cref="ReadFile"/> that also returns the
    /// winning archive name. It follows the identical mounted priority chain
    /// and performs no writes; batch evidence uses it to make provenance a
    /// first-class string rather than inferring it from pixels.
    /// </summary>
    public (byte[] Data, string Supplier)? ReadFileWithSupplier(string internalPath)
    {
        _lock.EnterReadLock();
        try
        {
            Interlocked.Increment(ref _reads);

            if (_overrides.TryGetValue(internalPath, out byte[]? overridden))
            {
                Interlocked.Increment(ref _hits);
                return (overridden, "override");
            }

            if (_negative.ContainsKey(internalPath))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            foreach (var (name, archive) in _archives)
            {
                try
                {
                    byte[]? data = archive.ReadFile(internalPath);
                    if (data is null) continue;
                    Interlocked.Increment(ref _hits);
                    return (data, name);
                }
                catch
                {
                    // Same fall-through law as ReadFile: try the next archive.
                }
            }

            Interlocked.Increment(ref _misses);
            _negative.TryAdd(internalPath, 0);
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public (byte[] Data, string Supplier)? ReadFileFromSupplier(string internalPath, string supplier)
    {
        _lock.EnterReadLock();
        try
        {
            var selected = _archives.FirstOrDefault(x => x.Name.Equals(supplier, StringComparison.OrdinalIgnoreCase));
            if (selected.Archive is null) return null;
            byte[]? data = selected.Archive.ReadFile(internalPath);
            return data is null ? null : (data, selected.Name);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Union the mounted archives' authored listfiles. The vanilla macro-icon chooser enumerates
    /// actual Interface\Icons files rather than trusting SpellIcon.dbc, whose table contains
    /// entries with no corresponding art. Names are returned once, case-insensitively; consumers
    /// that need reference ordering apply their own sort after filtering.
    /// </summary>
    public IReadOnlyList<string> ListedFiles()
    {
        _lock.EnterReadLock();
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((_, MpqArchive archive) in _archives)
            {
                byte[]? bytes;
                try { bytes = archive.ReadFile("(listfile)"); }
                catch { continue; }
                if (bytes is null || bytes.Length == 0) continue;
                foreach (string line in Encoding.UTF8.GetString(bytes)
                             .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (line.Length > 0) names.Add(line);
            }
            return names.ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private readonly record struct PatchName(string Locale, int? Number);

    private static PatchName? ParsePatchName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Equals("patch", StringComparison.OrdinalIgnoreCase))
            return new PatchName("", null);
        if (!stem.StartsWith("patch-", StringComparison.OrdinalIgnoreCase))
            return null;

        string suffix = stem[6..];
        string[] parts = suffix.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        if (int.TryParse(parts[^1], out int number) && number >= 0)
        {
            string locale = parts.Length == 1 ? "" : string.Join('-', parts[..^1]);
            return new PatchName(locale, number);
        }

        return new PatchName(suffix, null);
    }

    private static int BaseArchiveRank(string fileName)
    {
        if (fileName.Equals("terrain.mpq", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.Equals("model.mpq", StringComparison.OrdinalIgnoreCase)) return 1;
        return 10;
    }

    private static List<string> LoadOrder(string clientDataPath)
    {
        var result = new List<string>();
        if (!Directory.Exists(clientDataPath)) return result;

        var all = Directory.GetFiles(clientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(clientDataPath, "*.mpq", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.AddRange(OrderArchives(all));

        return result;
    }

    public void Report()
        => Console.WriteLine(
            $"[mpq] {Reads:N0} read(s), {Hits:N0} found, {Misses:N0} not present, " +
            $"{_archives.Count} archive(s) still open");

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var (_, archive) in _archives) archive.Dispose();
            _archives.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}
