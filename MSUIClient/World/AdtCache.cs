using MSUIClient.Formats;
using MSUIClient.Engine;

namespace MSUIClient.World;

/// <summary>
/// Parses each tile's ADT once and hands the same result to everyone who needs it.
///
/// WHY THIS EXISTS
///   Four separate consumers were each reading and parsing the same file:
///   TerrainTile for the mesh, TerrainRenderer for the CPU height grid,
///   WmoRenderer for MODF placements, DoodadRenderer for MDDF placements. Nine
///   tiles therefore cost thirty-six full ADT parses at boot, and an ADT parse
///   is decompression plus 256 chunks of height, normal and alpha data. That is
///   most of the startup time and none of it was doing anything new.
///
/// LIFETIME IS DELIBERATELY SHORT
///   A parsed ADT is not small — 256 chunks of MCVT, MCNR and MCAL each — so
///   holding nine of them forever would trade seconds for a lot of resident
///   memory. The cache is meant to live for one load pass and then be cleared:
///   load terrain, buildings and doodads for a batch of tiles, then drop it.
///   <see cref="Forget"/> exists for the streaming case, where tiles arrive and
///   leave a few at a time.
///
/// A null result is cached as null. Ocean tiles genuinely have no ADT, and
/// re-attempting a missing file once per consumer is exactly the waste this
/// class exists to remove.
/// </summary>
public sealed class AdtCache
{
    private readonly string _clientDataPath;
    private string _mapName;

    /// <summary>
    /// Bumped by <see cref="SetMap"/>. A worker parse started before a map
    /// change must NOT publish into the cache afterwards: the key (col, row) is
    /// the same on every map, so Azeroth's [32,32] would silently become
    /// Deadmines' [32,32] and the tile would render the wrong world with no
    /// error anywhere. PLAN_13 H2.
    /// </summary>
    private int _generation;
    private readonly Dictionary<(int col, int row), AdtTerrainReader.AdtResult?> _cache = [];
    private readonly Dictionary<(int col, int row), Task<AdtTerrainReader.AdtResult?>> _pending = [];
    private readonly object _gate = new();

    /// <summary>Files actually parsed.</summary>
    public int Parses { get; private set; }

    /// <summary>Requests served from an existing parse.</summary>
    public int Hits { get; private set; }

    public AdtCache(string clientDataPath, string mapName)
    {
        _clientDataPath = clientDataPath;
        _mapName = mapName;
    }

    /// <summary>The map directory this cache is currently reading.</summary>
    public string MapName { get { lock (_gate) return _mapName; } }

    /// <summary>
    /// Point the cache at a different map and drop everything it holds.
    ///
    /// THE TRAP THIS EXISTS TO AVOID. Tile keys are (col, row) and every map
    /// uses the same 64x64 grid, so Azeroth's [32,32] and Deadmines' [32,32]
    /// are the same dictionary key holding different worlds. Changing the map
    /// without clearing does not fail - it renders Elwynn inside the Deadmines,
    /// with nothing logged. Clearing is therefore not an optimisation, it is
    /// the correctness condition.
    ///
    /// In-flight worker parses are abandoned rather than awaited: their results
    /// are discarded by the generation check in PublishAsync, and a caller
    /// awaiting one gets null, which every caller already treats as "no ADT
    /// here". Blocking on them instead would make travel wait on work whose
    /// answer is already worthless.
    /// </summary>
    public void SetMap(string mapName)
    {
        int dropped;
        lock (_gate)
        {
            if (string.Equals(_mapName, mapName, StringComparison.OrdinalIgnoreCase)) return;
            _mapName = mapName;
            _generation++;
            dropped = _cache.Count;
            _cache.Clear();
            _pending.Clear();

            // Inside the lock: every increment of these is, so a reset outside
            // it races a worker that is still finishing.
            Parses = 0;
            Hits = 0;
        }

        Console.WriteLine($"[adt] map -> {mapName}, dropped {dropped} cached tile(s) " +
                          $"and abandoned any in-flight parse");
    }

    /// <summary>
    /// The parsed ADT for a tile, or null if it does not exist.
    ///
    /// Note the argument order: ReadFromMpq takes (gridX = row, gridY = col),
    /// inverted from the {col}_{row} filename. That inversion has bitten this
    /// project before, so it is applied here once and nowhere else.
    /// </summary>
    public AdtTerrainReader.AdtResult? Get(int col, int row)
    {
        Task<AdtTerrainReader.AdtResult?>? pending;
        string map;
        int generation;
        lock (_gate)
        {
            if (_cache.TryGetValue((col, row), out var cached))
            {
                Hits++;
                return cached;
            }
            _pending.TryGetValue((col, row), out pending);
            map = _mapName;
            generation = _generation;
        }

        if (pending is not null)
        {
            // Re-check AFTER the wait. A publish can land and SetMap can fire
            // while this caller is parked here, and returning the old map's
            // result once _cache has been dropped is the same corruption fix
            // above closes on the parse path - through the blocking door.
            var result = pending.GetAwaiter().GetResult();
            lock (_gate) if (_generation != generation) return null;
            return result;
        }

        // Read OUTSIDE the lock with the map name captured inside it. Reading
        // the field again down here would use whatever map a concurrent
        // SetMap had just installed, and cache the result under the new
        // generation as if it belonged there.
        var adt = AdtTerrainReader.ReadFromMpq(_clientDataPath, map, row, col);
        lock (_gate)
        {
            // The map changed while this read was in flight. Not caching is only
            // half the job: RETURNING it would hand a TerrainTile or a WMO build
            // Azeroth's bytes for the new map's [32,32], which is the same
            // corruption by the synchronous door. null is what every caller
            // already means by "no ADT here".
            if (_generation != generation) return null;
            _cache[(col, row)] = adt;
            Parses++;
        }
        return adt;
    }

    /// <summary>
    /// The parsed ADT only if it is ALREADY parsed. Never parses, never waits.
    ///
    /// <see cref="Get"/> blocks on a pending parse - correct when the caller
    /// genuinely needs the data, ruinous on the render thread when it does not.
    /// Speculative warming does not need it: an unparsed tile can simply be
    /// retried next frame. Returns true when <paramref name="adt"/> is
    /// authoritative, INCLUDING a cached null for an ocean tile that has no ADT
    /// at all - "known to be absent" is an answer, "not looked at yet" is not.
    /// </summary>
    public bool TryPeek(int col, int row, out AdtTerrainReader.AdtResult? adt)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((col, row), out adt))
            {
                Hits++;
                return true;
            }
        }

        adt = null;
        return false;
    }

    /// <summary>Parse a speculative tile on the bounded asset worker pool.</summary>
    public Task<AdtTerrainReader.AdtResult?> QueueLoad(
        int col, int row, AssetWorkerPool workers)
    {
        var key = (col, row);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Hits++;
                return Task.FromResult(cached);
            }
            if (_pending.TryGetValue(key, out var pending)) return pending;

            string map = _mapName;
            int generation = _generation;
            var worker = workers.RunCritical(() =>
                AdtTerrainReader.ReadFromMpq(_clientDataPath, map, row, col));
            // PublishAsync runs synchronously up to its await. If the worker is
            // already finished it completes INLINE, and its finally removes this
            // key before the line below ever inserts it - leaving a completed
            // task in _pending that nothing will ever take out, which pins the
            // tile's result forever and survives a SetMap.
            var published = PublishAsync(key, worker, generation);
            if (!published.IsCompleted) _pending[key] = published;
            return published;
        }
    }

    private async Task<AdtTerrainReader.AdtResult?> PublishAsync(
        (int col, int row) key, Task<AdtTerrainReader.AdtResult?> worker, int generation)
    {
        try
        {
            var adt = await worker.ConfigureAwait(false);
            lock (_gate)
            {
                // The map changed while this parse was in flight. The bytes are
                // the OLD map's; publishing them under a key the new map also
                // uses is the exact corruption SetMap exists to prevent.
                if (_generation != generation) return null;
                _cache[key] = adt;
                Parses++;
            }
            return adt;
        }
        finally
        {
            // Only remove OUR entry. SetMap already cleared _pending on any
            // generation bump, so an entry still sitting under this key belongs
            // to a newer generation - and removing it would break dedup, letting
            // the next Get miss _pending and do a full blocking parse on the
            // render thread.
            lock (_gate)
                if (_generation == generation) _pending.Remove(key);
        }
    }

    /// <summary>Drop one tile, for when streaming unloads it.</summary>
    public void Forget(int col, int row)
    {
        lock (_gate) _cache.Remove((col, row));
    }

    /// <summary>Keep parsed data only for the currently resident ring.</summary>
    public void Retain(IReadOnlySet<(int col, int row)> resident)
    {
        lock (_gate)
            foreach (var key in _cache.Keys.Where(k => !resident.Contains(k)).ToArray())
                _cache.Remove(key);
    }

    public int HeldTiles { get { lock (_gate) return _cache.Count; } }

    /// <summary>
    /// Drop every parse of the CURRENT map and abandon in-flight ones - the archive chain under
    /// it changed (World Builder hot-mounted patch-7.MPQ). <see cref="SetMap"/> cannot be used for
    /// this: it is a no-op when the map name does not change.
    /// </summary>
    public void Invalidate()
    {
        int dropped;
        lock (_gate)
        {
            _generation++;
            dropped = _cache.Count;
            _cache.Clear();
            _pending.Clear();
        }
        Console.WriteLine($"[adt] invalidated {dropped} cached tile(s) (archives changed)");
    }

    /// <summary>
    /// Drop everything. Call once a load pass is finished — the parsed data is
    /// large and nothing needs it again until tiles change.
    /// </summary>
    public void Clear()
    {
        int held, parses, hits;
        lock (_gate)
        {
            held = _cache.Count;
            _cache.Clear();
            parses = Parses;
            hits = Hits;
            Parses = 0;
            Hits = 0;
        }

        Console.WriteLine(
            $"[adt] {parses} parse(s), {hits} reuse(s) — released {held} cached tile(s)");
    }
}
