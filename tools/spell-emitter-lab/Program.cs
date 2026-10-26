using System.Text.Json.Nodes;
using System.Globalization;
using System.Numerics;
using System.Text;
using MSUIClient;
using MSUIClient.Creator;
using MSUIClient.Creator.Sketch;
#if WEB_M2READER
using WebM2 = MangosSuperUI.Services;
#endif
using MSUIClient.Formats;
using MSUIClient.World.Spells;
using SkiaSharp;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: spell-emitter-lab <client-config.json> [output.csv]");
    return 2;
}

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);

// LAB_DUMP_BLP="a.blp;b.blp" LAB_DUMP_DIR=dir: decode archive images through the CLIENT's own
// BlpDecoder (BGRA) and write PNGs - the colours the game draws, not a viewer's guess - then stop.
if (Environment.GetEnvironmentVariable("LAB_DUMP_BLP") is { Length: > 0 } dumpList)
{
    string dir = Environment.GetEnvironmentVariable("LAB_DUMP_DIR") ?? ".";
    foreach (string blpPath in dumpList.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        byte[]? raw = mpq.ReadFile(blpPath);
        if (raw is null) { Console.WriteLine($"[blp-dump] missing {blpPath}"); continue; }
        byte[] px = BlpDecoder.GetPixels(raw, 0, out int bw, out int bh);
        using var bitmap = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bitmap.GetPixels(), px.Length);
        string outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(blpPath) + ".png");
        using (var stream = File.Create(outPath)) bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        Console.WriteLine($"[blp-dump] {blpPath} {bw}x{bh} enc={raw[8]} alphaDepth={raw[9]} alphaType={raw[10]} -> {outPath}");
    }
    return 0;
}

// LAB_DUMP_DBC="DBFilesClient\X.dbc[:rows]": shape and the first rows (u32 / float side by side), then stop.
if (Environment.GetEnvironmentVariable("LAB_DUMP_DBC") is { Length: > 0 } dbcList)
{
    foreach (string spec in dbcList.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] parts = spec.Split(':');
        int show = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 8;
        DbcFile? dbc = mpq.ReadFile(parts[0]) is { } raw ? DbcFile.Parse(raw) : null;
        if (dbc is null) { Console.WriteLine($"[dbc-dump] missing {parts[0]}"); continue; }
        Console.WriteLine($"[dbc-dump] {parts[0]}: {dbc.RecordCount} rows x {dbc.FieldCount} fields");
        for (int r = 0; r < Math.Min(show, dbc.RecordCount); r++)
            Console.WriteLine("  " + string.Join(" ", Enumerable.Range(0, dbc.FieldCount).Select(f =>
            {
                uint u = dbc.GetUInt(r, f);
                float x = dbc.GetFloat(r, f);
                return u < 0x01000000 || u == 0xFFFFFFFF ? u.ToString() : x.ToString("0.###", CultureInfo.InvariantCulture) + "f";
            })));
    }
    return 0;
}

// LAB_FIND_SOUND="word word": SoundEntries rows whose name or files match every word, then stop.
if (Environment.GetEnvironmentVariable("LAB_FIND_SOUND") is { Length: > 0 } soundQuery)
{
    string[] soundWords = soundQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    foreach (SoundEntry e in SoundEntriesCatalog.Load(mpq)!.Entries.Where(e => soundWords.All(w =>
                 e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                 e.Variants.Any(v => v.Path.Contains(w, StringComparison.OrdinalIgnoreCase)))).OrderBy(e => e.Id).Take(40))
        Console.WriteLine($"[sound] {e.Id} {e.Name} loop={e.Looping} {string.Join(", ", e.Variants.Take(3).Select(v => v.Path))}");
    return 0;
}

// LAB_KIT_CENSUS=1: what the kits use that the client may not play - f12 (world effect) and the
// CharProc types (lanes f15..f18) - with the spells that use them. Then stop.
if (Environment.GetEnvironmentVariable("LAB_KIT_CENSUS") is not null)
{
    DbcFile kitDbc = DbcFile.Parse(mpq.ReadFile(@"DBFilesClient\SpellVisualKit.dbc")!)!;
    DbcFile visDbc = DbcFile.Parse(mpq.ReadFile(@"DBFilesClient\SpellVisual.dbc")!)!;
    SpellCatalog census = SpellCatalog.Load(mpq)!;
    var kitToVisual = new Dictionary<uint, List<uint>>();
    for (int r = 0; r < visDbc.RecordCount; r++)
        for (int f = 1; f <= 5; f++)
        {
            uint k = visDbc.GetUInt(r, f);
            if (k is 0 or uint.MaxValue) continue;
            if (!kitToVisual.TryGetValue(k, out var list)) kitToVisual[k] = list = new List<uint>();
            list.Add(visDbc.GetUInt(r, 0));
        }
    var visualToSpell = census.Spells.GroupBy(s => s.VisualId).ToDictionary(g => g.Key, g => g.Select(s => $"{s.Id} {s.Name}").First());
    string Example(uint kit) => kitToVisual.TryGetValue(kit, out var vs) ? vs.Select(v => visualToSpell.GetValueOrDefault(v)).FirstOrDefault(n => n is not null) ?? "-" : "-";
    var world = new Dictionary<uint, List<uint>>();
    var procTypes = new Dictionary<int, List<uint>>();
    for (int r = 0; r < kitDbc.RecordCount; r++)
    {
        uint kit = kitDbc.GetUInt(r, 0);
        uint w = kitDbc.GetUInt(r, 12);
        if (w is not (0 or uint.MaxValue)) { if (!world.TryGetValue(w, out var l)) world[w] = l = new(); l.Add(kit); }
        for (int lane = 0; lane < 4; lane++)
        {
            int type = kitDbc.GetInt(r, 15 + lane);
            if (type < 0) continue;
            if (!procTypes.TryGetValue(type, out var l2)) procTypes[type] = l2 = new();
            l2.Add(kit);
        }
    }
    DbcFile effectNames = DbcFile.Parse(mpq.ReadFile(@"DBFilesClient\SpellVisualEffectName.dbc")!)!;
    var effectRow = Enumerable.Range(0, effectNames.RecordCount).ToDictionary(r => effectNames.GetUInt(r, 0), r => r);
    string EffectName(uint id) => effectRow.TryGetValue(id, out int er) ? $"{effectNames.GetString(er, 1)} = {effectNames.GetString(er, 2)}" : "(not a SpellVisualEffectName)";
    Console.WriteLine($"[kit-census] f12 world effect: {world.Values.Sum(l => l.Count)} kits, {world.Count} distinct values");
    foreach (var (value, kits) in world.OrderByDescending(x => x.Value.Count).Take(10))
        Console.WriteLine($"[kit-census]   f12={value} x{kits.Count} e.g. kit {kits[0]} ({Example(kits[0])}) -> {EffectName(value)}");
    foreach (var (type, kits) in procTypes.OrderByDescending(x => x.Value.Count))
        Console.WriteLine($"[kit-census] charproc type {type}: {kits.Count} lanes, e.g. kit {kits[0]} ({Example(kits[0])}), kit {kits[^1]} ({Example(kits[^1])})");
    // Every spell whose kits carry a weapon trail (type 8), with its power cost - to find one any class can cast.
    if (procTypes.TryGetValue(8, out var trailKits))
    {
        var trailVisuals = trailKits.SelectMany(k => kitToVisual.GetValueOrDefault(k) ?? new List<uint>()).ToHashSet();
        foreach (SpellInfo s in census.Spells.Where(s => trailVisuals.Contains(s.VisualId)).OrderBy(s => s.PowerType).ThenBy(s => s.ManaCost).Take(20))
            Console.WriteLine($"[kit-census]   trail spell {s.Id} {s.Name} power={s.PowerType} cost={s.ManaCost} attr=0x{s.Attributes:X}");
    }
    // The parameters of the types the client does not play, per lane (p0..p3 as float and as int).
    var rowOfKit = Enumerable.Range(0, kitDbc.RecordCount).ToDictionary(r => kitDbc.GetUInt(r, 0), r => r);
    foreach (int type in new[] { 8, 13, 7, 2, 6, 10, 15 })
        if (procTypes.TryGetValue(type, out var kits))
            foreach (uint kit in kits.Distinct().Take(6))
            {
                int r = rowOfKit[kit];
                for (int lane = 0; lane < 4; lane++)
                    if (kitDbc.GetInt(r, 15 + lane) == type)
                        Console.WriteLine($"[kit-census]   type {type} kit {kit} ({Example(kit)}) lane {lane}: " +
                            string.Join(" ", Enumerable.Range(0, 4).Select(p => { uint u = kitDbc.GetUInt(r, 19 + p * 4 + lane); float f = BitConverter.UInt32BitsToSingle(u); return $"p{p}={f.ToString("0.###", CultureInfo.InvariantCulture)}"; })));
            }
    return 0;
}

#if WEB_M2READER
// LAB_ROUNDTRIP=1 (SPELL_CREATOR_PLAN.md P5.1): every stock SpellVisual rebuilt FROM ZERO through the
// Completer's real cloner, from only what the workshop's composition can say (every phase explicit:
// slots + scales, animation, camera shake, sound, world effect; the missile and its loop sound; the
// area model, its shards and sound) - then diffed against Blizzard's rows by resolved model path.
// "N of M lossless" is how much of vanilla's visual vocabulary a design made from nothing can reach.
if (Environment.GetEnvironmentVariable("LAB_ROUNDTRIP") is not null)
{
    SpellVisualCatalog cat = SpellVisualCatalog.Load(mpq)!;
    byte[] visBytes = mpq.ReadFile(@"DBFilesClient\SpellVisual.dbc")!, kitBytes = mpq.ReadFile(@"DBFilesClient\SpellVisualKit.dbc")!,
           nameBytes = mpq.ReadFile(@"DBFilesClient\SpellVisualEffectName.dbc")!;
    DbcFile stockVis = DbcFile.Parse(visBytes)!, stockKit = DbcFile.Parse(kitBytes)!, stockName = DbcFile.Parse(nameBytes)!;
    var visW = WebM2.DbcWriterService.ReadDbc(visBytes); var kitW = WebM2.DbcWriterService.ReadDbc(kitBytes);
    var nameW = WebM2.DbcWriterService.ReadDbc(nameBytes);
    var kitRow = Enumerable.Range(0, stockKit.RecordCount).ToDictionary(r => stockKit.GetUInt(r, 0), r => r);
    var nameRow = Enumerable.Range(0, stockName.RecordCount).ToDictionary(r => stockName.GetUInt(r, 0), r => r);
    static bool None(uint v) => v is 0 or uint.MaxValue;
    static string Norm(string p) => p.Replace('/', '\\').ToLowerInvariant().Replace(".mdx", ".m2").Replace(".mdl", ".m2");
    string StockPath(uint effect) => None(effect) || !nameRow.TryGetValue(effect, out int er) ? "" : Norm(stockName.GetString(er, 2));
    // A cloned effect row names a CUSTOM path the patch builder fills with the source model's bytes:
    // what it will show is the ORIGINAL model (CloneResult.EffectFiles), so that is what is compared.
    var origin = new Dictionary<uint, string>();
    string NewPath(uint effect) => None(effect) ? "" : origin.TryGetValue(effect, out string? src) ? Norm(src)
        : nameW.GetRow(effect) is { } row ? Norm(nameW.ReadString(row[2])) : "";
    var f1ByStage = new Dictionary<string, Dictionary<uint, int>>();
    string[] stageNames = { "precast", "cast", "impact", "state", "channel" };
    foreach (int f in new[] { 8, 14, 15 })
    {
        var values = Enumerable.Range(0, stockVis.RecordCount).Select(rr => stockVis.GetUInt(rr, f)).Where(v => !None(v)).ToList();
        Console.WriteLine($"[roundtrip] visual f{f}: {values.Count} rows set, {values.Distinct().Count()} distinct, {values.Count(v => kitRow.ContainsKey(v))} name a kit; e.g. {string.Join(",", values.Distinct().Take(8))}");
    }
    int total = 0, lossless = 0, danglingKits = 0;
    var lost = new Dictionary<string, int>(StringComparer.Ordinal);
    var lostExamples = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
    uint newVisual = 100_000, nextKit = 200_000, nextEffect = 300_000;
    for (int r = 0; r < stockVis.RecordCount; r++)
    {
        uint vid = stockVis.GetUInt(r, 0);
        if (!cat.TryGetStages(vid, out SpellVisualStages st)) continue;
        var comp = new JsonObject();
        uint[] stageKits = { st.Precast, st.Cast, st.Impact, st.State, st.Channel };
        for (int s = 0; s < 5; s++)
        {
            if (None(stageKits[s]) || !cat.TryGetKit(stageKits[s], out SpellVisualKitInfo kit)) continue;
            var slots = new JsonArray();
            foreach (SpellVisualKitEffect e in kit.Effects)
                slots.Add(new JsonObject { ["slot"] = Array.IndexOf(SpellVisualCatalog.KitAttachmentIds, e.AttachmentId), ["modelPath"] = e.ModelPath, ["scale"] = 1f });
            comp[stageNames[s]] = new JsonObject
            {
                ["animationId"] = kit.AnimationId is { } a ? a : null,
                ["slots"] = slots,
                ["cameraShake"] = kit.Shake ?? 0u,
                ["sound"] = kit.Sound is { } snd ? snd : null,
                ["worldEffect"] = kit.WorldEffect ?? "",
                ["kitType"] = kitRow.TryGetValue(stageKits[s], out int kt) ? stockKit.GetUInt(kt, 1) : null,
                // Positional: an empty lane before a used one is said as type -1 (the catalog compacts lanes).
                ["charProcs"] = kitRow.TryGetValue(stageKits[s], out int pr) ? new JsonArray(Enumerable.Range(0, 4).Select(lane => (JsonNode)new JsonObject
                {
                    ["type"] = stockKit.GetInt(pr, 15 + lane),
                    ["params"] = new JsonArray(Enumerable.Range(0, 4).Select(p => (JsonNode)stockKit.GetFloat(pr, 19 + p * 4 + lane)).ToArray()),
                }).ToArray()) : new JsonArray(),
            };
        }
        if (cat.MissilePath(st) is { Length: > 0 } || !None(st.MissileSound))
            comp["missile"] = new JsonObject
            {
                ["modelPath"] = cat.MissilePath(st) ?? "", ["scale"] = 1f,
                ["sound"] = None(st.MissileSound) ? null : st.MissileSound,
                ["attachment"] = stockVis.GetUInt(r, 9),
            };
        var rawFields = new JsonObject();
        foreach (int f in new[] { 8, 9, 14, 15 })
            if (f < stockVis.FieldCount && stockVis.GetUInt(r, f) != 0) rawFields[f.ToString()] = stockVis.GetUInt(r, f);
        if (rawFields.Count > 0) comp["visualFields"] = rawFields;
        if (cat.TryGetAreaVisual(vid, out SpellAreaVisualInfo area))
        {
            SpellAreaEmitterInfo? shard = area.Emitters.Count > 0 ? area.Emitters[0] : null;
            comp["area"] = new JsonObject
            {
                ["modelPath"] = area.LoopingModelPath ?? "", ["scale"] = 1f,
                ["shardSelector"] = shard is { } sh ? sh.Selector : -1, ["shardRate"] = shard is { } sr ? sr.InstancesPerSecond : 0f,
                ["sound"] = area.Sound is { } asnd ? asnd : null,
            };
        }
        var result = WebM2.SpellVisualCloner.Clone(visW, kitW, nameW, 0, newVisual, nextKit, nextEffect, $"rt{vid}",
            WebM2.SpellComposition.Parse(JsonNode.Parse(comp.ToJsonString())));   // as it arrives: JSON TEXT, like the web request
        uint[] made = visW.GetRow(newVisual)!;
        origin.Clear();
        foreach (var file in result.EffectFiles) origin[file.NewEffectId] = file.OriginalM2Path;
        for (int s3 = 0; s3 < 5; s3++)
            if (kitRow.TryGetValue(stockVis.GetUInt(r, 1 + s3), out int kr3))
            {
                if (!f1ByStage.TryGetValue(stageNames[s3], out var h)) f1ByStage[stageNames[s3]] = h = new();
                uint f1 = stockKit.GetUInt(kr3, 1);
                h[f1] = h.GetValueOrDefault(f1) + 1;
            }
        var diffs = new List<string>();
        void Diff(string what, bool same) { if (!same) diffs.Add(what); }
        for (int s = 0; s < 5; s++)
        {
            uint oldKit = stockVis.GetUInt(r, 1 + s), newKit = made[1 + s];
            if (None(oldKit)) { Diff($"{stageNames[s]}: kit made from nothing", None(newKit)); continue; }
            if (!kitRow.ContainsKey(oldKit) && None(newKit)) { danglingKits++; continue; }   // stock names a kit that does not exist: nothing renders either way
            if (None(newKit) || !kitRow.TryGetValue(oldKit, out int kr) || kitW.GetRow(newKit) is not { } nk) { diffs.Add($"{stageNames[s]}: kit missing"); continue; }
            for (int f = 3; f <= 11; f++) Diff($"kit slot f{f}", StockPath(stockKit.GetUInt(kr, f)) == NewPath(nk[f]));
            Diff("kit f1 kit type", stockKit.GetUInt(kr, 1) == nk[1]);
            Diff("kit f2 anim", (None(stockKit.GetUInt(kr, 2)) ? 0 : stockKit.GetUInt(kr, 2)) == (None(nk[2]) ? 0 : nk[2]));
            Diff("kit f12 world", StockPath(stockKit.GetUInt(kr, 12)) == NewPath(nk[12]));
            Diff("kit f13 sound", (None(stockKit.GetUInt(kr, 13)) ? 0 : stockKit.GetUInt(kr, 13)) == (None(nk[13]) ? 0 : nk[13]));
            Diff("kit f14 shake", (None(stockKit.GetUInt(kr, 14)) ? 0 : stockKit.GetUInt(kr, 14)) == (None(nk[14]) ? 0 : nk[14]));
            for (int lane = 0; lane < 4; lane++)
            {
                int oldType = stockKit.GetInt(kr, 15 + lane), newType = (int)nk[15 + lane];
                bool paramsSame = oldType < 0 || Enumerable.Range(0, 4).All(p => stockKit.GetUInt(kr, 19 + p * 4 + lane) == nk[19 + p * 4 + lane]);
                Diff($"kit charproc type {oldType}", (oldType == newType || (oldType < 0 && newType < 0)) && paramsSame);
            }
        }
        Diff("visual f7 missile", StockPath(stockVis.GetUInt(r, 7)) == NewPath(made[7]));
        Diff("visual f9 missile attachment", stockVis.GetUInt(r, 9) == made[9]);
        Diff("visual f10 missile sound", (None(stockVis.GetUInt(r, 10)) ? 0 : stockVis.GetUInt(r, 10)) == (None(made[10]) ? 0 : made[10]));
        Diff("visual f12 area model", StockPath(stockVis.GetUInt(r, 12)) == NewPath(made[12]));
        foreach (int f in new[] { 8, 14, 15 })
            if (f < stockVis.FieldCount) Diff($"visual f{f}", stockVis.GetUInt(r, f) == (f < made.Length ? made[f] : 0));
        if (Environment.GetEnvironmentVariable("LAB_ROUNDTRIP_SHOW") is { } show && show == vid.ToString())
        {
            Console.WriteLine($"[roundtrip] visual {vid} stock: {string.Join(",", Enumerable.Range(0, stockVis.FieldCount).Select(f => stockVis.GetUInt(r, f)))}");
            Console.WriteLine($"[roundtrip] visual {vid} made : {string.Join(",", made)}");
            for (int s2 = 0; s2 < 5; s2++)
            {
                uint ok2 = stockVis.GetUInt(r, 1 + s2);
                if (kitRow.TryGetValue(ok2, out int kr2)) Console.WriteLine($"[roundtrip]   {stageNames[s2]} stock kit {ok2}: {string.Join(",", Enumerable.Range(0, 19).Select(f => stockKit.GetUInt(kr2, f)))}");
                if (kitW.GetRow(made[1 + s2]) is { } nk2) Console.WriteLine($"[roundtrip]   {stageNames[s2]} made  kit {made[1 + s2]}: {string.Join(",", nk2.Take(19))}");
            }
            Console.WriteLine($"[roundtrip] composition: {comp.ToJsonString()}");
            Console.WriteLine($"[roundtrip] diffs: {string.Join("; ", diffs)}");
        }
        total++;
        if (diffs.Count == 0) lossless++;
        foreach (string d in diffs.Distinct())
        {
            lost[d] = lost.GetValueOrDefault(d) + 1;
            if (!lostExamples.TryGetValue(d, out var ex)) lostExamples[d] = ex = new();
            if (ex.Count < 6) ex.Add(vid);
        }
        newVisual++; nextKit += 16; nextEffect += 64;
    }
    Console.WriteLine($"[roundtrip] {lossless} of {total} stock spell visuals rebuilt from zero losslessly ({danglingKits} stock kit reference(s) name no SpellVisualKit row: nothing renders, none written)");
    foreach (var (stageName, h) in f1ByStage)
        Console.WriteLine($"[roundtrip]   kit f1 by phase {stageName}: {string.Join(", ", h.OrderByDescending(x => x.Value).Take(6).Select(x => $"{x.Key} x{x.Value}"))}");
    foreach (var (what, count) in lost.OrderByDescending(x => x.Value).Take(20))
        Console.WriteLine($"[roundtrip]   lost in {count}: {what} (visuals {string.Join(",", lostExamples[what])})");
    return 0;
}
#endif

SpellCatalog spells = SpellCatalog.Load(mpq)
    ?? throw new InvalidOperationException("Spell.dbc could not be loaded");
SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq)
    ?? throw new InvalidOperationException("SpellVisual catalogs could not be loaded");

var uses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
void Add(string? path, string use)
{
    if (string.IsNullOrWhiteSpace(path)) return;
    path = SpellVisualCatalog.ModelPath(path);
    if (!uses.TryGetValue(path, out HashSet<string>? set)) uses[path] = set = [];
    if (set.Count < 24) set.Add(use);
}
void AddKit(in SpellInfo spell, uint kitId, string phase)
{
    if (kitId == 0 || !visuals.TryGetKit(kitId, out SpellVisualKitInfo kit)) return;
    foreach (SpellVisualKitEffect effect in kit.Effects)
        Add(effect.ModelPath, $"{phase}:{spell.Id}:{spell.Name}");
}

foreach (SpellInfo spell in spells.Spells.Where(s => s.VisualId != 0))
{
    if (!visuals.TryGetStages(spell.VisualId, out SpellVisualStages stages)) continue;
    AddKit(spell, stages.Precast, "precast");
    AddKit(spell, stages.Cast, "cast");
    AddKit(spell, stages.Impact, "impact");
    AddKit(spell, stages.State, "state");
    AddKit(spell, stages.Channel, "channel");
    Add(visuals.MissilePath(stages), $"missile:{spell.Id}:{spell.Name}");
    if (visuals.TryGetAreaVisual(spell.VisualId, out SpellAreaVisualInfo area))
    {
        Add(area.LoopingModelPath, $"area-loop:{spell.Id}:{spell.Name}");
        foreach (SpellAreaEmitterInfo emitter in area.Emitters)
            Add(emitter.ModelPath, $"area-shard:{spell.Id}:{spell.Name}");
    }
}

var rows = new List<Row>();
var unresolved = new List<string>();
// Ribbon census for the Sketch spec's trail presets (shared_docs/SPELL_SKETCH.md §4.5).
var ribbonTextures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var ribbonExamples = new List<string>();
int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
(string Path, byte[] Bytes)? patchFixture = null;
(string Path, byte[] Bytes)? meshFixture = null;
(string Path, byte[] Bytes)? ribbonFixture = null;
int meshLayers = 0, meshLayersWithColour = 0, ribbonCount = 0;
foreach ((string path, HashSet<string> modelUses) in uses.OrderBy(p => p.Key,
             StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (bytes is null || model is null)
    {
        unresolved.Add(path);
        continue;
    }

    List<EmitterSnapshot> creatorEmitters = M2EmitterParser.ReadEmitters(bytes);
    Check(creatorEmitters.Count == model.ParticleEmitters.Count,
        $"Creator/runtime emitter count mismatch: {path}");
    List<MSUIClient.Creator.M2TextureEntry> creatorTextures =
        MSUIClient.Creator.M2TextureParser.ParseTextures(bytes);
    Check(creatorTextures.Count == model.Textures.Count,
        $"Creator/runtime texture count mismatch: {path}");
    Dictionary<int, byte> creatorBlendModes =
        MSUIClient.Creator.M2TextureParser.GetTextureBlendModes(bytes);
    foreach (MSUIClient.Creator.M2TextureEntry texture in creatorTextures)
    {
        int[] expectedRefs = model.ParticleEmitters.Select((e, i) => (e, i))
            .Where(pair => pair.e.Texture == texture.Index).Select(pair => pair.i).ToArray();
        Check(texture.ReferencedByEmitters.OrderBy(i => i).SequenceEqual(expectedRefs),
            $"Creator texture back-reference mismatch: {path} t{texture.Index}");
        byte expectedBlend = expectedRefs.Length == 0 ? (byte)4 :
            expectedRefs.Max(i => model.ParticleEmitters[i].BlendingType);
        Check(creatorBlendModes.GetValueOrDefault(texture.Index, byte.MaxValue) == expectedBlend,
            $"Creator texture blend mismatch: {path} t{texture.Index}");
    }
    if (patchFixture is null && creatorEmitters.Any(e =>
            e.TrackValues.GetValueOrDefault("zSource") is not null))
        patchFixture = (path, bytes);

    // ── mesh layers + ribbons (SPELL_CREATOR_IDE §2.5): the Creator readers against M2Reader ──
    List<MeshSnapshot> layers = M2MeshParser.ReadMeshes(bytes);
    Check(layers.Count == model.Batches.Count, $"mesh layer count mismatch: {path} creator={layers.Count} runtime={model.Batches.Count} version={model.Version}");
    for (int i = 0; i < layers.Count && i < model.Batches.Count; i++)
    {
        MeshSnapshot layer = layers[i];
        M2Batch batch = model.Batches[i];
        Check(layer.BatchIndex == i && layer.SubmeshIndex == batch.SubmeshIndex,
            $"mesh layer submesh mismatch: {path} m{i}");
        Check(layer.MaterialIndex == batch.MaterialIndex && layer.ColorIndex == batch.ColorIndex,
            $"mesh layer material/colour index mismatch: {path} m{i}");
        int expectedSlot = batch.TextureIndex < model.TextureLookup.Count
            ? model.TextureLookup[batch.TextureIndex] : -1;
        Check(layer.TextureSlot == expectedSlot, $"mesh layer texture slot mismatch: {path} m{i}");
        if (batch.SubmeshIndex < model.Submeshes.Count)
            Check(layer.TriangleIndexCount == model.Submeshes[batch.SubmeshIndex].IndexCount,
                $"mesh layer triangle count mismatch: {path} m{i}");
        if (batch.MaterialIndex < model.RenderFlags.Count)
            Check(layer.Blend == model.RenderFlags[batch.MaterialIndex].BlendingMode &&
                  layer.MaterialFlags == model.RenderFlags[batch.MaterialIndex].Flags,
                $"mesh layer material mismatch: {path} m{i}");
        if (batch.ColorIndex >= 0 && batch.ColorIndex < model.Colors.Count)
        {
            M2ColorAnimation colour = model.Colors[batch.ColorIndex];
            Check(layer.ColorKeys == colour.Color.Keys.Count && layer.AlphaKeys == colour.Alpha.Keys.Count,
                $"mesh layer colour key count mismatch: {path} m{i}");
            if (colour.Color.Keys.Count > 0)
                Check(layer.ColorFirst is { } firstColour && NearV(firstColour, colour.Color.Keys[0]),
                    $"mesh layer colour first key mismatch: {path} m{i}");
            if (colour.Alpha.Keys.Count > 0)
                Check(layer.AlphaFirst is { } firstAlpha &&
                      MathF.Abs(firstAlpha - Math.Clamp(colour.Alpha.Keys[0] / 32767f, 0f, 1f)) <= 1e-5f,
                    $"mesh layer alpha first key mismatch: {path} m{i}");
            meshLayersWithColour++;
        }
        if (batch.TextureWeightIndex < model.TransparencyLookup.Count)
        {
            int transparency = model.TransparencyLookup[batch.TextureWeightIndex];
            if (transparency < model.TransparencyTracks.Count)
            {
                M2AnimTrack<short> track = model.TransparencyTracks[transparency];
                Check(layer.TransparencyIndex == transparency && layer.TransparencyKeys == track.Keys.Count,
                    $"mesh layer transparency index mismatch: {path} m{i}");
                if (track.Keys.Count > 0)
                    Check(layer.TransparencyFirst is { } weight &&
                          MathF.Abs(weight - Math.Clamp(track.Keys[0] / 32767f, 0f, 1f)) <= 1e-5f,
                        $"mesh layer transparency first key mismatch: {path} m{i}");
            }
        }
    }
    meshLayers += layers.Count;
    if (meshFixture is null && layers.Any(l => l.ColorFirst is not null && l.AlphaFirst is not null &&
                                               l.TriangleIndexCount > 0 && l.TextureSlot >= 0))
        meshFixture = (path, bytes);

    List<RibbonSnapshot> ribbons = M2RibbonParser.ReadRibbons(bytes);
    Check(ribbons.Count == model.RibbonEmitters.Count, $"ribbon count mismatch: {path}");
    for (int i = 0; i < ribbons.Count && i < model.RibbonEmitters.Count; i++)
    {
        RibbonSnapshot ribbon = ribbons[i];
        M2RibbonEmitter runtime = model.RibbonEmitters[i];
        Check(ribbon.Index == i && ribbon.Bone == runtime.Bone, $"ribbon bone mismatch: {path} r{i}");
        Check(Near(ribbon.PositionX, runtime.Position.X) && Near(ribbon.PositionY, -runtime.Position.Z) &&
              Near(ribbon.PositionZ, runtime.Position.Y), $"ribbon raw position mismatch: {path} r{i}");
        if (runtime.Texture != ushort.MaxValue)
            Check(ribbon.TextureSlot == runtime.Texture, $"ribbon texture slot mismatch: {path} r{i}");
        if (runtime.Material != ushort.MaxValue)
            Check(ribbon.MaterialIndex == runtime.Material, $"ribbon material index mismatch: {path} r{i}");
        Check(Near(ribbon.EdgesPerSecond, runtime.EdgesPerSecond) &&
              Near(MathF.Max(.25f, ribbon.EdgeLifetime), runtime.EdgeLifetime) &&
              Near(ribbon.Gravity, runtime.Gravity), $"ribbon timing mismatch: {path} r{i}");
        Check(Math.Max((ushort)1, ribbon.TextureRows) == runtime.TextureRows &&
              Math.Max((ushort)1, ribbon.TextureColumns) == runtime.TextureColumns,
            $"ribbon sprite cells mismatch: {path} r{i}");
        Check(ribbon.HeightAboveKeys == runtime.HeightAbove.Keys.Count &&
              ribbon.HeightBelowKeys == runtime.HeightBelow.Keys.Count &&
              ribbon.ColorKeys == runtime.Color.Keys.Count && ribbon.AlphaKeys == runtime.Alpha.Keys.Count &&
              ribbon.VisibilityKeys == runtime.Visibility.Keys.Count,
            $"ribbon key count mismatch: {path} r{i}");
        if (runtime.HeightAbove.Keys.Count > 0)
            Check(ribbon.HeightAboveFirst is { } above && Near(above, runtime.HeightAbove.Keys[0]),
                $"ribbon height-above first key mismatch: {path} r{i}");
        if (runtime.Color.Keys.Count > 0)
            Check(ribbon.ColorFirst is { } colour && NearV(colour, runtime.Color.Keys[0]),
                $"ribbon colour first key mismatch: {path} r{i}");
    }
    ribbonCount += ribbons.Count;
    if (ribbonFixture is null && ribbons.Any(r => r.HeightAboveFirst is not null && r.MaterialIndex >= 0))
        ribbonFixture = (path, bytes);
    foreach (RibbonSnapshot ribbon in ribbons)
    {
        string ribbonTex = ribbon.TextureSlot >= 0 && ribbon.TextureSlot < model.Textures.Count
            ? model.Textures[ribbon.TextureSlot].Filename : "<none>";
        ribbonTextures[ribbonTex] = ribbonTextures.GetValueOrDefault(ribbonTex) + 1;
        if (!ribbon.Silenced && ribbonExamples.Count < 60)
            ribbonExamples.Add($"{path} r{ribbon.Index} tex={ribbonTex} above={ribbon.HeightAboveFirst:0.##} " +
                               $"below={ribbon.HeightBelowFirst:0.##} edges/s={ribbon.EdgesPerSecond:0.#} " +
                               $"life={ribbon.EdgeLifetime:0.##} blend={ribbon.Blend} bone={ribbon.Bone}");
    }

    uint rawCount = bytes.Length >= 0x144 ? BitConverter.ToUInt32(bytes, 0x13C) : 0;
    uint rawOffset = bytes.Length >= 0x144 ? BitConverter.ToUInt32(bytes, 0x140) : 0;
    for (int i = 0; i < model.ParticleEmitters.Count; i++)
    {
        M2ParticleEmitter emitter = model.ParticleEmitters[i];
        int at = checked((int)rawOffset + i * 504);
        ushort shapeId = i < rawCount && at >= 0 && at + 504 <= bytes.Length
            ? BitConverter.ToUInt16(bytes, at + 0x2A) : ushort.MaxValue;
        EmitterSnapshot creator = creatorEmitters[i];
        Check(creator.EmitterType == shapeId, $"Creator shape mismatch: {path} e{i}");
        Check(creator.TextureId == emitter.Texture, $"Creator texture mismatch: {path} e{i}");
        Check(creator.Bone == emitter.Bone, $"Creator bone mismatch: {path} e{i}");
        Check(creator.Flags == emitter.Flags, $"Creator flags mismatch: {path} e{i}");
        Check(Near(creator.PositionX, emitter.PosX) &&
              Near(creator.PositionY, -emitter.PosZ) &&
              Near(creator.PositionZ, emitter.PosY),
            $"Creator raw position mismatch: {path} e{i}");
        Check(Near(creator.Drag, emitter.Drag), $"Creator drag mismatch: {path} e{i}");
        Check(Near(creator.SpriteSpin, emitter.Spin), $"Creator spin mismatch: {path} e{i}");
        Check(Near(creator.TrackValues.GetValueOrDefault("zSource") ?? 0f, emitter.ZSource),
            $"Creator zSource mismatch: {path} e{i}");
        string texture = emitter.Texture < model.Textures.Count
            ? model.Textures[emitter.Texture].Filename : "";
        rows.Add(new Row(path, string.Join('|', modelUses.OrderBy(x => x)), i, shapeId,
            emitter.Shape.ToString(), texture, emitter.BlendingType, emitter.Flags,
            new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ), emitter.Bone,
            emitter.EmissionRate, emitter.Lifespan, emitter.SteadyStatePopulation,
            emitter.EmissionSpeed, emitter.SpeedVariation, emitter.EmissionAreaLength,
            emitter.EmissionAreaWidth, emitter.VerticalRange, emitter.HorizontalRange,
            emitter.Gravity, emitter.ZSource, emitter.Drag, emitter.Spin,
            emitter.HasBoneSpin, emitter.HasBoneMotion,
            emitter.AngularVelocityMin, emitter.AngularVelocityMax,
            emitter.GeometryModel, emitter.RecursionModel, emitter.HeadOrTail,
            emitter.MidPoint, emitter.EnabledTrack.Keys.Count,
            emitter.TrackKeyCounts.Count(k => k > 1)));
    }
}

foreach (Row row in rows)
{
    ParticleShape expected = row.ShapeId switch
    {
        2 => ParticleShape.Sphere,
        3 => ParticleShape.Spline,
        _ => ParticleShape.Plane,
    };
    Check(row.Shape == expected.ToString(), $"shape decode mismatch: {row.Model} e{row.Emitter}");
    Check(float.IsFinite(row.Rate) && float.IsFinite(row.Life) &&
          float.IsFinite(row.Speed) && float.IsFinite(row.Spin),
        $"non-finite core value: {row.Model} e{row.Emitter}");
}

Check(patchFixture is not null, "no patchable spell emitter fixture found");
if (patchFixture is { } fixture)
{
    byte[] patchedBytes = (byte[])fixture.Bytes.Clone();
    EmitterSnapshot before = M2EmitterParser.ReadEmitters(patchedBytes)[0];
    ushort targetShape = before.EmitterType == 2 ? (ushort)1 : (ushort)2;
    float targetZSource = (before.TrackValues["zSource"] ?? 0f) + 0.375f;
    byte paddingBefore = patchedBytes[before.EmitterBase + 0x29];
    int patched = M2EmitterParser.ApplyEmitterPatch(patchedBytes, new EmitterPatch
    {
        EmitterIndex = 0,
        EmitterType = targetShape,
        Flags = before.Flags ^ 0x10u,
        PositionX = before.PositionX + 0.125f,
        PositionY = before.PositionY - 0.25f,
        PositionZ = before.PositionZ + 0.5f,
        Drag = before.Drag + 0.75f,
        SpriteSpin = before.SpriteSpin - 0.625f,
        ZSource = targetZSource,
    });
    Check(patched == 6, $"expected six patch groups, got {patched}: {fixture.Path}");
    EmitterSnapshot after = M2EmitterParser.ReadEmitters(patchedBytes)[0];
    Check(after.EmitterType == targetShape, "shape patch did not round-trip");
    Check(after.TextureId == before.TextureId, "shape patch changed texture slot");
    Check(patchedBytes[after.EmitterBase + 0x29] == paddingBefore,
        "shape patch changed the +0x29 padding byte");
    Check(after.Flags == (before.Flags ^ 0x10u), "flags patch did not round-trip");
    Check(Near(after.PositionX, before.PositionX + 0.125f) &&
          Near(after.PositionY, before.PositionY - 0.25f) &&
          Near(after.PositionZ, before.PositionZ + 0.5f), "position patch did not round-trip");
    Check(Near(after.Drag, before.Drag + 0.75f), "drag patch did not round-trip");
    Check(Near(after.SpriteSpin, before.SpriteSpin - 0.625f),
        "sprite-spin patch did not round-trip");
    Check(Near(after.TrackValues["zSource"] ?? float.NaN, targetZSource),
        "zSource patch did not round-trip");
    M2Model runtimePatched = M2Reader.Parse(patchedBytes)
        ?? throw new InvalidOperationException("runtime rejected patched fixture");
    Check(runtimePatched.ParticleEmitters[0].Shape ==
          (targetShape == 2 ? ParticleShape.Sphere : ParticleShape.Plane),
        "runtime did not observe patched shape");

    (byte[] cloneBytes, int cloneIndex) = M2ParticlePatcher.CloneEmitter(
        fixture.Bytes, 0, before.TextureId)
        ?? throw new InvalidOperationException("clone fixture failed");
    List<EmitterSnapshot> cloned = M2EmitterParser.ReadEmitters(cloneBytes);
    Check(cloned.Count == M2EmitterParser.ReadEmitters(fixture.Bytes).Count + 1,
        "clone did not append exactly one emitter");
    Check(cloned[cloneIndex].TextureId == before.TextureId &&
          cloned[cloneIndex].EmitterType == before.EmitterType,
        "clone did not retain texture/shape schema");
    float sourceZ = cloned[0].TrackValues["zSource"] ?? 0f;
    Check(M2EmitterParser.PatchTrackValue(cloneBytes, cloneIndex, "zSource", sourceZ + 1f),
        "cloned zSource track could not be patched");
    cloned = M2EmitterParser.ReadEmitters(cloneBytes);
    Check(Near(cloned[0].TrackValues["zSource"] ?? float.NaN, sourceZ),
        "clone zSource patch leaked into source track");
    Check(Near(cloned[cloneIndex].TrackValues["zSource"] ?? float.NaN, sourceZ + 1f),
        "clone zSource private track did not round-trip");
}

// ── mesh layer patch round trips (SPELL_CREATOR_IDE §2.5) ──
Check(meshFixture is not null, "no mesh-layer fixture found");
if (meshFixture is { } meshFx)
{
    List<MeshSnapshot> layers = M2MeshParser.ReadMeshes(meshFx.Bytes);
    MeshSnapshot first = layers.First(l => l.ColorFirst is not null && l.AlphaFirst is not null &&
                                           l.TriangleIndexCount > 0 && l.TextureSlot >= 0);
    int b = first.BatchIndex;
    M2Model authored = M2Reader.Parse(meshFx.Bytes) ?? throw new InvalidOperationException("mesh fixture unparseable");

    byte[] hidden = (byte[])meshFx.Bytes.Clone();
    Check(M2MeshParser.HideSubmesh(hidden, first.SubmeshIndex), "HideSubmesh refused the fixture");
    Check(M2MeshParser.ReadMeshes(hidden)[b].Hidden, "hidden layer still reports triangles");
    M2Model? hiddenModel = M2Reader.Parse(hidden);
    Check(hiddenModel is not null && hiddenModel.Submeshes[first.SubmeshIndex].IndexCount == 0,
        "M2Reader still sees triangles on the hidden submesh");

    ushort blend = first.Blend == 4 ? (ushort)2 : (ushort)4;
    ushort flags = (ushort)(first.MaterialFlags ^ M2MeshParser.FlagTwoSided);
    byte[] patched = M2MeshParser.ApplyMeshPatch((byte[])meshFx.Bytes.Clone(), new MeshPatch
    {
        BatchIndex = b, Blend = blend, MaterialFlags = flags, TextureSlot = first.TextureSlot,
        Color = new Vector3(0.25f, 0.5f, 0.75f), Alpha = 0.5f,
        Transparency = first.TransparencyFirst is null ? null : 0.25f,
    });
    Check(patched.Length > meshFx.Bytes.Length, "mesh patch did not append private records");
    List<MeshSnapshot> after = M2MeshParser.ReadMeshes(patched);
    Check(after[b].Blend == blend && after[b].MaterialFlags == flags, "mesh material patch did not round-trip");
    Check(after[b].MaterialIndex == authored.RenderFlags.Count, "mesh material is not the appended private record");
    Check(after[b].TextureSlot == first.TextureSlot, "texture lookup repoint changed the slot");
    Check(after[b].ColorFirst is { } colourAfter && NearV(colourAfter, new Vector3(0.25f, 0.5f, 0.75f)) &&
          after[b].AlphaFirst is { } alphaAfter && MathF.Abs(alphaAfter - 0.5f) <= 1f / 32767f,
        "mesh colour/alpha first keys did not round-trip");
    if (first.TransparencyFirst is not null)
        Check(after[b].TransparencyFirst is { } weightAfter && MathF.Abs(weightAfter - 0.25f) <= 1f / 32767f,
            "mesh transparency first key did not round-trip");
    for (int i = 0; i < layers.Count; i++)
        if (i != b)
            Check(after[i].MaterialIndex == layers[i].MaterialIndex && after[i].Blend == layers[i].Blend &&
                  after[i].TextureSlot == layers[i].TextureSlot, $"mesh patch leaked into layer m{i}");
    M2Model? patchedModel = M2Reader.Parse(patched);
    Check(patchedModel is not null &&
          patchedModel.Batches[b].MaterialIndex == authored.RenderFlags.Count &&
          patchedModel.RenderFlags[patchedModel.Batches[b].MaterialIndex].BlendingMode == blend &&
          patchedModel.TextureLookup[patchedModel.Batches[b].TextureIndex] == first.TextureSlot,
        "M2Reader disagrees with the patched mesh layer");
    Check(patchedModel!.RenderFlags.Count == authored.RenderFlags.Count + 1 &&
          patchedModel.Batches.Count == authored.Batches.Count &&
          patchedModel.ParticleEmitters.Count == authored.ParticleEmitters.Count &&
          patchedModel.Vertices.Count == authored.Vertices.Count,
        "mesh patch disturbed unrelated tables");
    Console.WriteLine($"[emitter-lab] mesh fixture: {meshFx.Path} m{b} (hide, private material, lookup, colour, alpha" +
                      $"{(first.TransparencyFirst is null ? "" : ", transparency")}) round-tripped");
}

// ── ribbon patch round trips ──
Check(ribbonFixture is not null, "no ribbon fixture found");
if (ribbonFixture is { } ribbonFx)
{
    List<RibbonSnapshot> ribbons = M2RibbonParser.ReadRibbons(ribbonFx.Bytes);
    RibbonSnapshot first = ribbons.First(r => r.HeightAboveFirst is not null && r.MaterialIndex >= 0);
    int r = first.Index;
    M2Model authored = M2Reader.Parse(ribbonFx.Bytes) ?? throw new InvalidOperationException("ribbon fixture unparseable");
    ushort blend = first.Blend == 4 ? (ushort)2 : (ushort)4;
    byte[] patched = M2RibbonParser.ApplyRibbonPatch((byte[])ribbonFx.Bytes.Clone(), new RibbonPatch
    {
        RibbonIndex = r, Blend = blend, EdgesPerSecond = first.EdgesPerSecond + 1f, EdgeLifetime = 1.5f,
        Gravity = first.Gravity + 0.5f, HeightAbove = 1.25f, PositionX = first.PositionX + 0.125f,
        Color = first.ColorFirst is null ? null : new Vector3(0.1f, 0.2f, 0.3f),
    });
    List<RibbonSnapshot> after = M2RibbonParser.ReadRibbons(patched);
    Check(after[r].Blend == blend && after[r].MaterialIndex == authored.RenderFlags.Count,
        "ribbon material patch did not round-trip privately");
    Check(Near(after[r].EdgesPerSecond, first.EdgesPerSecond + 1f) && Near(after[r].EdgeLifetime, 1.5f) &&
          Near(after[r].Gravity, first.Gravity + 0.5f) && Near(after[r].HeightAboveFirst ?? float.NaN, 1.25f) &&
          Near(after[r].PositionX, first.PositionX + 0.125f), "ribbon field patch did not round-trip");
    if (first.ColorFirst is not null)
        Check(after[r].ColorFirst is { } ribbonColour && NearV(ribbonColour, new Vector3(0.1f, 0.2f, 0.3f)),
            "ribbon colour first key did not round-trip");
    for (int i = 0; i < ribbons.Count; i++)
        if (i != r)
            Check(after[i].MaterialIndex == ribbons[i].MaterialIndex && after[i].Blend == ribbons[i].Blend,
                $"ribbon patch leaked into r{i}");
    M2Model? patchedModel = M2Reader.Parse(patched);
    Check(patchedModel is not null &&
          Near(patchedModel.RibbonEmitters[r].EdgesPerSecond, first.EdgesPerSecond + 1f) &&
          Near(patchedModel.RibbonEmitters[r].HeightAbove.Keys[0], 1.25f) &&
          Near(patchedModel.RibbonEmitters[r].Position.X, first.PositionX + 0.125f) &&
          patchedModel.RibbonEmitters[r].Material == authored.RenderFlags.Count &&
          patchedModel.RenderFlags[patchedModel.RibbonEmitters[r].Material].BlendingMode == blend,
        "M2Reader disagrees with the patched ribbon");

    byte[] off = (byte[])ribbonFx.Bytes.Clone();
    Check(M2RibbonParser.DisableRibbon(off, r), "DisableRibbon refused the fixture");
    Check(M2RibbonParser.ReadRibbons(off)[r].Silenced, "disabled ribbon still commits edges");
    M2Model? offModel = M2Reader.Parse(off);
    Check(offModel is not null && offModel.RibbonEmitters[r].EdgesPerSecond == 0f &&
          offModel.RibbonEmitters[r].Visibility.Keys.All(k => k == 0) &&
          offModel.RibbonEmitters[r].Alpha.Keys.All(k => k == 0),
        "M2Reader still sees a live disabled ribbon");
    Console.WriteLine($"[emitter-lab] ribbon fixture: {ribbonFx.Path} r{r} (private material, fields, first keys, off) round-tripped");
}

// ── ribbon clone: private records, source untouched, M2Reader sees the copy ──
if (ribbonFixture is { } cloneFx)
{
    List<RibbonSnapshot> before = M2RibbonParser.ReadRibbons(cloneFx.Bytes);
    RibbonSnapshot source = before.First(r => r.HeightAboveFirst is not null && r.MaterialIndex >= 0);
    (byte[] cloned, int cloneIndex) = M2RibbonParser.CloneRibbon(cloneFx.Bytes, source.Index)
        ?? throw new InvalidOperationException("CloneRibbon refused the fixture");
    List<RibbonSnapshot> after = M2RibbonParser.ReadRibbons(cloned);
    Check(after.Count == before.Count + 1 && cloneIndex == before.Count, "ribbon clone count");
    Check(after[cloneIndex].TextureSlot == source.TextureSlot && after[cloneIndex].Bone == source.Bone &&
          Near(after[cloneIndex].EdgesPerSecond, source.EdgesPerSecond) &&
          Near(after[cloneIndex].HeightAboveFirst ?? float.NaN, source.HeightAboveFirst ?? float.NaN),
        "ribbon clone is not a faithful copy");
    cloned = M2RibbonParser.ApplyRibbonPatch(cloned, new RibbonPatch
        { RibbonIndex = cloneIndex, HeightAbove = 2.5f, TextureSlot = source.TextureSlot, Blend = 2 });
    after = M2RibbonParser.ReadRibbons(cloned);
    Check(Near(after[cloneIndex].HeightAboveFirst ?? float.NaN, 2.5f) &&
          Near(after[source.Index].HeightAboveFirst ?? float.NaN, source.HeightAboveFirst ?? float.NaN),
        "ribbon clone's private track leaked into the source");
    M2Model? clonedModel = M2Reader.Parse(cloned);
    Check(clonedModel is not null && clonedModel.RibbonEmitters.Count == before.Count + 1 &&
          Near(clonedModel.RibbonEmitters[cloneIndex].HeightAbove.Keys[0], 2.5f) &&
          clonedModel.ParticleEmitters.Count == M2Reader.Parse(cloneFx.Bytes)!.ParticleEmitters.Count,
        "M2Reader disagrees with the cloned ribbon");
    Console.WriteLine($"[emitter-lab] ribbon clone: {cloneFx.Path} r{source.Index} -> r{cloneIndex} round-tripped");
}

// ── mesh layer clone: view 0 grows by one batch with a private material ──
if (meshFixture is { } layerFx)
{
    List<MeshSnapshot> before = M2MeshParser.ReadMeshes(layerFx.Bytes);
    MeshSnapshot source = before.First(l => l.TriangleIndexCount > 0 && l.TextureSlot >= 0);
    M2Model authored = M2Reader.Parse(layerFx.Bytes)!;
    (byte[] cloned, int cloneIndex) = M2MeshParser.CloneLayer(layerFx.Bytes, source.BatchIndex)
        ?? throw new InvalidOperationException("CloneLayer refused the fixture");
    List<MeshSnapshot> after = M2MeshParser.ReadMeshes(cloned);
    Check(after.Count == before.Count + 1 && cloneIndex == before.Count, "layer clone count");
    Check(after[cloneIndex].SubmeshIndex == source.SubmeshIndex && after[cloneIndex].TextureSlot == source.TextureSlot &&
          after[cloneIndex].Blend == source.Blend && after[cloneIndex].MaterialIndex == authored.RenderFlags.Count,
        "layer clone is not a faithful copy with a private material");
    cloned = M2MeshParser.ApplyMeshPatch(cloned, new MeshPatch { BatchIndex = cloneIndex, Blend = (ushort)(source.Blend == 4 ? 2 : 4) });
    after = M2MeshParser.ReadMeshes(cloned);
    Check(after[source.BatchIndex].Blend == source.Blend && after[cloneIndex].Blend != source.Blend,
        "layer clone's blend leaked into the source");
    M2Model? clonedModel = M2Reader.Parse(cloned);
    Check(clonedModel is not null && clonedModel.Batches.Count == authored.Batches.Count + 1 &&
          clonedModel.Batches[cloneIndex].SubmeshIndex == source.SubmeshIndex &&
          clonedModel.Vertices.Count == authored.Vertices.Count,
        "M2Reader disagrees with the cloned layer");
    Console.WriteLine($"[emitter-lab] layer clone: {layerFx.Path} m{source.BatchIndex} -> m{cloneIndex} round-tripped");
}

// ── bones: pivot + rotation first key, re-parsed by M2Reader ──
(string Path, byte[] Bytes)? boneFixture = null;
foreach ((string path, HashSet<string> _) in uses.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    if (bytes is null) continue;
    if (M2BoneParser.ReadBones(bytes).Any(b => b.RotationFirst is not null)) { boneFixture = (path, bytes); break; }
}
Check(boneFixture is not null, "no bone fixture found");
if (boneFixture is { } boneFx)
{
    List<BoneSnapshot> bones = M2BoneParser.ReadBones(boneFx.Bytes);
    M2Model authored = M2Reader.Parse(boneFx.Bytes)!;
    Check(bones.Count == authored.Bones.Count, "bone count mismatch");
    for (int i = 0; i < bones.Count; i++)
    {
        Check(bones[i].Parent == authored.Bones[i].ParentBone && bones[i].KeyBoneId == authored.Bones[i].KeyBoneId,
            $"bone parent/key mismatch b{i}");
        // M2Reader swaps the pivot (x, y, z) -> (x, z, -y).
        Check(Near(bones[i].PivotX, authored.Bones[i].Pivot.X) && Near(bones[i].PivotZ, authored.Bones[i].Pivot.Y) &&
              Near(-bones[i].PivotY, authored.Bones[i].Pivot.Z), $"bone pivot mismatch b{i}");
        Check(bones[i].RotationKeys == authored.Bones[i].Rotation.Keys.Count &&
              bones[i].TranslationKeys == authored.Bones[i].Translation.Keys.Count, $"bone key count mismatch b{i}");
    }
    BoneSnapshot target = bones.First(b => b.RotationFirst is not null);
    byte[] patched = (byte[])boneFx.Bytes.Clone();
    Check(M2BoneParser.ApplyBonePatch(patched, new BonePatch
    {
        BoneIndex = target.Index, PivotX = target.PivotX + 0.25f, PivotY = target.PivotY - 0.5f, PivotZ = target.PivotZ + 1f,
        RotationEulerDegrees = new Vector3(0f, 0f, 90f),
    }), "ApplyBonePatch refused the fixture");
    List<BoneSnapshot> after = M2BoneParser.ReadBones(patched);
    Check(Near(after[target.Index].PivotX, target.PivotX + 0.25f) && Near(after[target.Index].PivotY, target.PivotY - 0.5f) &&
          Near(after[target.Index].PivotZ, target.PivotZ + 1f), "bone pivot patch did not round-trip");
    Vector4 expected = M2BoneParser.EulerToQuaternion(new Vector3(0f, 0f, 90f));
    Vector4 got = after[target.Index].RotationFirst ?? default;
    Check(MathF.Abs(got.X - expected.X) < 1e-5f && MathF.Abs(got.Y - expected.Y) < 1e-5f &&
          MathF.Abs(got.Z - expected.Z) < 1e-5f && MathF.Abs(got.W - expected.W) < 1e-5f,
        "bone rotation first key did not round-trip");
    Vector3 euler = M2BoneParser.QuaternionToEuler(got);
    Check(MathF.Abs(euler.Z - 90f) < 0.01f && MathF.Abs(euler.X) < 0.01f && MathF.Abs(euler.Y) < 0.01f,
        "bone Euler <-> quaternion is not an identity for a yaw");
    M2Model? patchedModel = M2Reader.Parse(patched);
    Check(patchedModel is not null && Near(patchedModel.Bones[target.Index].Pivot.X, target.PivotX + 0.25f),
        "M2Reader disagrees with the patched bone pivot");
    Console.WriteLine($"[emitter-lab] bone fixture: {boneFx.Path} b{target.Index} (pivot, rotation) round-tripped");
}

// ── drag handles (SPELL_CREATOR_IDE §2.10): the law's frame facts against M2Reader, the
// quaternion pose write, and the pure drag arithmetic ──
if (boneFixture is { } handleFx)
{
    List<BoneSnapshot> bones = M2BoneParser.ReadBones(handleFx.Bytes);
    M2Model authored = M2Reader.Parse(handleFx.Bytes)!;
    for (int i = 0; i < bones.Count; i++)
    {
        var rawPivot = new Vector3(bones[i].PivotX, bones[i].PivotY, bones[i].PivotZ);
        Check(NearV(SpellGizmoHandleLaw.Swap(rawPivot), authored.Bones[i].Pivot), $"handle law pivot swap b{i}");
        Check(NearV(SpellGizmoHandleLaw.Unswap(authored.Bones[i].Pivot), rawPivot), $"handle law pivot unswap b{i}");
        if (bones[i].RotationFirst is { } rq && authored.Bones[i].Rotation.Keys.Count > 0)
        {
            Vector4 model = SpellGizmoHandleLaw.RawToModelQuaternion(rq);
            Vector4 runtime = authored.Bones[i].Rotation.Keys[0];
            Check(NearQ(model, runtime), $"handle law quaternion swap b{i}");
            Check(NearQ(SpellGizmoHandleLaw.ModelToRawQuaternion(model), rq), $"handle law quaternion unswap b{i}");
        }
        if (bones[i].TranslationFirst is { } tr && authored.Bones[i].Translation.Keys.Count > 0)
            Check(NearV(SpellGizmoHandleLaw.Swap(tr), authored.Bones[i].Translation.Keys[0]), $"handle law translation swap b{i}");
    }

    BoneSnapshot target = bones.First(b => b.RotationFirst is not null);
    Vector4 pose = Vector4.Normalize(new Vector4(0.1f, 0.2f, 0.3f, 0.9f));
    byte[] patched = (byte[])handleFx.Bytes.Clone();
    Check(M2BoneParser.ApplyBonePatch(patched, new BonePatch
    {
        BoneIndex = target.Index, Rotation = pose, RotationEulerDegrees = new Vector3(0f, 0f, 45f),
    }), "quaternion pose refused");
    Vector4 got = M2BoneParser.ReadBones(patched)[target.Index].RotationFirst ?? default;
    Check(NearQ(got, pose), "quaternion pose did not win over the Euler field, or did not round-trip");
    Vector4 runtimePose = M2Reader.Parse(patched)!.Bones[target.Index].Rotation.Keys[0];
    Check(NearQ(runtimePose, SpellGizmoHandleLaw.RawToModelQuaternion(pose)),
        "M2Reader disagrees with the quaternion pose through the law's swap");

    // Rotation composition: a bone under a rotated, uniformly scaled parent. Turning its world
    // orientation by 25 degrees about world Z must come back as a LOCAL rotation R_new with
    // R_new * P == world * Rz - the drag turns exactly what the user sees.
    Matrix4x4 parent = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateRotationZ(0.6f) * Matrix4x4.CreateRotationX(0.3f);
    Matrix4x4 local = Matrix4x4.CreateRotationX(0.7f) * Matrix4x4.CreateRotationY(-0.4f);
    Matrix4x4 world = local * parent;
    float theta = 25f * MathF.PI / 180f;
    Vector4 q = SpellGizmoHandleLaw.RotateBoneWorld(world, parent, Vector3.UnitZ, theta);
    Matrix4x4 turned = Matrix4x4.CreateFromQuaternion(new Quaternion(q.X, q.Y, q.Z, q.W)) * parent;
    Check(NearM(turned, world * Matrix4x4.CreateRotationZ(theta)), "RotateBoneWorld does not turn the world frame by the angle");
    Quaternion localQ = Quaternion.CreateFromRotationMatrix(local);
    Check(NearQ(SpellGizmoHandleLaw.LocalRotation(world, parent), new Vector4(localQ.X, localQ.Y, localQ.Z, localQ.W)),
        "LocalRotation does not recover the local rotation under a scaled parent");

    // Drag arithmetic: a ray aimed at a known point on the axis / plane / ring reads it back.
    var anchor = new Vector3(3f, -2f, 1f);
    Vector3 axis = Vector3.Normalize(new Vector3(1f, 2f, 0.5f));
    var eye = new Vector3(-5f, 4f, 9f);
    Vector3 onAxis = anchor + axis * 1.7f;
    Check(MathF.Abs(SpellGizmoHandleLaw.AxisParam(eye, Vector3.Normalize(onAxis - eye), anchor, axis) - 1.7f) < 1e-3f, "AxisParam");
    Vector3 reference = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ));
    Vector3 onPlane = anchor + reference * 0.8f;
    Check(SpellGizmoHandleLaw.PlanePoint(eye, Vector3.Normalize(onPlane - eye), anchor, axis) is { } hit && NearV(hit, onPlane), "PlanePoint");
    Vector3 side = Vector3.Cross(axis, reference);
    float a40 = 40f * MathF.PI / 180f;
    Vector3 onRing = anchor + (reference * MathF.Cos(a40) + side * MathF.Sin(a40)) * 0.5f;
    Check(SpellGizmoHandleLaw.RingAngle(eye, Vector3.Normalize(onRing - eye), anchor, axis, reference) is { } angle &&
          MathF.Abs(angle - a40) < 1e-3f, "RingAngle");
    Matrix4x4 frame = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationZ(0.4f) * Matrix4x4.CreateTranslation(5f, 6f, 7f);
    var rawDelta = new Vector3(0.25f, -0.5f, 1f);
    Vector3 worldDelta = Vector3.TransformNormal(SpellGizmoHandleLaw.Swap(rawDelta), frame);
    Check(NearV(SpellGizmoHandleLaw.WorldDeltaToRaw(worldDelta, frame), rawDelta), "WorldDeltaToRaw");
    Check(MathF.Abs(SpellGizmoHandleLaw.Snap(1.37f, 0.25f) - 1.25f) < 1e-6f && MathF.Abs(SpellGizmoHandleLaw.Snap(1.38f, 0.25f) - 1.5f) < 1e-6f, "Snap");
    Console.WriteLine($"[emitter-lab] handle law: swaps agree with M2Reader on {bones.Count} bones; pose, rotation composition and drag arithmetic hold");
}

// ── emitter inline block: colours, cells, head/tail, midpoint, tail time, bone ──
if (patchFixture is { } inlineFx)
{
    List<EmitterSnapshot> before = M2EmitterParser.ReadEmitters(inlineFx.Bytes);
    EmitterSnapshot source = before[0];
    byte[] patched = (byte[])inlineFx.Bytes.Clone();
    int patchedCount = M2EmitterParser.ApplyEmitterPatch(patched, new EmitterPatch
    {
        EmitterIndex = 0, ColorStart = 0xFF102030, ColorMid = 0x80405060, ColorEnd = 0x00708090,
        MidPoint = 0.75f, HeadOrTail = 2, TextureRows = 4, TextureCols = 4, TailTime = 0.5f,
        Bone = source.Bone, InheritScale = 0.5f, EmissionRate = 33f, FlattenTracks = true,
    });
    Check(patchedCount >= 10, $"emitter inline patch applied only {patchedCount} fields");
    EmitterSnapshot after = M2EmitterParser.ReadEmitters(patched)[0];
    Check(after.ColorStart == 0xFF102030 && after.ColorMid == 0x80405060 && after.ColorEnd == 0x00708090,
        "emitter colours did not round-trip");
    Check(Near(after.MidPoint, 0.75f) && after.HeadOrTail == 2 && after.TextureRows == 4 && after.TextureCols == 4 &&
          Near(after.TailTime, 0.5f) && Near(after.InheritScale, 0.5f), "emitter inline fields did not round-trip");
    M2Model? patchedModel = M2Reader.Parse(patched);
    M2ParticleEmitter runtime = patchedModel!.ParticleEmitters[0];
    Check(runtime.ColorKeys[0] == 0xFF102030 && runtime.ColorKeys[2] == 0x00708090 && Near(runtime.MidPoint, 0.75f) &&
          runtime.HeadOrTail == 2 && runtime.TextureRows == 4 && runtime.TextureCols == 4 && Near(runtime.TailTime, 0.5f),
        "M2Reader disagrees with the emitter inline patch");
    Check(runtime.ScalarTracks[6].Keys.All(k => Near(k, 33f)), "flattened emission-rate track has a stray key");
    Console.WriteLine($"[emitter-lab] emitter inline fixture: {inlineFx.Path} e0 (colours, cells, head/tail, midpoint, tail, flatten) round-tripped");
}

// ── SKETCH WRITER (shared_docs/SPELL_SKETCH.md §4.6, slice S1) ──────────────────
// Bytes WE authored, read back by the runtime reader AND the creator parsers, with
// the coordinate swap checked in both directions and the IDE's own byte patchers
// still working on the result. Nothing in the Sketch window is worth building until
// this passes, so it is deliberately unforgiving.
//
// This fixture needs no archive: SketchWriter is pure input → bytes.
{
    // The §1 script: a crescent at the chest, amber, standing vertical, flying 3 yd
    // forward over 0.6 s and fading out — then Ctrl+V for the second slash.
    var sketch = new SketchDoc { Stage = "cast" };
    SketchPiece slash = sketch.AddPiece(SketchShapeKind.Crescent);
    slash.Place.Facing = SketchFacing.VerticalForward;
    slash.Look.Colour = new Vector3(1f, 0.65f, 0.2f);
    slash.Look.Blend = SketchBlend.Additive;
    slash.Motion.Distance = 3f;
    slash.Motion.Direction = SketchDirection.Forward;
    slash.Motion.Duration = 0.6f;
    slash.Motion.Ease = SketchEase.Out;
    SketchPiece pasted = sketch.PasteCopy(slash);

    Check(sketch.Pieces.Count == 2, "paste did not add a second piece");
    Check(Near(pasted.Place.Origin.Y, slash.Place.Origin.Y + 0.5f),
        "paste offset is not 0.5 yd left");
    Check(Near(sketch.Length, 0.6f), $"sketch length should follow the longest motion, got {sketch.Length}");

    var blpWriter = new BlpWriterService();
    SketchWriter.SketchBuild? built = SketchWriter.Build(sketch, 4242, "Test Slash", blpWriter);
    Check(built is not null, "SketchWriter refused a two-piece sketch");
    byte[] sketchBytes = built!.Model;

    // ── the runtime reader ─────────────────────────────────────────────────────
    M2Model? sk = M2Reader.Parse(sketchBytes);
    Check(sk is not null, "M2Reader rejected the authored sketch M2");
    Check(sk!.Version == 256, $"sketch version is {sk.Version}, not 256");
    Check(sk.IsValid, "authored sketch has no renderable geometry (IsValid false)");
    Check(sk.HasRenderableContent, "authored sketch fails HasRenderableContent");
    Check(sketchBytes.Length >= 0x144, "authored sketch is shorter than the 0x144 header");

    Check(sk.Bones.Count == 3, $"sketch bones: expected root + 2 pieces, got {sk.Bones.Count}");
    Check(sk.Vertices.Count == 8, $"sketch vertices: expected 4 per quad, got {sk.Vertices.Count}");
    Check(sk.Indices.Count == 12, $"sketch indices: expected 6 per quad, got {sk.Indices.Count}");
    Check(sk.Batches.Count == 2, $"sketch batches: expected one per quad, got {sk.Batches.Count}");
    Check(sk.Submeshes.Count == 2, $"sketch submeshes: expected one per quad, got {sk.Submeshes.Count}");
    Check(sk.Textures.Count == 2, $"sketch textures: expected one per piece, got {sk.Textures.Count}");
    Check(sk.Colors.Count == 2, $"sketch colour records: expected one per quad, got {sk.Colors.Count}");
    Check(sk.RenderFlags.Count == 2, $"sketch render flags: expected one per quad, got {sk.RenderFlags.Count}");
    Check(sk.Sequences.Count == 1, $"sketch sequences: expected exactly one, got {sk.Sequences.Count}");
    Check(sk.ParticleEmitters.Count == 0 && sk.RibbonEmitters.Count == 0,
        "an S1 sketch should carry no emitters or ribbons yet");

    // The sequence duration IS the effect's life (SpellAttachment.SelfTerminatingSpan).
    Check(sk.Sequences[0].StartTimestamp == 0 && sk.Sequences[0].DurationMs == 600,
        $"sketch sequence window is {sk.Sequences[0].StartTimestamp}..{sk.Sequences[0].EndTimestamp}, expected 0..600");
    Check(!sk.Sequences[0].IsLooping, "a sketch sequence must not loop (flags bit 0 set)");
    Check(Near((float)MSUIClient.World.Units.SpellAttachment.SelfTerminatingSpan(sk), 0.6f),
        "SelfTerminatingSpan disagrees with the sketch length");

    // ── the four creator parsers agree with the runtime reader ─────────────────
    List<MeshSnapshot> skLayers = M2MeshParser.ReadMeshes(sketchBytes);
    Check(skLayers.Count == sk.Batches.Count,
        $"sketch mesh layer count mismatch creator={skLayers.Count} runtime={sk.Batches.Count}");
    List<BoneSnapshot> skBones = M2BoneParser.ReadBones(sketchBytes);
    Check(skBones.Count == sk.Bones.Count,
        $"sketch bone count mismatch creator={skBones.Count} runtime={sk.Bones.Count}");
    List<RibbonSnapshot> skRibbons = M2RibbonParser.ReadRibbons(sketchBytes);
    Check(skRibbons.Count == sk.RibbonEmitters.Count, "sketch ribbon count mismatch");
    List<EmitterSnapshot> skEmitters = M2EmitterParser.ReadEmitters(sketchBytes);
    Check(skEmitters.Count == sk.ParticleEmitters.Count, "sketch emitter count mismatch");

    // ── THE SWAP, both directions ──────────────────────────────────────────────
    // We authored RAW (x forward, y left, z up). The creator parser reads raw; the
    // runtime reader swaps (x, y, z) -> (x, z, -y). Both must see what we meant.
    for (int i = 0; i < sk.Bones.Count; i++)
    {
        Check(Near(skBones[i].PivotX, sk.Bones[i].Pivot.X) &&
              Near(skBones[i].PivotZ, sk.Bones[i].Pivot.Y) &&
              Near(-skBones[i].PivotY, sk.Bones[i].Pivot.Z),
            $"sketch bone pivot swap mismatch b{i}");
    }
    // Piece 1 sits at the chest: raw (0, 0, 1.2).
    Check(Near(skBones[1].PivotX, 0f) && Near(skBones[1].PivotY, 0f) && Near(skBones[1].PivotZ, 1.2f),
        $"piece 1 pivot is not the authored chest origin: raw {F(skBones[1].PivotX)},{F(skBones[1].PivotY)},{F(skBones[1].PivotZ)}");
    // …and the runtime therefore sees it 1.2 up its own Y.
    Check(NearV(sk.Bones[1].Pivot, new Vector3(0f, 1.2f, 0f)),
        $"piece 1 pivot did not swap to model space: {V(sk.Bones[1].Pivot)}");
    // Ctrl+V put the second piece 0.5 yd LEFT, which is raw +y.
    Check(Near(skBones[2].PivotY, 0.5f), "pasted piece is not 0.5 yd left in the raw frame");

    // ── geometry: a 1 x 1 yd crescent is literally +/-0.5 file units ────────────
    // 1.0 file unit == 1.0 world yard: there is no scale factor in the spell path.
    float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
    for (int v = 0; v < 4; v++)
    {
        M2Vertex vert = sk.Vertices[v];
        minX = MathF.Min(minX, vert.PosX); maxX = MathF.Max(maxX, vert.PosX);
        minY = MathF.Min(minY, vert.PosY); maxY = MathF.Max(maxY, vert.PosY);
        Check(vert.BoneWeight0 == 255 && vert.BoneIndex0 == 1,
            $"sketch vertex {v} is not fully weighted to its own bone");
    }
    // The QUAD is deliberately larger than the shape: the drawing occupies the middle
    // 2R of its texture and the rest is margin for the soft edge and the glow, so the
    // quad is scaled by QuadOversize to keep the SHAPE the size the owner typed.
    float half = 0.5f * SketchTextures.QuadOversize;
    Check(Near(minX, -half) && Near(maxX, half),
        $"quad width is not the oversized 1 yd shape: {F(minX)}..{F(maxX)}");
    Check(Near(minY, 1.2f - half) && Near(maxY, 1.2f + half),
        $"quad height/placement is wrong: {F(minY)}..{F(maxY)}");
    // What the owner actually SEES is the drawn shape, and that must be 1 yd exactly.
    float drawnWidth = (maxX - minX) * 2f * SketchTextures.ShapeRadius;
    Check(Near(drawnWidth, 1f),
        $"the DRAWN crescent measures {F(drawnWidth)} yd, not the 1 yd it was given");

    // ── motion: ease-out travel is four keys ending 3 yd forward ────────────────
    M2Bone travelBone = sk.Bones[1];
    Check(travelBone.Translation.Keys.Count == 4,
        $"ease-out travel should be 4 keys, got {travelBone.Translation.Keys.Count}");
    Check(travelBone.Translation.Timestamps.Count == travelBone.Translation.Keys.Count,
        "translation timestamps and keys differ in count (M2Reader wipes such a track)");
    Check(travelBone.Translation.Ranges.Count == 1 &&
          travelBone.Translation.Ranges[0].Start == 0 &&
          travelBone.Translation.Ranges[0].End == 3,
        "translation range is not the inclusive [0,3] of a 4-key track");
    Check(travelBone.Translation.Timestamps[0] == 0 && travelBone.Translation.Timestamps[3] == 600,
        "translation keys do not span the sequence window");
    // Raw travel is +x (forward) by 3; the runtime keeps x, so the last key is (3,0,0).
    Check(NearV(travelBone.Translation.Keys[3], new Vector3(3f, 0f, 0f)),
        $"travel does not end 3 yd forward: {V(travelBone.Translation.Keys[3])}");
    Check(NearV(travelBone.Translation.Keys[0], Vector3.Zero),
        "travel does not start at the piece's own pivot");
    Check(skBones[1].TranslationKeys == 4 && skBones[1].TranslationFirst is { } firstRaw &&
          NearV(firstRaw, Vector3.Zero),
        "the creator bone parser disagrees about the travel track");

    // Rotation: vertical-forward is identity, and it must be a UNIT quaternion —
    // a non-unit quat is exactly how a mis-parse shows up.
    Check(travelBone.Rotation.Keys.Count == 1, "a non-spinning piece should hold one rotation key");
    Vector4 q = travelBone.Rotation.Keys[0];
    Check(Near(MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W), 1f),
        "sketch rotation key is not a unit quaternion");
    Check(skBones[1].RotationFirst is { } rawQ && NearQ(rawQ, new Vector4(0f, 0f, 0f, 1f)),
        "vertical-forward facing should be the identity rotation in the raw frame");

    // ── look: colour rides the record, alpha rides the fade ────────────────────
    // Colour is NOT swapped: it is RGB, not a position.
    Check(skLayers[0].ColorFirst is { } tint && NearV(tint, new Vector3(1f, 0.65f, 0.2f)),
        $"amber did not survive into the colour record: {(skLayers[0].ColorFirst is { } t2 ? V(t2) : "none")}");
    Check(sk.RenderFlags[0].BlendingMode == 4, "additive should be blend mode 4");
    Check(sk.RenderFlags[0].Unlit && sk.RenderFlags[0].TwoSided && sk.RenderFlags[0].NoZWrite,
        "a blended sketch plane should be unlit, two-sided and not Z-writing");
    // Default fade-out: full, hold, zero — three keys, and the LAST one is zero.
    Check(skLayers[0].AlphaKeys == 3, $"default fade should be 3 alpha keys, got {skLayers[0].AlphaKeys}");
    Check(skLayers[0].AlphaFirst is { } a0 && Near(a0, 1f), "the fade should start opaque");
    Check(sk.Colors[0].Alpha.Keys.Count == 3 && sk.Colors[0].Alpha.Keys[2] == 0,
        "the fade does not end at zero alpha");
    // An ANIMATED alpha must never be mistaken for a constant-zero batch and culled.
    Check(!sk.IsBatchConstantInvisible(sk.Batches[0]),
        "a faded sketch batch is being treated as constant-invisible and would never draw");

    // ── the texture actually resolves through the lookup chain ─────────────────
    Check(built.Textures.Count == 2, "expected one BLP per piece");
    Check(sk.TextureLookup.Count == 2 && sk.TextureLookup[sk.Batches[0].TextureIndex] == 0,
        "batch 0 does not resolve to texture 0 through the lookup");
    string wantTexture = built.Textures[0].Path;
    Check(string.Equals(sk.Textures[0].Filename, wantTexture, StringComparison.OrdinalIgnoreCase),
        $"texture path mismatch: file has '{sk.Textures[0].Filename}', build says '{wantTexture}'");
    Check(wantTexture.StartsWith(@"Spells\Custom\4242_Test_Slash\", StringComparison.OrdinalIgnoreCase) &&
          wantTexture.EndsWith(".blp", StringComparison.OrdinalIgnoreCase),
        $"texture path does not follow the §4.1 path law: {wantTexture}");
    Check(string.Equals(built.ModelPath, @"Spells\Custom\4242_Test_Slash\sketch_cast.m2",
        StringComparison.OrdinalIgnoreCase), $"model path does not follow the §4.1 path law: {built.ModelPath}");
    Check(Near(sk.GetStaticAlphaForBatch(sk.Batches[0]), 1f),
        "the shared transparency record should read as fully opaque");

    // ── the BLP re-reads, uncompressed, white, with a soft alpha edge ──────────
    byte[] blp = built.Textures[0].Blp;
    Check(blp.Length > 0 && blp[0] == 'B' && blp[1] == 'L' && blp[2] == 'P' && blp[3] == '2',
        "the sketch texture is not a BLP2");
    Check(blp[8] == 1, $"sketch BLPs must be UNCOMPRESSED (compression=1), got {blp[8]} — a DXT block turns every soft edge hard");
    Check(blp[9] == 8, $"sketch BLPs need an 8-bit alpha plane, got alphaDepth={blp[9]}");
    byte[] pixels = BlpDecoder.GetPixels(blp, 0, out int blpW, out int blpH);
    Check(blpW == 128 && blpH == 128, $"a 1 yd piece should be 128 px at 128 px/yd, got {blpW}x{blpH}");
    Check(pixels.Length == blpW * blpH * 4, "decoded BLP pixel buffer is the wrong size");
    int opaque = 0, partial = 0;
    bool allWhite = true;
    for (int i = 0; i < pixels.Length; i += 4)
    {
        byte alpha = pixels[i + 3];
        if (alpha > 250) opaque++;
        else if (alpha > 4) partial++;
        if (alpha > 250 && (pixels[i] < 250 || pixels[i + 1] < 250 || pixels[i + 2] < 250)) allWhite = false;
    }
    Check(opaque > 0, "the crescent texture has no solid interior — the SDF drew nothing");
    Check(partial > 0, "the crescent texture has no soft edge — softness/glow was lost");
    Check(allWhite, "the sketch texture is not white; colour must come from the colour record, not the image");

    // ── our bytes are ordinary M2 bytes: the IDE's own patchers still work ─────
    byte[] relit = M2MeshParser.ApplyMeshPatch((byte[])sketchBytes.Clone(),
        new MeshPatch { BatchIndex = 0, Blend = 2, Color = new Vector3(0.1f, 0.2f, 0.3f) });
    Check(M2MeshParser.ReadMeshes(relit)[0].Blend == 2, "a sketch mesh layer is not patchable");
    Check(M2MeshParser.ReadMeshes(relit)[0].ColorFirst is { } c2 && NearV(c2, new Vector3(0.1f, 0.2f, 0.3f)),
        "a sketch colour record is not patchable");
    M2Model? relitModel = M2Reader.Parse(relit);
    // ApplyMeshPatch gives the batch a PRIVATE material rather than editing a shared
    // one, so read the blend back through the batch, not out of slot 0.
    Check(relitModel is not null &&
          relitModel.RenderFlags[relitModel.Batches[0].MaterialIndex].BlendingMode == 2,
        "M2Reader disagrees with the patched sketch layer");
    Check(relitModel!.Bones.Count == sk.Bones.Count && relitModel.Vertices.Count == sk.Vertices.Count,
        "patching a sketch layer disturbed unrelated tables");

    byte[] turned = (byte[])sketchBytes.Clone();
    Check(M2BoneParser.ApplyBonePatch(turned, new BonePatch
    {
        BoneIndex = 1, RotationEulerDegrees = new Vector3(0f, 0f, 90f),
    }), "a sketch bone is not patchable");
    Vector4 wanted = M2BoneParser.EulerToQuaternion(new Vector3(0f, 0f, 90f));
    Check(M2BoneParser.ReadBones(turned)[1].RotationFirst is { } turnedQ && NearQ(turnedQ, wanted),
        "the rotate rings would not turn a sketch piece");
    Check(M2Reader.Parse(turned) is not null, "M2Reader rejected the rotated sketch M2");

    // ── facings, glow and spin: the other shapes of a piece ────────────────────
    var facings = new SketchDoc { Stage = "impact" };
    SketchPiece ring = facings.AddPiece(SketchShapeKind.Ring);          // §9: rings lie flat
    SketchPiece star = facings.AddPiece(SketchShapeKind.Star);          // §9: stars face you
    SketchPiece side = facings.AddPiece(SketchShapeKind.Crescent);
    side.Place.Facing = SketchFacing.VerticalSide;
    side.Motion.SpinDegreesPerSecond = 360f;
    star.Extras.Glow.Radius = 0.4f;
    // Stand them apart and colour them apart, or the preview is one bright smudge.
    // Raw +y is LEFT, so the ring goes left of the caster and the crescent right.
    ring.Place.Origin = new Vector3(0f, 1.05f, 0.15f);
    ring.Look.Colour = new Vector3(0.35f, 0.85f, 1f);
    star.Place.Origin = new Vector3(0f, 0f, 1.35f);
    side.Place.Origin = new Vector3(0f, -1.05f, 1.2f);
    side.Look.Colour = new Vector3(1f, 0.4f, 0.85f);
    Check(ring.Place.Facing == SketchFacing.Flat, "a ring should default to lying flat");
    // Everything else starts TURNED TO FACE YOU, because a piece you cannot see is a piece you
    // cannot work on - see SketchPiece.DefaultFacing for what driving the real client showed.
    Check(star.Place.Facing == SketchFacing.VerticalSide,
        "a new piece should start facing the viewer");
    star.Place.Facing = SketchFacing.FaceCamera;      // this fixture is about the billboard

    SketchWriter.SketchBuild? facingBuild = SketchWriter.Build(facings, 4242, "Test Slash", blpWriter);
    Check(facingBuild is not null, "SketchWriter refused the facings sketch");
    M2Model? fm = M2Reader.Parse(facingBuild!.Model);
    Check(fm is not null, "M2Reader rejected the facings sketch M2");
    // Three pieces, but the star's glow adds a FOURTH quad on the star's own bone.
    Check(fm!.Bones.Count == 4, $"facings sketch should have root + 3 pieces, got {fm.Bones.Count}");
    Check(fm.Batches.Count == 4, $"a glow should add a fourth quad, got {fm.Batches.Count} batches");
    Check(fm.Vertices.Count == 16, "the glow quad should carry its own four vertices");
    Check(fm.Textures.Count == 3, "a glow reuses its piece's texture rather than making a new one");

    // Face-camera is the bone's spherical billboard bit, inside the 0x78 mask.
    Check((fm.Bones[2].Flags & 0x08u) != 0, "a face-camera piece must set the spherical billboard bone flag");
    Check((fm.Bones[2].Flags & 0x04u) == 0,
        "ignore-parent-rotation (0x04) beats the billboard bits in the client's chain — never set both");
    Check((fm.Bones[1].Flags & 0x78u) == 0, "a flat ring should not be billboarded");

    // Flat = roll +90 about raw X; vertical-side = yaw -90 about raw Z.
    List<BoneSnapshot> fBones = M2BoneParser.ReadBones(facingBuild.Model);
    Check(fBones[1].RotationFirst is { } flatQ &&
          NearQ(flatQ, M2BoneParser.EulerToQuaternion(new Vector3(90f, 0f, 0f))),
        "flat facing is not a +90 roll about the raw X axis");
    Check(fBones[3].RotationFirst is { } sideQ0 &&
          NearQ(sideQ0, M2BoneParser.EulerToQuaternion(new Vector3(0f, 0f, -90f))),
        "vertical-side facing is not a -90 yaw about the raw Z axis");

    // Spin: 360 deg/s over a 0.5 s sketch is 180 deg, so 45-deg keys give 5 of them.
    M2Bone spinBone = fm.Bones[3];
    Check(spinBone.Rotation.Keys.Count == 5,
        $"a half-turn spin should be 5 keys at 45 deg each, got {spinBone.Rotation.Keys.Count}");
    Check(spinBone.Rotation.Timestamps.Count == spinBone.Rotation.Keys.Count,
        "spin timestamps and keys differ in count");
    foreach (Vector4 key in spinBone.Rotation.Keys)
        Check(Near(MathF.Sqrt(key.X * key.X + key.Y * key.Y + key.Z * key.Z + key.W * key.W), 1f),
            "a spin key is not a unit quaternion");
    Check(spinBone.Rotation.Timestamps[^1] == 500, "spin keys do not reach the end of the sequence");

    // Every creator parser still agrees on the richer file.
    Check(M2MeshParser.ReadMeshes(facingBuild.Model).Count == fm.Batches.Count,
        "facings sketch mesh layer count mismatch");
    Check(M2BoneParser.ReadBones(facingBuild.Model).Count == fm.Bones.Count,
        "facings sketch bone count mismatch");

    // ── THE SECOND READER: the Completer parses what it stores ────────────────
    // A sketch that only this client can load is a sketch that dies at export.
#if WEB_M2READER
    Check(WebM2.M2Reader.Parse(sketchBytes) is { } webModel &&
          webModel.Vertices.Count == sk.Vertices.Count &&
          webModel.Indices.Count == sk.Indices.Count &&
          webModel.Batches.Count == sk.Batches.Count &&
          webModel.Bones.Count == sk.Bones.Count &&
          webModel.Textures.Count == sk.Textures.Count,
        "the MangosSuperUI M2Reader disagrees with this client about the sketch M2");
    Check(WebM2.M2Reader.Parse(facingBuild.Model) is not null,
        "the MangosSuperUI M2Reader rejected the facings/glow/spin sketch M2");
    Check(WebM2.M2Reader.Parse(relit) is not null && WebM2.M2Reader.Parse(turned) is not null,
        "the MangosSuperUI M2Reader rejected a patched sketch M2");
#else
    Console.WriteLine("[emitter-lab] NOTE: MangosSuperUI not checked out beside this repo - " +
                      "the sketch M2 was NOT validated against the web reader.");
#endif

    // ── the live-editing budget (§4.7: "under 5 ms for a handful of pieces") ──
    // Every card change recompiles, so this number is the difference between a dial
    // that feels connected to the picture and one that lags behind the mouse.
    {
        SketchWriter.Build(sketch, 4242, "Test Slash", blpWriter);   // warm the JIT
        const int Runs = 20;

        // COLD: the shape itself changed, so the SDF and the BLP have to be redone.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < Runs; i++) SketchWriter.Build(sketch, 4242, "Test Slash", blpWriter);
        double cold = clock.Elapsed.TotalMilliseconds / Runs;

        // WARM: a colour / motion / placement dial moved, which cannot change a pixel.
        // This is what dragging a slider actually costs.
        var cache = new SketchWriter.SketchTextureCache();
        SketchWriter.Build(sketch, 4242, "Test Slash", blpWriter, cache);
        clock.Restart();
        for (int i = 0; i < Runs; i++) SketchWriter.Build(sketch, 4242, "Test Slash", blpWriter, cache);
        double warm = clock.Elapsed.TotalMilliseconds / Runs;

        // ONE miss, not two: Ctrl+V copies the shape as well as the cards, so the pasted
        // crescent is pixel-identical to its source and the two pieces share a single
        // raster. Keying on the shape rather than on the piece is what buys that.
        Check(cache.Misses == 1 && cache.Hits == 2 * (Runs + 1) - 1,
            $"the texture cache should rasterize each distinct SHAPE once: " +
            $"hits={cache.Hits} misses={cache.Misses}");
        // Cached builds must be a small fraction of a frame, and must beat the cold path
        // by a wide margin or the cache is not doing its job.
        Check(warm < cold, $"the cached recompile ({warm:0.00} ms) is not faster than the " +
                           $"cold one ({cold:0.00} ms)");
        // Generous bounds on purpose: these are regression tripwires, not benchmarks, and
        // must not go red because the machine was busy.
        Check(cold < 100d, $"a cold two-piece sketch recompile took {cold:0.0} ms");
        Check(warm < 20d, $"a cached two-piece sketch recompile took {warm:0.0} ms - the " +
                          "live-editing path would visibly lag");
        Console.WriteLine($"[emitter-lab] sketch recompile: {cold:0.00} ms cold (shape changed) / " +
                          $"{warm:0.00} ms cached (colour, motion, placement) for 2 pieces; " +
                          "§4.7 budget 5 ms");
    }

    // ── a doc with no pieces is refused rather than shipped as junk ────────────
    Check(SketchWriter.Build(new SketchDoc { Stage = "cast" }, 1, "x", blpWriter) is null,
        "SketchWriter must refuse an empty sketch instead of emitting an unloadable M2");

    // ── appearance time, quarter turns and mirrors ────────────────────────────
    // "When does this show up" is the first question a phase raises and the one the owner
    // could not find an answer to. A late piece must be INVISIBLE and STILL until it is due,
    // and must not be part-way through its travel when it arrives.
    var timed = new SketchDoc { Stage = "cast" };
    SketchWriter.SketchBuild? timedBuild;
    {
        SketchPiece first = timed.AddPiece(SketchShapeKind.Crescent);
        first.Place.Facing = SketchFacing.VerticalForward;
        first.Look.Colour = new Vector3(1f, 0.65f, 0.2f);
        first.Motion.Distance = 3f;
        first.Motion.Duration = 0.5f;

        SketchPiece second = timed.PasteCopy(first);
        second.Motion.StartAt = 0.25f;                  // the second slash follows the first
        second.Look.Colour = new Vector3(0.5f, 0.8f, 1f);
        second.Place.QuarterTurns = 1;                  // and comes in turned a quarter
        second.Place.MirrorX = true;                    // and mirrored: "(" becomes ")"

        Check(Near(timed.Length, 0.75f),
            $"a piece starting at 0.25 with a 0.50 travel should make the sketch 0.75s, got {timed.Length}");

        timedBuild = SketchWriter.Build(timed, 4242, "Test Slash", blpWriter);
        Check(timedBuild is not null, "the timed sketch did not compile");
        M2Model? tm = M2Reader.Parse(timedBuild!.Model);
        Check(tm is not null, "M2Reader rejected the timed sketch");
        uint window = tm!.Sequences[0].EndTimestamp;
        Check(window == 750, $"the timed sketch sequence should be 750 ms, got {window}");

        // The late piece is bone 2 / batch 1.
        M2AnimTrack<short> lateAlpha = tm.Colors[1].Alpha;
        Check(lateAlpha.Keys.Count >= 3, "the late piece has no appearance schedule");
        Check(lateAlpha.Keys[0] == 0 && lateAlpha.Timestamps[0] == 0,
            "the late piece is visible at t=0 - it should not exist yet");
        // Invisible right up to its cue, and visible right after it.
        Check(M2TrackSampling.Fixed16(lateAlpha, tm, 0, 0.20f) <= 0.001f,
            "the late piece is already showing at 0.20s, before its 0.25s cue");
        Check(M2TrackSampling.Fixed16(lateAlpha, tm, 0, 0.26f) > 0.9f,
            "the late piece has not appeared at 0.26s, just after its 0.25s cue");
        // And STILL: its travel must not have started early.
        M2AnimTrack<Vector3> lateMove = tm.Bones[2].Translation;
        Check(NearV(M2TrackSampling.Vector(lateMove, tm, 0, 0.20f, Vector3.Zero), Vector3.Zero),
            "the late piece has already moved before it appeared");
        Check(NearV(M2TrackSampling.Vector(lateMove, tm, 0, 0.75f, Vector3.Zero), new Vector3(3f, 0f, 0f)),
            "the late piece does not finish its 3 yd travel by the end of the sequence");
        // The early piece is unaffected and still starts opaque.
        Check(tm.Colors[0].Alpha.Keys[0] == 32767, "the on-time piece should start visible");

        // A quarter turn is a real rotation about the piece's own normal...
        Vector4 quarterQ = M2BoneParser.ReadBones(timedBuild.Model)[2].RotationFirst
            ?? throw new InvalidOperationException("the turned piece has no rotation key");
        Vector4 wantTurn = M2BoneParser.EulerToQuaternion(new Vector3(0f, 90f, 0f));
        Check(NearQ(quarterQ, wantTurn),
            "a quarter turn on a vertical-forward piece is not a 90 degree pitch about raw Y");
        // ...and a mirror is NOT a rotation: it swaps the U coordinate and leaves the bone alone.
        Check(tm.Vertices[4].TexU > tm.Vertices[5].TexU,
            "MirrorX did not swap the piece's U coordinates");
        Check(Near(tm.Vertices[4].TexV, 0f) && Near(tm.Vertices[6].TexV, 1f),
            "MirrorX should not have touched V");
#if WEB_M2READER
        Check(WebM2.M2Reader.Parse(timedBuild.Model) is not null,
            "the MangosSuperUI M2Reader rejected the timed sketch");
#endif
    }

    // ── a HAND-DRAWN shape becomes an ordinary piece ──────────────────────────
    // The shape shelf is seven shapes somebody else chose; this is the other half of the
    // owner's ask - draw a thing and have it turn into an effect. A drawing is strokes in
    // 0..1 canvas space, rasterized with a round pen, and from there it is a piece like any
    // other: same cards, same compiler, same M2.
    var drawnDoc = new SketchDoc { Stage = "cast" };
    SketchWriter.SketchBuild? drawnBuild;
    {
        SketchPiece sketchedByHand = drawnDoc.AddPiece(SketchShapeKind.Stroke);
        Check(sketchedByHand.Place.Facing == SketchFacing.VerticalSide,
            "a hand drawing should start facing the viewer");

        // Two strokes, because lifting the pen is most of what drawing is: a swept arc and a
        // separate tick across it. One unbroken polyline could not make this mark.
        var arc = new List<Vector2>();
        for (int i = 0; i <= 24; i++)
        {
            float a = MathF.PI * (0.15f + 0.7f * i / 24f);
            arc.Add(new Vector2(0.5f + 0.38f * MathF.Cos(a), 0.5f - 0.38f * MathF.Sin(a)));
        }
        var tick = new List<Vector2>
        {
            new(0.34f, 0.70f), new(0.50f, 0.44f), new(0.66f, 0.70f),
        };
        sketchedByHand.Shape.Strokes.Add(arc);
        sketchedByHand.Shape.Strokes.Add(tick);
        sketchedByHand.Shape.StrokeWidth = 26f;
        sketchedByHand.Look.Colour = new Vector3(0.65f, 0.95f, 1f);
        sketchedByHand.Motion.Distance = 2f;
        sketchedByHand.Motion.Duration = 0.5f;

        Check(sketchedByHand.Shape.StrokePointCount == arc.Count + tick.Count,
            "the drawing lost points on its way into the piece");

        // The rasterizer must actually put ink down - and must draw BOTH strokes, which is
        // the whole reason a drawing is a list of lists.
        using (SKBitmap inked = SketchTextures.BuildBitmap(sketchedByHand))
        {
            int lit = 0;
            for (int y = 0; y < inked.Height; y++)
            for (int x = 0; x < inked.Width; x++)
                if (inked.GetPixel(x, y).Alpha > 8) lit++;
            Check(lit > 400, $"the drawing rasterized to almost nothing ({lit} px)");

            // The tick's apex sits at (0.50, 0.44); the arc never passes through it. If only
            // the first stroke were drawn, this pixel would be empty.
            int tx = (int)(0.50f * inked.Width), ty = (int)(0.45f * inked.Height);
            Check(inked.GetPixel(tx, ty).Alpha > 8,
                "only the first stroke was drawn - lifting the pen produced nothing");
        }

        drawnBuild = SketchWriter.Build(drawnDoc, 4242, "Test Slash", blpWriter);
        Check(drawnBuild is not null, "a hand-drawn piece did not compile");
        M2Model? hm = M2Reader.Parse(drawnBuild!.Model);
        Check(hm is not null, "M2Reader rejected the hand-drawn sketch");
        Check(hm!.Batches.Count == 1 && hm.Bones.Count == 2 && hm.Vertices.Count == 4,
            "a drawing should compile to exactly one quad on its own bone, like any other piece");
        Check(hm.Textures.Count == 1 && hm.Textures[0].Filename.EndsWith(".blp",
                StringComparison.OrdinalIgnoreCase),
            "the drawing did not get its own texture");
        // And it moves: a drawing is not a decal, it is a piece.
        Check(NearV(M2TrackSampling.Vector(hm.Bones[1].Translation, hm, 0, 0.5f, Vector3.Zero),
                    new Vector3(2f, 0f, 0f)),
            "a hand-drawn piece does not travel like the others");
#if WEB_M2READER
        Check(WebM2.M2Reader.Parse(drawnBuild.Model) is not null,
            "the MangosSuperUI M2Reader rejected the hand-drawn sketch");
#endif
    }

    // ── a drawn path never outruns the sequence it lives in ───────────────────
    // Timestamps are whole milliseconds inside the sequence window, so a track can hold at
    // most lengthMs + 1 keys. A hand-drawn stroke can carry hundreds of points over half a
    // second, and the first writer "spread them evenly" as 0,1,2,... - i.e. straight out of
    // the window, where BOTH readers drop the tail and the piece freezes part-way along the
    // path the owner drew.
    {
        var drawn = new SketchDoc { Stage = "cast" };
        SketchPiece traced = drawn.AddPiece(SketchShapeKind.Disc);
        const int Points = 700;                       // far more than the 600 ms it has to live in
        for (int i = 0; i < Points; i++)
        {
            float t = i / (float)(Points - 1);
            traced.Motion.Path.Add(new Vector3(t * 4f, MathF.Sin(t * 6f) * 1.5f, 1.2f));
        }
        traced.Motion.Duration = 0.6f;

        SketchWriter.SketchBuild? pathBuild = SketchWriter.Build(drawn, 4242, "Test Slash", blpWriter);
        Check(pathBuild is not null, "a long drawn path should still compile");
        M2Model? dm = M2Reader.Parse(pathBuild!.Model);
        Check(dm is not null, "M2Reader rejected the drawn-path sketch");

        uint window = dm!.Sequences[0].EndTimestamp;
        M2AnimTrack<Vector3> track = dm.Bones[1].Translation;
        Check(track.Keys.Count == track.Timestamps.Count, "drawn-path track counts disagree");
        Check(track.Keys.Count > 1, "the drawn path produced no motion");
        Check(track.Keys.Count <= window + 1,
            $"the drawn path wrote {track.Keys.Count} keys into a {window} ms sequence - more keys " +
            "than there are milliseconds to hold them");
        for (int i = 0; i < track.Timestamps.Count; i++)
            Check(track.Timestamps[i] <= window,
                $"drawn-path key {i} is stamped {track.Timestamps[i]} ms, past the {window} ms " +
                "sequence end - both readers drop everything after it");
        for (int i = 1; i < track.Timestamps.Count; i++)
            Check(track.Timestamps[i] > track.Timestamps[i - 1],
                $"drawn-path timestamps are not strictly increasing at key {i}");
        Check(track.Ranges.Count == 1 && track.Ranges[0].End == (uint)(track.Keys.Count - 1),
            "the drawn path's range is not the inclusive whole track");

        // Thinning must keep the ENDS: the first and last points are the two the owner
        // placed most deliberately, and a stroke that stops short reads as a bug.
        Vector3 wantLast = traced.Motion.Path[^1] - traced.Place.Origin;
        Check(NearV(track.Keys[^1], new Vector3(wantLast.X, wantLast.Z, -wantLast.Y)),
            $"the drawn path does not end where it was drawn: {V(track.Keys[^1])}");
        Vector3 wantFirst = traced.Motion.Path[0] - traced.Place.Origin;
        Check(track.Timestamps[0] == 0 &&
              NearV(track.Keys[0], new Vector3(wantFirst.X, wantFirst.Z, -wantFirst.Y)),
            $"the drawn path does not start where it was drawn: {V(track.Keys[0])}");
#if WEB_M2READER
        Check(WebM2.M2Reader.Parse(pathBuild.Model) is not null,
            "the MangosSuperUI M2Reader rejected the drawn-path sketch");
#endif
    }

    // ── a camera-facing piece must actually FACE the camera ───────────────────
    // The offline preview cannot catch this: it drives bones through M2Animator, and the
    // billboard is applied later, by SpellMeshSkinningLaw, at draw time. So do here what
    // the renderer does - rewrite the skin palette for the billboard and measure the quad's
    // area from the camera. A camera-facing piece authored in the wrong local plane comes
    // out as a line, invisible, with nothing in either reader to object to.
    {
        var billboard = new SketchDoc { Stage = "cast" };
        SketchPiece facer = billboard.AddPiece(SketchShapeKind.Star);      // §9: stars face you
        // Deliberately OBLONG: a square piece looks identical after a quarter turn, so a
        // square fixture cannot tell "faces the camera" from "faces the camera, sideways" -
        // which is exactly the half-fix that got through the first time.
        facer.Shape.Width = 2f;
        facer.Shape.Height = 0.5f;
        SketchPiece edger = billboard.AddPiece(SketchShapeKind.Crescent);  // vertical-forward
        facer.Place.Facing = SketchFacing.FaceCamera;    // the point of this fixture
        Check(edger.Place.Facing == SketchFacing.VerticalSide,
            "a new piece should start facing the viewer");

        SketchWriter.SketchBuild? bb = SketchWriter.Build(billboard, 4242, "Test Slash", blpWriter);
        M2Model? bm = M2Reader.Parse(bb!.Model);
        Check(bm is not null, "M2Reader rejected the billboard fixture");
        Check((bm!.Bones[1].Flags & 0x08u) != 0, "the camera-facing piece lost its billboard flag");

        var palette = new Matrix4x4[bm.Bones.Count];
        for (int i = 0; i < palette.Length; i++) palette[i] = Matrix4x4.Identity;
        // Look at the caster from the front-right, the ordinary viewing angle.
        var cameraWorld = new Vector3(6f, -4f, 2f);
        Vector3 cameraForward = Vector3.Normalize(new Vector3(0f, 1.2f, 0f) - cameraWorld);
        MSUIClient.World.Units.SpellMeshSkinningLaw.ApplyBillboardBones(bm, Matrix4x4.Identity, cameraWorld,
            cameraForward, bm.Bones.Count, palette);

        // Project each quad onto the camera plane and measure the area it covers.
        Vector3 camRight = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitZ));
        Vector3 camUp = Vector3.Normalize(Vector3.Cross(camRight, cameraForward));
        static (float W, float H) QuadSpan(M2Model m, Matrix4x4[] skin, int batch,
            Vector3 rightAxis, Vector3 upAxis)
        {
            M2Submesh sm = m.Submeshes[m.Batches[batch].SubmeshIndex];
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int v = sm.VertexStart; v < sm.VertexStart + sm.VertexCount; v++)
            {
                M2Vertex vert = m.Vertices[v];
                Vector3 world = Vector3.Transform(
                    new Vector3(vert.PosX, vert.PosY, vert.PosZ), skin[vert.BoneIndex0]);
                float u = Vector3.Dot(world, rightAxis), w = Vector3.Dot(world, upAxis);
                minU = MathF.Min(minU, u); maxU = MathF.Max(maxU, u);
                minV = MathF.Min(minV, w); maxV = MathF.Max(maxV, w);
            }
            return (maxU - minU, maxV - minV);
        }

        // What the piece MEASURES on screen. The quad carries the shape plus its margin, so
        // the drawn shape is the span times 2R - and it must come back as the 2.0 x 0.5 the
        // owner asked for, the right way up.
        (float spanW, float spanH) = QuadSpan(bm, palette, 0, camRight, camUp);
        float drawnW = spanW * 2f * SketchTextures.ShapeRadius;
        float drawnH = spanH * 2f * SketchTextures.ShapeRadius;
        Check(spanW * spanH > 0.3f,
            $"a camera-facing piece covers only {spanW * spanH:0.0000} sq yd from the camera - it " +
            "is edge-on, i.e. invisible. The quad must be authored in the plane the spherical " +
            "billboard basis makes visible (raw Y across, raw Z up), not the raw XZ plane.");
        Check(Near2(drawnW, 2f) && Near2(drawnH, 0.5f),
            $"a camera-facing 2.0 x 0.5 piece measures {F(drawnW)} x {F(drawnH)} on screen - it is " +
            "facing the camera but turned a quarter turn: raw +Y is screen RIGHT (width) and raw " +
            "+Z is screen UP (height), not the other way round.");

        // And the same piece, viewed from a completely different angle, is still face-on.
        var otherCamera = new Vector3(-5f, 5f, 6f);
        Vector3 otherForward = Vector3.Normalize(new Vector3(0f, 1.2f, 0f) - otherCamera);
        for (int i = 0; i < palette.Length; i++) palette[i] = Matrix4x4.Identity;
        MSUIClient.World.Units.SpellMeshSkinningLaw.ApplyBillboardBones(bm, Matrix4x4.Identity, otherCamera,
            otherForward, bm.Bones.Count, palette);
        Vector3 otherRight = Vector3.Normalize(Vector3.Cross(otherForward, Vector3.UnitZ));
        Vector3 otherUp = Vector3.Normalize(Vector3.Cross(otherRight, otherForward));
        (float otherW, float otherH) = QuadSpan(bm, palette, 0, otherRight, otherUp);
        Check(Near2(otherW * 2f * SketchTextures.ShapeRadius, 2f) &&
              Near2(otherH * 2f * SketchTextures.ShapeRadius, 0.5f),
            $"from a second camera the piece measures {F(otherW)} x {F(otherH)} - it does not " +
            "keep facing the camera as the camera moves");

        // A spin on a camera-facing piece is silently discarded by the billboard, so the
        // writer must not author one: a dial that cannot do anything is worse than no dial.
        facer.Motion.SpinDegreesPerSecond = 720f;
        M2Model? spun = M2Reader.Parse(SketchWriter.Build(billboard, 4242, "x", blpWriter)!.Model);
        Check(spun!.Bones[1].Rotation.Keys.Count == 1,
            "a camera-facing piece must not author spin keys - the billboard discards them");
        facer.Motion.SpinDegreesPerSecond = 0f;
    }

    // ── every primitive draws something ───────────────────────────────────────
    // An SDF that comes out empty (or solid) for a legal Thickness is a silent, invisible
    // effect. Rasterize each shape and count its pixels rather than trusting the maths.
    var shelf = new SketchDoc { Stage = "cast" };
    SketchWriter.SketchBuild? shelfBuild2;
    M2Model? shelfModel2;
    {
        SketchShapeKind[] kinds =
        {
            SketchShapeKind.Crescent, SketchShapeKind.Ring, SketchShapeKind.Disc,
            SketchShapeKind.Arrow, SketchShapeKind.Line, SketchShapeKind.Star,
            SketchShapeKind.Rect,
        };
        for (int i = 0; i < kinds.Length; i++)
        {
            SketchPiece shelfPiece = shelf.AddPiece(kinds[i]);
            shelfPiece.Place.Facing = SketchFacing.VerticalForward;   // all face-on to the preview
            shelfPiece.Place.Origin = new Vector3(-3.3f + i * 1.1f, 0f, 1.2f);
            shelfPiece.Look.Colour = i % 2 == 0
                ? new Vector3(1f, 0.85f, 0.55f) : new Vector3(0.6f, 0.85f, 1f);

            // Each shape, across its whole legal Thickness / Softness range.
            foreach (float thickness in new[] { 0.01f, 0.35f, 1f })
            foreach (float softness in new[] { 0f, 0.5f, 1f })
            {
                var probe = SketchPiece.New(kinds[i], "probe");
                probe.Shape.Thickness = thickness;
                probe.Shape.Softness = softness;
                probe.Look.Glow = 0f;                                  // measure the SHAPE, not its halo
                using SKBitmap bmp = SketchTextures.BuildBitmap(probe);
                int lit = 0, solid = 0;
                for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte a = bmp.GetPixel(x, y).Alpha;
                    if (a > 8) lit++;
                    if (a > 247) solid++;
                }
                int total = bmp.Width * bmp.Height;
                Check(lit > total / 200,
                    $"{kinds[i]} at thickness {thickness}, softness {softness} drew almost nothing " +
                    $"({lit}/{total} px) - that ships as an invisible effect");
                Check(solid < total * 99 / 100,
                    $"{kinds[i]} at thickness {thickness}, softness {softness} filled the whole " +
                    $"texture ({solid}/{total} px) - that ships as a solid block");
            }

            // The BORDER must stay dark even at the worst settings WITH a glow on. A halo
            // that runs into the edge of its own texture is not a halo, it is a square -
            // which is exactly what shipped first and what the preview showed around the
            // star. Checking the outer band turns "the margin looks big enough" into a fact.
            foreach (float thickness in new[] { 0.01f, 0.35f, 1f })
            {
                var probe = SketchPiece.New(kinds[i], "probe");
                probe.Shape.Thickness = thickness;
                probe.Shape.Softness = 1f;          // widest edge
                probe.Look.Glow = 1f;               // strongest halo
                using SKBitmap bmp = SketchTextures.BuildBitmap(probe);
                int band = Math.Max(1, bmp.Width / 64), worst = 0;
                for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (x >= band && x < bmp.Width - band && y >= band && y < bmp.Height - band) continue;
                    worst = Math.Max(worst, bmp.GetPixel(x, y).Alpha);
                }
                Check(worst <= 16,
                    $"{kinds[i]} at thickness {thickness} with a full glow reaches alpha {worst} " +
                    "at the texture border - the halo is being cut square by the image edge");
            }
        }
        shelfBuild2 = SketchWriter.Build(shelf, 4242, "Test Slash", blpWriter);
        Check(shelfBuild2 is not null, "the shape shelf did not compile");
        shelfModel2 = M2Reader.Parse(shelfBuild2!.Model);
        Check(shelfModel2 is not null && shelfModel2.Batches.Count == kinds.Length,
            "the shape shelf did not compile to one batch per shape");
        Check(shelfModel2!.Textures.Count == kinds.Length,
            "each shape on the shelf should get its own texture");
    }

    // ── the piece ceiling: a byte-sized bone index must never be allowed to wrap ──
    {
        var crowd = new SketchDoc { Stage = "cast" };
        for (int i = 0; i < SketchWriter.MaxPieces; i++) crowd.AddPiece(SketchShapeKind.Disc);
        var crowdCache = new SketchWriter.SketchTextureCache();
        SketchWriter.SketchBuild? full = SketchWriter.Build(crowd, 1, "x", blpWriter, crowdCache);
        Check(full is not null, $"{SketchWriter.MaxPieces} pieces should still compile");
        M2Model? crowdModel = M2Reader.Parse(full!.Model);
        Check(crowdModel is not null && crowdModel.Bones.Count == SketchWriter.MaxPieces + 1,
            "the crowded sketch lost bones");
        // Every vertex must still name its OWN bone - this is the wrap that would be silent.
        for (int v = 0; v < crowdModel!.Vertices.Count; v++)
            Check(crowdModel.Vertices[v].BoneIndex0 == v / 4 + 1,
                $"vertex {v} rides bone {crowdModel.Vertices[v].BoneIndex0}, not its own");

        crowd.AddPiece(SketchShapeKind.Disc);
        Check(SketchWriter.Build(crowd, 1, "x", blpWriter, crowdCache) is null,
            "SketchWriter must refuse more pieces than a byte-sized bone index can address");
    }

    // ── the `sketches` block (SPELL_CREATOR_PLAN.md P0.2): what is saved must rebuild ──
    // compile(parse(serialize(doc))) == compile(doc), byte for byte, on the fixtures AND on a
    // piece that sets every field off its default - so a field added to the model that the
    // serializer cannot carry fails HERE, not as a spell that reopens subtly different.
    {
        var sink = new SketchDoc { Stage = "impact" };
        SketchPiece all = sink.AddPiece(SketchShapeKind.Stroke);
        all.Shape.Width = 1.7f; all.Shape.Height = 0.6f; all.Shape.Thickness = 0.5f; all.Shape.Softness = 0.1f;
        all.Shape.Strokes.Add(new List<Vector2> { new(0.1f, 0.2f), new(0.5f, 0.9f), new(0.8f, 0.3f) });
        all.Shape.Strokes.Add(new List<Vector2> { new(0.2f, 0.8f), new(0.7f, 0.7f) });
        all.Shape.StrokeWidth = 31f; all.Shape.Closed = true;
        all.Look.Colour = new Vector3(0.2f, 0.4f, 1f); all.Look.Alpha = 0.7f; all.Look.Glow = 0.9f;
        all.Look.Blend = SketchBlend.Mod; all.Look.Unlit = false;
        all.Place.Origin = new Vector3(0.5f, -0.25f, 1.6f); all.Place.Facing = SketchFacing.Custom;
        all.Place.CustomEuler = new Vector3(10f, 20f, 30f); all.Place.QuarterTurns = 3;
        all.Place.MirrorX = true; all.Place.MirrorY = true; all.Place.Offset = new Vector3(0.85f, 0f, 0f);
        all.Place.Scale = 1.3f;
        all.Motion.StartAt = 0.1f; all.Motion.Distance = 2f; all.Motion.Direction = SketchDirection.Custom;
        all.Motion.CustomDirection = new Vector3(1f, 1f, 0.5f); all.Motion.Duration = 0.4f;
        all.Motion.Ease = SketchEase.Linear; all.Motion.SpinDegreesPerSecond = 90f; all.Motion.SwingDegrees = 215f;
        all.Motion.GrowFrom = 0.5f; all.Motion.GrowTo = 1.5f; all.Motion.FadeIn = 0.05f; all.Motion.FadeOut = 0.15f;
        all.Motion.Path.AddRange(new[] { new Vector3(0f, 0f, 1f), new Vector3(1f, 0.5f, 1.2f), new Vector3(2f, 0f, 1f) });
        all.Extras.Glow.Radius = 0.4f; all.Extras.Glow.Colour = new Vector3(1f, 1f, 0f);
        all.Extras.Trail.Preset = SketchTrailPreset.Glow; all.Extras.Trail.Length = 0.5f;
        all.Extras.Sparks.Preset = SketchSparkPreset.Motes; all.Extras.Sparks.Rate = 12f;
        sink.PasteCopy(all);

        foreach ((string label, SketchDoc doc) in new[] { ("fixture A", sketch), ("facings", facings), ("every field", sink) })
        {
            string json = SketchJson.Serialize(doc);
            SketchDoc? back = SketchJson.Deserialize(json);
            Check(back is not null, $"sketch json ({label}) did not parse back");
            Check(SketchJson.Serialize(back!) == json, $"sketch json ({label}) is not stable across a round trip");
            byte[] a = SketchWriter.Build(doc, 4242, "Test Slash", blpWriter)!.Model;
            byte[] b = SketchWriter.Build(back!, 4242, "Test Slash", blpWriter)!.Model;
            Check(a.AsSpan().SequenceEqual(b), $"sketch json ({label}): the reopened sketch compiles to different bytes");
        }
        // A file from a newer build is refused, not misread.
        Check(SketchJson.Deserialize("{\"version\":99,\"stage\":\"cast\",\"pieces\":[]}") is null,
            "sketch json from a newer format must be refused");
        // A file written before a field existed keeps that field's default.
        SketchDoc? sparse = SketchJson.Deserialize("{\"version\":1,\"stage\":\"cast\",\"pieces\":[{\"name\":\"bare\",\"shape\":{\"kind\":\"Ring\"}}]}");
        Check(sparse is { Pieces.Count: 1 } && sparse.Pieces[0].Shape.Kind == SketchShapeKind.Ring &&
              Near(sparse.Pieces[0].Place.Origin.Z, 1.2f) && Near(sparse.Pieces[0].Motion.FadeOut, 0.2f) &&
              SketchWriter.Build(sparse, 1, "x", blpWriter) is not null,
            "a sparse sketch json must keep every absent field's default and still compile");
        Console.WriteLine($"[emitter-lab] sketch json: 3 docs round-trip byte-identical ({SketchJson.Serialize(sink).Length} B for the every-field doc)");
    }

    // ── a MISSILE sketch loops and never fades (SPELL_CREATOR_PLAN.md P2, live 2026-10-26) ──
    // A missile lives for its whole flight; a one-shot sequence faded the ball to nothing after
    // half a second of a six-second flight. Both halves of the law, on the shipped bytes.
    {
        var missileDoc = new SketchDoc { Stage = "cast" };
        SketchPiece ball = missileDoc.AddPiece(SketchShapeKind.Disc);
        ball.Motion.Mode = SketchTravelMode.ToTarget;
        ball.Motion.FadeIn = 0.1f;
        ball.Motion.FadeOut = 0.3f;
        M2Model? flown = M2Reader.Parse(SketchWriter.Build(missileDoc, 4242, "Test Ball", blpWriter)!.Model);
        Check(flown is not null && flown.Sequences.Count == 1 && (flown.Sequences[0].Flags & 1) == 0,
            "a missile sketch must write a LOOPING sequence (flag bit 0 clear) even when not editing");
        var shot = new SketchDoc { Stage = "cast" };
        shot.AddPiece(SketchShapeKind.Disc).Motion.FadeOut = 0.3f;
        M2Model? once = M2Reader.Parse(SketchWriter.Build(shot, 4242, "Test Ball", blpWriter)!.Model);
        Check(once is not null && (once.Sequences[0].Flags & 1) == 1, "an attached sketch still plays once");
        Console.WriteLine("[emitter-lab] sketch missile: loops, attached plays once");
    }

    // ── SOLIDS, IMAGES and UV ANIMATION (SPELL_CREATOR_PLAN.md P3.5 / P3.6) ──
    // Every solid compiles to a mesh both readers accept, with the vertex count its generator
    // promised; an Image piece names the archive image and ships no BLP; a flipbook writes a
    // STEPPED translation track on its own global sequence with one key per cell, a scroll a
    // linear one whose end lands on whole repeats; animated or imaged textures wrap (flags 3).
    {
        var solids = new SketchDoc { Stage = "cast" };
        foreach (SketchShapeKind kind in new[] { SketchShapeKind.Sphere, SketchShapeKind.Cylinder,
                     SketchShapeKind.Cone, SketchShapeKind.Torus, SketchShapeKind.Beam })
        {
            SketchPiece solid = solids.AddPiece(kind);
            solid.Shape.Segments = 12;
            Check(solid.Place.Facing == SketchFacing.VerticalForward, $"a {kind} starts standing on its own axis");
        }
        SketchPiece ball = solids.Pieces[0];
        ball.Look.Image = @"Spells\Lightning2.blp";
        ball.Look.UvScroll = new Vector2(0.5f, 1.25f);
        SketchPiece flip = solids.Pieces[1];
        flip.Look.FlipRows = 4; flip.Look.FlipCols = 4; flip.Look.FlipFps = 16f;
        solids.Pieces[4].Extras.Glow.Radius = 0.2f;   // a beam with a glow shell
        SketchWriter.SketchBuild? sb = SketchWriter.Build(solids, 4242, "Test Solids", blpWriter);
        Check(sb is not null, "the solids sketch compiles");
        M2Model? sm = M2Reader.Parse(sb!.Model);
        Check(sm is not null && sm.HasRenderableContent, "M2Reader accepts the solids and finds them drawable");
        Check(sm!.Batches.Count == 6, $"five solids + one glow shell = 6 batches (got {sm.Batches.Count})");
        Check(sb.Textures.Count == 4 && sb.Textures.All(t => !t.Path.Contains("sketch_cast_sphere", StringComparison.OrdinalIgnoreCase)),
            "the image piece ships no BLP; the four others do");
        Check(sm.Textures.Any(t => t.Filename.Equals(@"Spells\Lightning2.blp", StringComparison.OrdinalIgnoreCase) && (t.Flags & 3) == 3),
            "the image piece's texture names the archive image and wraps");
        Check(sm.TextureTransforms.Count == 2 && sm.GlobalSequenceDurations.Count == 2,
            $"two animated pieces = two texture transforms on two global sequences ({sm.TextureTransforms.Count}/{sm.GlobalSequenceDurations.Count})");
        M2AnimTrack<Vector3> scroll = sm.TextureTransforms[0].Translation, cells = sm.TextureTransforms[1].Translation;
        Check(scroll.Keys.Count == 2 && scroll.IsLinear && scroll.GlobalSequence == 0 &&
              MathF.Abs(scroll.Keys[1].X - 2f) < 1e-4f && MathF.Abs(scroll.Keys[1].Y - 5f) < 1e-4f && sm.GlobalSequenceDurations[0] == 4000,
            $"the scroll loops seamlessly at its own speed: 0.5 and 1.25 per s -> 4 s ending on (2, 5) whole repeats (got {scroll.Keys.LastOrDefault()} over {sm.GlobalSequenceDurations[0]} ms)");
        Check(cells.Keys.Count == 16 && !cells.IsLinear && cells.GlobalSequence == 1 && sm.GlobalSequenceDurations[1] == 1000,
            "the flipbook steps 16 cells (interpolation 0) over one second on its own clock");
        Vector3 sampled = M2TrackSampling.Vector(cells, sm, 0, 0.30f, Vector3.Zero);
        Check(MathF.Abs(sampled.X) < 1e-4f && MathF.Abs(sampled.Y - 0.25f) < 1e-4f,
            $"at 0.30 s the flipbook HOLDS cell 4 = row 1, column 0 = (0, 0.25) - stepped, not blended (got {sampled})");
        for (int bIndex = 0; bIndex < sm.Batches.Count; bIndex++)
        {
            int transform = sm.GetTextureTransformForBatch(sm.Batches[bIndex]);
            int piece = sm.Batches[bIndex].TextureIndex;   // texture lookup is identity per piece
            int expected = piece == 0 ? 0 : piece == 1 ? 1 : -1;
            Check(transform == expected, $"batch {bIndex} (piece {piece}) uses transform {expected} (got {transform})");
        }
        Check(M2MeshParser.ReadMeshes(sb.Model).Count == 6, "the creator's mesh parser reads all six layers");
        // A sphere is closed and its normals point out: the top vertex's normal is +Z.
        Check(sm.Vertices.Any(v => MathF.Max(v.NormY, v.NormZ) > 0.99f), "the sphere's pole normal points out along its axis");
#if WEB_M2READER
        Check(WebM2.M2Reader.Parse(sb.Model) is not null, "the web M2Reader accepts the solids sketch");
#endif
        Console.WriteLine($"[emitter-lab] sketch solids: {sm.Vertices.Count} verts, {sm.Batches.Count} batches, " +
                          $"{sm.TextureTransforms.Count} texture transforms, scroll + flipbook laws hold");
    }

    // ── SPARKS and TRAILS: cross-file record copy (SPELL_SKETCH.md §4.5, PLAN P3.3 / P3.4) ──
    // Every preset's donor exists and copies; the copy re-reads the SAME scalars as the source
    // (rate, life, speed, gravity, shape, blend, atlas, colour and size keys, texture), rides the
    // piece's bone at the piece's pivot, and the card's overrides land where they should. With no
    // archive the extra is skipped with a note and the sketch still compiles.
    {
        Func<string, byte[]?> archive = p => mpq.ReadFile(p);
        int copied = 0;
        foreach (SketchSparkPreset preset in Enum.GetValues<SketchSparkPreset>())
        {
            if (preset is SketchSparkPreset.None or SketchSparkPreset.Custom) continue;
            var doc = new SketchDoc { Stage = "cast" };
            SketchPiece p = doc.AddPiece(SketchShapeKind.Disc);
            p.Place.Offset = new Vector3(0.5f, 0f, 0f);
            p.Extras.Sparks.Preset = preset;
            SketchWriter.DonorRef donor = SketchWriter.SparkDonor(p.Extras.Sparks)!.Value;
            M2Model? source = mpq.ReadFile(donor.Model) is { } donorBytes ? M2Reader.Parse(donorBytes) : null;
            Check(source is not null && source.ParticleEmitters.Count > donor.Index, $"spark preset {preset}: donor {donor.Model} e{donor.Index} exists");
            SketchWriter.SketchBuild? eb = SketchWriter.Build(doc, 4242, "Test Sparks", blpWriter, archive: archive);
            Check(eb is not null && eb.Notes is { Count: 0 }, $"spark preset {preset} compiles with no notes ({string.Join("; ", eb?.Notes ?? [])})");
            M2Model? em = M2Reader.Parse(eb!.Model);
            Check(em is not null && em.ParticleEmitters.Count == 1, $"spark preset {preset}: one emitter in the sketch");
            M2ParticleEmitter a = source!.ParticleEmitters[donor.Index], c = em!.ParticleEmitters[0];
            static bool Same(float x, float y) => MathF.Abs(x - y) <= 1e-4f * MathF.Max(1f, MathF.Abs(x));
            Check(Same(a.EmissionRate, c.EmissionRate) && Same(a.Lifespan, c.Lifespan) && Same(a.EmissionSpeed, c.EmissionSpeed) &&
                  Same(a.Gravity, c.Gravity) && Same(a.SpeedVariation, c.SpeedVariation) && Same(a.VerticalRange, c.VerticalRange) &&
                  Same(a.HorizontalRange, c.HorizontalRange) && Same(a.EmissionAreaLength, c.EmissionAreaLength) &&
                  a.Shape == c.Shape && a.BlendingType == c.BlendingType && a.TextureRows == c.TextureRows &&
                  a.TextureCols == c.TextureCols && a.ColorKeys.SequenceEqual(c.ColorKeys) && a.ScaleKeys.SequenceEqual(c.ScaleKeys) &&
                  a.Flags == c.Flags,
                $"spark preset {preset}: the copy re-reads the source's scalars (rate {a.EmissionRate}/{c.EmissionRate}, life {a.Lifespan}/{c.Lifespan}, speed {a.EmissionSpeed}/{c.EmissionSpeed})");
            Check(string.Equals(source.Textures[a.Texture].Filename, em.Textures[c.Texture].Filename, StringComparison.OrdinalIgnoreCase),
                $"spark preset {preset}: samples the donor's own texture ({em.Textures[c.Texture].Filename})");
            Check(c.Bone == 1 && MathF.Abs(c.PosX - 0.5f) < 1e-4f, $"spark preset {preset}: rides the piece's bone at its pivot");
#if WEB_M2READER
            Check(WebM2.M2Reader.Parse(eb.Model) is not null, $"spark preset {preset}: the web reader accepts it");
#endif
            copied++;
        }
        foreach (SketchTrailPreset preset in Enum.GetValues<SketchTrailPreset>())
        {
            if (preset is SketchTrailPreset.None or SketchTrailPreset.Custom) continue;
            var doc = new SketchDoc { Stage = "cast" };
            SketchPiece p = doc.AddPiece(SketchShapeKind.Crescent);
            p.Extras.Trail.Preset = preset;
            p.Extras.Trail.Length = 0.45f;
            SketchWriter.DonorRef donor = SketchWriter.TrailDonor(p.Extras.Trail)!.Value;
            M2Model? source = mpq.ReadFile(donor.Model) is { } donorBytes ? M2Reader.Parse(donorBytes) : null;
            Check(source is not null && source.RibbonEmitters.Count > donor.Index, $"trail preset {preset}: donor {donor.Model} r{donor.Index} exists");
            SketchWriter.SketchBuild? rb = SketchWriter.Build(doc, 4242, "Test Trail", blpWriter, archive: archive);
            Check(rb is not null && rb.Notes is { Count: 0 }, $"trail preset {preset} compiles with no notes ({string.Join("; ", rb?.Notes ?? [])})");
            M2Model? rm = M2Reader.Parse(rb!.Model);
            Check(rm is not null && rm.RibbonEmitters.Count == 1, $"trail preset {preset}: one ribbon in the sketch");
            M2RibbonEmitter a = source!.RibbonEmitters[donor.Index], c = rm!.RibbonEmitters[0];
            Check(MathF.Abs(a.EdgesPerSecond - c.EdgesPerSecond) < 1e-4f && MathF.Abs(c.EdgeLifetime - 0.45f) < 1e-4f &&
                  a.HeightAbove.Keys.FirstOrDefault() == c.HeightAbove.Keys.FirstOrDefault() &&
                  a.HeightBelow.Keys.FirstOrDefault() == c.HeightBelow.Keys.FirstOrDefault() && c.Bone == 1,
                $"trail preset {preset}: edges/s and heights copied, life = the card's 0.45 s, on the piece's bone");
            Check(c.Texture < rm.Textures.Count && string.Equals(rm.Textures[c.Texture].Filename, source.Textures[a.Texture].Filename, StringComparison.OrdinalIgnoreCase),
                $"trail preset {preset}: samples the donor's texture ({(c.Texture < rm.Textures.Count ? rm.Textures[c.Texture].Filename : "none")})");
            Check(c.Material < rm.RenderFlags.Count && a.Material < source.RenderFlags.Count &&
                  rm.RenderFlags[c.Material].BlendingMode == source.RenderFlags[a.Material].BlendingMode,
                $"trail preset {preset}: draws with the donor's blend");
            Check(c.Color.Keys.Count > 0 && c.Color.Keys.All(k => Vector3.Distance(k, p.Look.Colour) < 1e-4f),
                $"trail preset {preset}: wears the piece's colour");
#if WEB_M2READER
            Check(WebM2.M2Reader.Parse(rb.Model) is not null, $"trail preset {preset}: the web reader accepts it");
#endif
            copied++;
        }

        // The card's overrides, and the no-archive path.
        var over = new SketchDoc { Stage = "cast" };
        SketchPiece op = over.AddPiece(SketchShapeKind.Sphere);
        op.Extras.Sparks.Preset = SketchSparkPreset.Crackle;
        op.Extras.Sparks.Rate = 55f;
        op.Extras.Sparks.Size = 2f;
        op.Extras.Sparks.Colour = new Vector3(0.3f, 0.6f, 1f);
        op.Extras.Trail.Preset = SketchTrailPreset.Glow;
        SketchWriter.SketchBuild? ob = SketchWriter.Build(over, 4242, "Test Over", blpWriter, archive: archive);
        M2Model? om = M2Reader.Parse(ob!.Model);
        M2Model crackle = M2Reader.Parse(mpq.ReadFile(SketchWriter.SparkDonor(op.Extras.Sparks)!.Value.Model)!)!;
        M2ParticleEmitter oc = om!.ParticleEmitters[0], od = crackle.ParticleEmitters[0];
        Check(MathF.Abs(oc.ScalarTracks[6].Keys.Max() - 55f) < 1e-3f, $"Rate 55 sets the rate track's peak (got {oc.ScalarTracks[6].Keys.Max()})");
        Check(oc.ScaleKeys.Zip(od.ScaleKeys).All(z => MathF.Abs(z.First - 2f * z.Second) < 1e-4f), "Size 2 doubles the three size keys");
        Check(oc.ColorKeys.All(k => ((k >> 16) & 0xFF) < (k & 0xFF)) && oc.ColorKeys.Zip(od.ColorKeys).All(z => (z.First >> 24) == (z.Second >> 24)),
            "the recolour turns the sparks blue and keeps the donor's alpha ramp");
        Check(om.ParticleEmitters.Count == 1 && om.RibbonEmitters.Count == 1 && om.Textures.Count == 3,
            "a sphere with sparks and a trail: one emitter, one ribbon, three textures (piece + two donors)");
        Check(ob.Model.AsSpan().SequenceEqual(SketchWriter.Build(over, 4242, "Test Over", blpWriter, archive: archive)!.Model),
            "copying is deterministic: two builds are byte-identical");
        SketchWriter.SketchBuild? bare = SketchWriter.Build(over, 4242, "Test Over", blpWriter);
        Check(bare is not null && bare.Notes is { Count: 2 } && M2Reader.Parse(bare.Model)!.ParticleEmitters.Count == 0,
            "no archive: both extras skipped WITH notes, the sketch still compiles");
        var custom = new SketchDoc { Stage = "cast" };
        SketchPiece cp = custom.AddPiece(SketchShapeKind.Disc);
        cp.Extras.Sparks.Preset = SketchSparkPreset.Custom;
        cp.Extras.Sparks.Model = @"Spells\NoSuchModel.m2";
        Check(SketchWriter.Build(custom, 1, "x", blpWriter, archive: archive)!.Notes!.Single().Contains("not found"),
            "a custom donor that does not exist is a note, not a crash");
        Console.WriteLine($"[emitter-lab] sketch extras: {copied} presets copied cross-file and re-read identical; overrides land");
    }

    // ── CROSS-FILE TRANSPLANT INTO STOCK MODELS (PLAN P3.3) ──
    {
        string targetPath = new[] { @"Spells\Cleave_Cast_Base.m2", @"Spells\Arcane_Missile.m2" }.First(p => mpq.ReadFile(p) is not null);
        byte[] target = mpq.ReadFile(targetPath)!;
        M2Model before = M2Reader.Parse(target)!;
        int grafts = 0;
        foreach (SketchSparkPreset preset in Enum.GetValues<SketchSparkPreset>().Where(p => p is not (SketchSparkPreset.None or SketchSparkPreset.Custom)))
        {
            SketchWriter.DonorRef donor = SketchWriter.SparkDonor(new SketchSparks { Preset = preset })!.Value;
            byte[] donorBytes = mpq.ReadFile(donor.Model)!;
            M2RecordTransplant.Result? r = M2RecordTransplant.AddParticle(target, donorBytes, donor.Index, 0, new Vector3(0.3f, 0f, 1f), out string note);
            Check(r is not null, $"transplant {preset} into {targetPath}: {note}");
            M2Model? after = M2Reader.Parse(r!.Model);
            Check(after is not null && after.ParticleEmitters.Count == before.ParticleEmitters.Count + 1 && r.NewIndex == before.ParticleEmitters.Count,
                $"{targetPath} + {preset}: one more emitter, at index {r.NewIndex}");
            M2ParticleEmitter a = M2Reader.Parse(donorBytes)!.ParticleEmitters[donor.Index], c = after!.ParticleEmitters[r.NewIndex];
            Check(MathF.Abs(a.EmissionRate - c.EmissionRate) < 1e-4f && MathF.Abs(a.Lifespan - c.Lifespan) < 1e-4f &&
                  MathF.Abs(a.EmissionSpeed - c.EmissionSpeed) < 1e-4f && a.ColorKeys.SequenceEqual(c.ColorKeys) && a.Flags == c.Flags &&
                  string.Equals(after.Textures[c.Texture].Filename, r.TexturePath, StringComparison.OrdinalIgnoreCase) &&
                  c.Bone == 0 && MathF.Abs(c.PosX - 0.3f) < 1e-4f,
                $"{targetPath} + {preset}: the copy re-reads the donor (rate {a.EmissionRate}/{c.EmissionRate}) on bone 0 at the given point");
            Check(before.ParticleEmitters.Select((e, i) => (e, i)).All(p => after.ParticleEmitters[p.i].EmissionRate == p.e.EmissionRate),
                $"{targetPath} + {preset}: the target's own emitters are untouched");
#if WEB_M2READER
            Check(WebM2.M2Reader.Parse(r.Model) is not null, $"{targetPath} + {preset}: the web reader accepts it");
#endif
            grafts++;
        }
        foreach (SketchTrailPreset preset in Enum.GetValues<SketchTrailPreset>().Where(p => p is not (SketchTrailPreset.None or SketchTrailPreset.Custom)))
        {
            SketchWriter.DonorRef donor = SketchWriter.TrailDonor(new SketchTrail { Preset = preset })!.Value;
            byte[] donorBytes = mpq.ReadFile(donor.Model)!;
            M2RecordTransplant.Result? r = M2RecordTransplant.AddRibbon(target, donorBytes, donor.Index, 0, Vector3.Zero, out string note);
            M2Model? after = r is null ? null : M2Reader.Parse(r.Model);
            Check(after is not null && after.RibbonEmitters.Count == before.RibbonEmitters.Count + 1 &&
                  string.Equals(after.Textures[after.RibbonEmitters[r!.NewIndex].Texture].Filename, r.TexturePath, StringComparison.OrdinalIgnoreCase) &&
                  MathF.Abs(after.RibbonEmitters[r.NewIndex].EdgesPerSecond - M2Reader.Parse(donorBytes)!.RibbonEmitters[donor.Index].EdgesPerSecond) < 1e-4f,
                $"transplant trail {preset} into {targetPath}: one more ribbon with the donor's texture and edges/s ({note})");
            grafts++;
        }
        Console.WriteLine($"[emitter-lab] transplant: {grafts} donor records grafted into {targetPath}; both readers accept, the target's own records untouched");
    }

    // ── TRACK PATCHER (PLAN P3.1): every track of real models, rewritten, re-reads the same ──
    // Unchanged rewrites must be invisible to M2Reader (keys, times, interpolation, global
    // sequence, per-sequence sampling); an inserted / moved / deleted key must land where asked;
    // both readers accept the result; the arrays go to EOF and nothing before them moves.
    {
        string[] fixtures = { @"Item\ObjectComponents\Ammo\ArrowFireFlight_01.m2", @"Spells\Arcane_Missile.m2", @"Spells\Frostbolt.m2",
                              @"Spells\Cleave_Cast_Base.m2", @"Spells\ArcaneShot_Missile.m2" };
        int tracksChecked = 0, modelsChecked = 0;
        foreach (string trackFixture in fixtures)
        {
            if (mpq.ReadFile(trackFixture) is not { } bytes) { Console.WriteLine($"[emitter-lab] track fixture {trackFixture} missing"); continue; }
            modelsChecked++;
            List<TrackInfo> tracks = M2TrackPatcher.Enumerate(bytes);
            Check(tracks.Count > 0, $"{trackFixture}: the patcher finds its tracks");
            foreach (TrackInfo track in tracks)
            {
                TrackKeys? before = M2TrackPatcher.Read(bytes, track);
                if (before is null) continue;
                byte[] rewritten = M2TrackPatcher.Write(bytes, track, before);
                TrackKeys? after = M2TrackPatcher.Read(rewritten, track);
                bool same = after is not null && after.Times.SequenceEqual(before.Times) &&
                            after.Values.Zip(before.Values).All(p => p.First.SequenceEqual(p.Second)) &&
                            after.Interpolation == before.Interpolation && after.GlobalSequence == before.GlobalSequence;
                Check(same, $"{trackFixture} {track.Id}: an unchanged rewrite re-reads identical");
                Check(rewritten.AsSpan(0, bytes.Length).SequenceEqual(bytes.AsSpan()) == false || before.Times.Count == 0 ||
                      rewritten.Length > bytes.Length, $"{trackFixture} {track.Id}: the rewrite appends");
                tracksChecked++;
            }
            // Edit one emitter rate (or the first float track): insert, move, delete.
            TrackInfo? rate = tracks.FirstOrDefault(t => t.Id.EndsWith(".rate")) ?? tracks.FirstOrDefault(t => t.Kind == TrackValueKind.Float);
            if (rate is not null && M2TrackPatcher.Read(bytes, rate) is { } keys)
            {
                List<(uint Start, uint End)> seqs = M2TrackPatcher.Sequences(bytes);
                uint mid = seqs.Count > 0 ? seqs[0].Start + (seqs[0].End - seqs[0].Start) / 2 : 500;
                TrackKeys edited = keys.Clone();
                edited.Times.Add(mid); edited.Values.Add(new[] { 123.5f });
                byte[] inserted = M2TrackPatcher.Write(bytes, rate, edited);
                TrackKeys again = M2TrackPatcher.Read(inserted, rate)!;
                int at = again.Times.IndexOf(mid);
                Check(at >= 0 && again.Values[at][0] == 123.5f && again.Times.SequenceEqual(again.Times.OrderBy(t => t)),
                    $"{trackFixture} {rate.Id}: an inserted key lands at {mid} ms, the times stay sorted");
                M2Model? parsed = M2Reader.Parse(inserted);
                Check(parsed is not null && parsed.ParticleEmitters.Count == M2Reader.Parse(bytes)!.ParticleEmitters.Count,
                    $"{trackFixture}: M2Reader still reads the edited model whole");
#if WEB_M2READER
                Check(WebM2.M2Reader.Parse(inserted) is not null, $"{trackFixture}: the web reader accepts the edited model");
#endif
                again.Times[at] = mid + 7;
                TrackKeys moved = M2TrackPatcher.Read(M2TrackPatcher.Write(inserted, rate, again), rate)!;
                Check(moved.Times.Contains(mid + 7) && !moved.Times.Contains(mid), $"{trackFixture} {rate.Id}: a moved key moves");
                moved.Times.RemoveAt(moved.Times.IndexOf(mid + 7)); moved.Values.RemoveAt(moved.Values.FindIndex(v => v[0] == 123.5f));
                TrackKeys deleted = M2TrackPatcher.Read(M2TrackPatcher.Write(inserted, rate, moved), rate)!;
                Check(deleted.Times.SequenceEqual(keys.Times), $"{trackFixture} {rate.Id}: deleting it gives the original keys back");
            }
        }
        Console.WriteLine($"[emitter-lab] track patcher: {tracksChecked} tracks over {modelsChecked} models rewrite losslessly; insert/move/delete land");
    }

    // ── RULES ROUND TRIP + LEGALITY CENSUS (PLAN P5.2) ──
    // Every stock Spell.dbc row, authored onto the from-zero template the way the workshop does
    // (CopyFrom every column), exported as the rules JSON the session carries, applied to a FRESH
    // template and read back: every column must come back as the row had it. Then the legality law
    // over every stock spell - a census, not a gate: what it flags in Blizzard's own data is what
    // an author copying from that spell will see.
    {
        SpellRuleTables tables = SpellRuleTables.Load(mpq)!;
        Check(tables.TryGetSpellRow(MSUIClient.Creator.Spells.SpellRules.FromZeroTemplateSpell, out uint[] template),
            "the from-zero template spell 18282 is in Spell.dbc");
        string[] columns = MSUIClient.Creator.Spells.SpellTemplateColumns.All.Where(c => c.Kind != MSUIClient.Creator.Spells.SpellTemplateColumns.Kind.BigHigh).Select(c => c.Name)
            .Where(n => !MSUIClient.Creator.Spells.SpellRules.ServerOnlyColumns.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();
        int ruleRows = 0, lossless = 0;
        var lostColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (uint id in tables.SpellIds.OrderBy(i => i))
        {
            tables.TryGetSpellRow(id, out uint[] row);
            Dictionary<string, double> truth = MSUIClient.Creator.Spells.SpellTemplateColumns.FromDbcRow(row);
            var authored = MSUIClient.Creator.Spells.SpellRules.FromDbcRow(MSUIClient.Creator.Spells.SpellRules.FromZeroTemplateSpell, template);
            authored.CopyFrom(row, columns);
            var reopened = MSUIClient.Creator.Spells.SpellRules.FromDbcRow(MSUIClient.Creator.Spells.SpellRules.FromZeroTemplateSpell, template);
            reopened.ApplyJson(JsonNode.Parse(authored.ToJson().ToJsonString()));
            bool same = true;
            foreach (string column in columns)
            {
                double want = truth.TryGetValue(column, out double v) ? v : 0;
                double got = reopened.Get(column);
                bool equal = MSUIClient.Creator.Spells.SpellTemplateColumns.TryGet(column, out MSUIClient.Creator.Spells.SpellTemplateColumns.Column meta) && meta.Kind == MSUIClient.Creator.Spells.SpellTemplateColumns.Kind.Float
                    ? (float)want == (float)got || (float.IsNaN((float)want) && float.IsNaN((float)got))
                    : want == got;
                if (!equal)
                {
                    same = false;
                    lostColumns[column] = lostColumns.GetValueOrDefault(column) + 1;
                }
            }
            ruleRows++;
            if (same) lossless++;
        }
        Check(lossless == ruleRows, $"rules round trip: {lossless} of {ruleRows} stock spells come back lossless" +
              (lostColumns.Count == 0 ? "" : $" (lost: {string.Join(", ", lostColumns.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} x{k.Value}"))})"));

        var errorKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int clean = 0, withError = 0;
        foreach (uint id in tables.SpellIds)
        {
            tables.TryGetSpellRow(id, out uint[] row);
            var rules = MSUIClient.Creator.Spells.SpellRules.FromDbcRow(id, row);
            var lines = MSUIClient.Creator.Spells.SpellLegality.Validate(rules, tables, s => tables.TryGetSpellRow(s, out _));
            var errors = lines.Where(l => l.Severity == MSUIClient.Creator.Spells.SpellLegality.Severity.Error).ToList();
            if (errors.Count == 0) clean++;
            else
            {
                withError++;
                foreach (var e in errors) errorKinds[e.Field] = errorKinds.GetValueOrDefault(e.Field) + 1;
            }
        }
        Console.WriteLine($"[emitter-lab] rules round trip: {lossless}/{ruleRows} lossless; legality census: {clean} stock spells clean, " +
                          $"{withError} flagged ({string.Join(", ", errorKinds.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} x{k.Value}"))})");
    }

    // ── CAMERA SHAKE (PLAN P4.3): the tables load, kits name them, the wave law holds ──
    {
        int shakingKits = 0;
        var shakers = new List<string>();
        foreach (SpellInfo spell in spells.Spells.OrderBy(s => s.Id))
        {
            if (!visuals.TryGetStages(spell.VisualId, out SpellVisualStages st)) continue;
            foreach (SpellStage stage in Enum.GetValues<SpellStage>())
                if (visuals.TryGetKit(SpellVisualCatalog.KitFor(st, stage), out SpellVisualKitInfo kit) && kit.Shake is uint shake)
                {
                    shakingKits++;
                    if (shakers.Count < 12) shakers.Add($"{spell.Id} {spell.Name} {stage}->{shake}");
                    if (Environment.GetEnvironmentVariable("LAB_LIST_SHAKES") is not null) Console.WriteLine($"[shake-list] {spell.Id} {spell.Name} {stage} -> {shake}");
                }
        }
        Check(shakingKits > 0, "some stock spell kit names a camera shake (f14)");
        Check(visuals.SpellCameraShakeIds.Any() && visuals.SpellCameraShakeIds.All(id => visuals.TryGetCameraShake(id, out _)),
            "every SpellEffectCameraShakes row resolves to CameraShakes waves");
        Check(visuals.TryGetCameraShake(1, out IReadOnlyList<CameraShakeInfo> quake) && quake.Count == 3 &&
              quake.Select(w => w.Direction).OrderBy(d => d).SequenceEqual(new uint[] { 0, 1, 2 }),
            "SpellEffectCameraShakes 1 is three waves, one per camera axis");
        CameraShakeInfo w0 = quake[0];
        Check(CameraShakeLaw.Wave(w0, w0.Duration + 0.01f) == 0f && CameraShakeLaw.Wave(w0, -0.01f) == 0f,
            "a wave is silent before it starts and after its duration");
        float peak = Enumerable.Range(0, 400).Max(i => MathF.Abs(CameraShakeLaw.Wave(w0, w0.Duration * i / 400f)));
        Check(peak <= w0.Amplitude * CameraShakeLaw.YardsPerAmplitude + 1e-4f && peak > 0f,
            $"a wave never exceeds its amplitude ({peak:0.###} yd <= {w0.Amplitude * CameraShakeLaw.YardsPerAmplitude:0.###})");
        Check(CameraShakeLaw.Falloff(5f) == 1f && CameraShakeLaw.Falloff(100f) == 0f &&
              MathF.Abs(CameraShakeLaw.Falloff(37.5f) - 0.5f) < 1e-4f, "distance falloff: full near, half at 37.5 yd, none far");
        var bounce = w0 with { Type = 1 };
        Check(Enumerable.Range(0, 100).All(i => CameraShakeLaw.Wave(bounce, bounce.Duration * i / 100f) >= 0f),
            "a type-1 wave bounces (never below rest)");
        Console.WriteLine($"[emitter-lab] camera shake: {shakingKits} stock kits shake; e.g. {string.Join("; ", shakers.Take(6))}");
    }

    // ── HIERARCHY + ORBIT + JITTER (PLAN P3.8), through the client's own M2Animator ──
    // A child rides its parent's travel; an orbit keeps its radius and loops on its own clock; a
    // jitter holds between rolls, stays inside its box and is deterministic; a parent cycle
    // resolves to the root; modifier bones never renumber a piece's bone.
    {
        var doc = new SketchDoc { Stage = "cast" };
        SketchPiece parent = doc.AddPiece(SketchShapeKind.Disc);
        parent.Place.Facing = SketchFacing.VerticalForward;
        parent.Motion.Distance = 2f; parent.Motion.Duration = 0.5f; parent.Motion.Ease = SketchEase.Linear;
        parent.Motion.FadeOut = 0f;
        SketchPiece child = doc.AddPiece(SketchShapeKind.Disc);
        child.Place.Facing = SketchFacing.VerticalForward;
        child.Place.Origin = new Vector3(0f, 1f, 1.2f);
        child.Place.Parent = parent.Name;
        SketchPiece moon = doc.AddPiece(SketchShapeKind.Sphere);
        moon.Place.Origin = new Vector3(0f, -1f, 1.2f);
        moon.Motion.OrbitRadius = 0.5f; moon.Motion.OrbitTurnsPerSecond = 1f;
        SketchPiece arc = doc.AddPiece(SketchShapeKind.Beam);
        arc.Motion.JitterYards = 0.1f; arc.Motion.JitterDegrees = 40f; arc.Motion.JitterRate = 10f;
        SketchWriter.SketchBuild? hb = SketchWriter.Build(doc, 4242, "Test Tree", blpWriter);
        M2Model? hm = M2Reader.Parse(hb!.Model);
        Check(hm is not null && hm.Bones.Count == 7, $"4 pieces + root + 2 modifier bones = 7 bones (got {hm?.Bones.Count})");
        Check(hm!.Bones[2].ParentBone == 1 && hm.Bones[3].ParentBone == 5 && hm.Bones[4].ParentBone == 6 &&
              hm.Bones[5].ParentBone == 0 && hm.Bones[6].ParentBone == 0,
            "child -> parent's bone; orbit and jitter pieces -> their modifier bones -> root");
        Check(hm.Bones[5].Translation.GlobalSequence >= 0 && hm.Bones[6].Rotation.GlobalSequence >= 0 &&
              hm.Bones[6].Rotation.InterpolationType == 0,
            "modifiers run on their own global sequences; the jitter is stepped");
        var animator = MSUIClient.World.Units.M2Animator.Build(hm, Array.Empty<int>(), true);
        var clip = animator!.FindSequenceOrBake(0, true);
        var skin = new Matrix4x4[hm.Bones.Count];
        static Vector3 Model(Vector3 raw) => new(raw.X, raw.Z, -raw.Y);   // M2Reader's swap
        Vector3 At(int bone, Vector3 raw) => Vector3.Transform(Model(raw), skin[bone]);

        animator.Evaluate(clip, 0.49f, 0f, skin);
        Vector3 childAt = At(2, child.Place.Origin);
        Check(MathF.Abs(childAt.X - 1.96f) < 0.05f && MathF.Abs(childAt.Z - Model(child.Place.Origin).Z) < 1e-3f,
            $"the child rides its parent's 2 yd travel (x {childAt.X:0.00} at 0.49 s of 0.5)");

        bool radiusHolds = true;
        foreach (float g in new[] { 0f, 0.1f, 0.33f, 0.5f, 0.77f })
        {
            animator.Evaluate(clip, 0.1f, g, skin);
            float r = Vector3.Distance(At(3, moon.Place.Origin), Model(moon.Place.Origin));
            radiusHolds &= MathF.Abs(r - 0.5f) < 0.02f;
        }
        animator.Evaluate(clip, 0.1f, 0.25f, skin); Vector3 quarter = At(3, moon.Place.Origin);
        animator.Evaluate(clip, 0.1f, 1.25f, skin); Vector3 nextLap = At(3, moon.Place.Origin);
        Check(radiusHolds && Vector3.Distance(quarter, nextLap) < 1e-3f,
            "the orbit keeps its 0.5 yd radius and comes round to the same place a turn later");

        // Jitter: 10 rolls a second -> held for 0.1 s; inside its box; different between rolls.
        Vector3 tip = arc.Place.Origin + new Vector3(0.5f, 0f, 0f);
        animator.Evaluate(clip, 0.1f, 0.31f, skin); Vector3 j1 = At(4, tip);
        animator.Evaluate(clip, 0.1f, 0.38f, skin); Vector3 j2 = At(4, tip);
        animator.Evaluate(clip, 0.1f, 0.45f, skin); Vector3 j3 = At(4, tip);
        Check(Vector3.Distance(j1, j2) < 1e-4f && Vector3.Distance(j2, j3) > 1e-3f,
            "the jitter HOLDS between rolls and jumps at the next one (flicker, not drift)");
        Check(Vector3.Distance(j3, Model(tip)) < 0.1f * 1.8f + 0.5f * 2f * MathF.Sin(40f * MathF.PI / 180f) + 0.01f,
            "the jitter stays inside its box");
        Check(hb.Model.AsSpan().SequenceEqual(SketchWriter.Build(doc, 4242, "Test Tree", blpWriter)!.Model),
            "the jitter is seeded: two builds are byte-identical");
        Check(hm.Vertices.All(v => v.BoneIndex0 >= 1 && v.BoneIndex0 <= 4), "every vertex still names its PIECE bone (1-4)");

        child.Place.Parent = null; parent.Place.Parent = child.Name; child.Place.Parent = parent.Name;
        int[] resolved = SketchWriter.ResolveParents(doc);
        Check(resolved[0] == -1 || resolved[1] == -1, "a parent cycle is broken to the root, not followed forever");
        Check(SketchWriter.Build(doc, 4242, "Test Tree", blpWriter) is not null, "a doc with a cycle still compiles");
        Console.WriteLine("[emitter-lab] sketch hierarchy: parent carries child, orbit loops, jitter flickers, cycles break");
    }

    // ── the picture (SPELL_SKETCH §1): what the owner will actually see ───────
    // Bytes that parse are not the same as bytes that DRAW. This walks the whole
    // chain the game walks - M2Reader on our file, the client's own M2Animator for
    // the bone tracks, the colour record for tint and fade, the BLP for the shape -
    // and rasterizes it, so "the slash flies 3 yards and fades" is a picture and not
    // a claim. Deterministic and archive-free, like the rest of this section.
    {
        string previewPath = Path.Combine(config.RepoRoot, "dumps", "sketch-preview.png");

        static Dictionary<string, byte[]> TexMap(SketchWriter.SketchBuild b)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (SketchWriter.SketchTextureFile t in b.Textures) map[t.Path] = t.Blp;
            return map;
        }

        RenderSketchFilmstrip(previewPath, new[]
        {
            // The §1 script, from the side: the crescent stands on edge, leaves the
            // chest, decelerates over 3 yd and fades out.
            ("two crescents: 3 yd forward, ease out, fade", sk!, TexMap(built), built.LengthSeconds,
                new Vector3(1.5f, 1.2f, 0f), new Vector3(0f, 0f, 8f), 5.2f),
            // The other shapes, from three-quarters: a flat ring, a camera-facing star
            // wearing a glow, and a side-facing crescent spinning half a turn.
            ("ring flat / star + glow / crescent spinning", fm!, TexMap(facingBuild),
                facingBuild.LengthSeconds, new Vector3(0f, 1.2f, 0f), new Vector3(-1.6f, 3.4f, 6.6f), 5.2f),
            // Appearance time: the blue slash is told to start a quarter second after the amber
            // one, so it is not there at all in the first frames and then follows it out.
            ("appearance time: the blue slash starts 0.25s after the amber one",
                M2Reader.Parse(timedBuild!.Model)!, TexMap(timedBuild), timedBuild.LengthSeconds,
                new Vector3(1.5f, 1.2f, 0f), new Vector3(0f, 0f, 8f), 5.2f),
            // A hand drawing, treated exactly like a shelf shape.
            ("hand-drawn: two strokes, travelling and fading",
                M2Reader.Parse(drawnBuild!.Model)!, TexMap(drawnBuild), drawnBuild.LengthSeconds,
                new Vector3(1f, 1.2f, 0f), new Vector3(0f, 0f, 8f), 5.2f),
            // The shape shelf: every primitive the shelf offers, drawn from its own maths.
            ("shape shelf: crescent ring disc arrow line star rect", shelfModel2!, TexMap(shelfBuild2!),
                shelfBuild2!.LengthSeconds, new Vector3(0f, 1.2f, 0f), new Vector3(0f, 0f, 9f), 9.2f),
        });
        Check(File.Exists(previewPath) && new FileInfo(previewPath).Length > 1024,
            "the sketch preview did not render");
        Console.WriteLine($"[emitter-lab] sketch preview: wrote {previewPath}");

        // Keep the fixture's bytes beside the picture: when a reader disagrees one day,
        // the first useful question is "what did we actually write", and a file answers it.
        string fixturePath = Path.Combine(config.RepoRoot, "dumps", "sketch-fixture.m2");
        File.WriteAllBytes(fixturePath, built.Model);
        foreach (SketchWriter.SketchTextureFile t in built.Textures)
            File.WriteAllBytes(Path.Combine(config.RepoRoot, "dumps",
                "sketch-fixture_" + Path.GetFileName(t.Path)), t.Blp);
    }

    Console.WriteLine($"[emitter-lab] sketch writer: {built.PieceCount} piece(s) -> " +
                      $"{built.BatchCount} quad(s), {built.BoneCount} bone(s), {built.VertexCount} vert(s), " +
                      $"{built.Model.Length} B M2 + {built.Textures.Count} BLP; " +
                      $"facings/glow/spin fixture {facingBuild.BatchCount} quad(s) - " +
                      "round-tripped through M2Reader and all four creator parsers");
}

Console.WriteLine("[emitter-lab] ribbon textures (trail preset candidates, SPELL_SKETCH §4.5):");
foreach (var (tex, n) in ribbonTextures.OrderByDescending(kv => kv.Value).Take(16))
    Console.WriteLine($"  {n,4}  {tex}");
Console.WriteLine("[emitter-lab] ribbon examples:");
foreach (string example in ribbonExamples) Console.WriteLine($"  {example}");

string output = args.Length == 2 ? Path.GetFullPath(args[1]) :
    Path.Combine(config.RepoRoot, "dumps", "spell-emitter-census.csv");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var csv = new List<string>
{
    "model,uses,emitter,shape_id,shape,texture,blend,flags_hex,pos_x,pos_y,pos_z,bone," +
    "rate,life,steady_population,speed,speed_variation,area_length,area_width," +
    "vertical_range,horizontal_range,gravity,z_source,drag,sprite_spin,bone_spin," +
    "bone_motion,angular_min,angular_max,geometry_model,recursion_model,head_tail," +
    "midpoint,enable_keys,animated_scalar_tracks"
};
csv.AddRange(rows.Select(r => string.Join(',', Q(r.Model), Q(r.Uses), r.Emitter,
    r.ShapeId, r.Shape, Q(r.Texture), r.Blend, $"0x{r.Flags:X}", F(r.Position.X),
    F(r.Position.Y), F(r.Position.Z), r.Bone, F(r.Rate), F(r.Life), F(r.Population),
    F(r.Speed), F(r.SpeedVariation), F(r.AreaLength), F(r.AreaWidth),
    F(r.VerticalRange), F(r.HorizontalRange), F(r.Gravity), F(r.ZSource),
    F(r.Drag), F(r.Spin), r.BoneSpin, r.BoneMotion, Q(V(r.AngularMin)),
    Q(V(r.AngularMax)), Q(r.GeometryModel), Q(r.RecursionModel), r.HeadTail,
    F(r.MidPoint), r.EnableKeys, r.AnimatedScalarTracks)));
File.WriteAllLines(output, csv, new UTF8Encoding(false));

// ── The tint law (Formats/BlpRecolor.cs; shared_docs/SPELL_IDE_MAP.md §6.2) ────────────
// A pure-white pixel could not take a tint at all (HSL lightness 1 has no hue), which is
// why "Tint" on a sketch's white image, or on any white glow, did nothing. Greys and whites
// now multiply by the target; coloured pixels still hue-map with their lightness kept.
{
    const uint red = 0xFF0000, amber = 0xFF8C00;
    byte[] pixels =
    {
        255, 255, 255, 255,   // BGRA white
        128, 128, 128, 200,   // mid grey, alpha 200
        0, 0, 0, 255,         // black
        255, 0, 0, 255,       // pure blue (B=255): coloured, hue-mapped
    };
    BlpRecolor.HueMapBgra(pixels, amber);
    Check(pixels[2] == 255 && pixels[1] == 140 && pixels[0] == 0 && pixels[3] == 255,
        $"tint law: white must become the target itself, got B{pixels[0]} G{pixels[1]} R{pixels[2]} A{pixels[3]}");
    Check(pixels[6] == 128 && pixels[5] == 70 && pixels[4] == 0 && pixels[7] == 200,
        $"tint law: mid grey must become a half-bright target with alpha kept, got B{pixels[4]} G{pixels[5]} R{pixels[6]} A{pixels[7]}");
    Check(pixels[8] == 0 && pixels[9] == 0 && pixels[10] == 0, "tint law: black stays black");
    Check(pixels[14] > pixels[12] && pixels[14] > pixels[13],
        "tint law: a saturated blue pixel hue-maps to the target's hue (red-dominant)");
    Vector3 white = BlpRecolor.HueMapColor(Vector3.One, red);
    Check(MathF.Abs(white.X - 1f) < 1e-3f && white.Y < 1e-3f && white.Z < 1e-3f,
        $"tint law (colour track): white -> red, got {white}");
    Vector3 grey = BlpRecolor.HueMapColor(new Vector3(0.5f), red);
    Check(MathF.Abs(grey.X - 0.5f) < 1e-3f && grey.Y < 1e-3f && grey.Z < 1e-3f,
        $"tint law (colour track): grey -> half red, got {grey}");
    Vector3 greyTarget = BlpRecolor.HueMapColor(Vector3.One, 0x808080);
    Check((greyTarget - Vector3.One).Length() < 1e-3f, "tint law: a grey target leaves a white pixel alone");
}

Console.WriteLine($"[emitter-lab] spell-models={uses.Count} resolved={uses.Count - unresolved.Count} " +
                  $"unresolved={unresolved.Count} emitters={rows.Count} checks={checks}");
Console.WriteLine($"[emitter-lab] mesh-layers={meshLayers} (with a colour record {meshLayersWithColour}) " +
                  $"ribbons={ribbonCount}");
foreach (IGrouping<ushort, Row> group in rows.GroupBy(r => r.ShapeId).OrderBy(g => g.Key))
    Console.WriteLine($"[emitter-lab] shape-id={group.Key} parsed={group.First().Shape} count={group.Count()}");
Console.WriteLine($"[emitter-lab] sprite-spin={rows.Count(r => MathF.Abs(r.Spin) > 1e-6f)} " +
                  $"bone-spin={rows.Count(r => r.BoneSpin)} bone-motion={rows.Count(r => r.BoneMotion)} " +
                  $"model-particle={rows.Count(r => r.GeometryModel.Length > 0)} " +
                  $"recursion={rows.Count(r => r.RecursionModel.Length > 0)} " +
                  $"burst={rows.Count(r => (r.Flags & 0x8000) != 0)}");
Console.WriteLine("[emitter-lab] largest plane birth regions:");
foreach (Row row in rows.Where(r => r.Shape == nameof(ParticleShape.Plane))
             .OrderByDescending(r => r.AreaLength * r.AreaWidth).Take(12))
    Console.WriteLine($"  {row.Model} e{row.Emitter} area={row.AreaLength:0.###}x{row.AreaWidth:0.###} " +
                      $"speed={row.Speed:0.###} rate={row.Rate:0.###} life={row.Life:0.###} " +
                      $"boneSpin={row.BoneSpin} flags=0x{row.Flags:X}");
Console.WriteLine("[emitter-lab] representative spin mechanisms:");
foreach (Row row in rows.Where(r => MathF.Abs(r.Spin) > 1e-6f || r.BoneSpin ||
                                    r.AngularMin != Vector3.Zero || r.AngularMax != Vector3.Zero)
             .OrderByDescending(r => r.BoneSpin).ThenByDescending(r => MathF.Abs(r.Spin)).Take(16))
    Console.WriteLine($"  {row.Model} e{row.Emitter} sprite={row.Spin:0.###} bone={row.BoneSpin} " +
                      $"tumble={V(row.AngularMin)}..{V(row.AngularMax)} geometry={row.GeometryModel}");
Console.WriteLine($"[emitter-lab] wrote {output}");
return 0;

// ── Sketch preview rasterizer (SPELL_SKETCH §1) ──────────────────────────────
// A tiny software rasterizer for the authored M2, on purpose: it shares no code with
// the GL renderer, so agreeing with it is evidence rather than a tautology. Bones come
// from the client's real M2Animator, tint and fade from the colour record, the shape
// from the BLP - the same four things the game combines.
//
// Two rows, because one view cannot show both: the SIDE view looks down the axis the
// pieces are separated on (so they overlap, correctly) and shows the 3 yd flight and
// the chest height; the TOP view separates the two slashes by their 0.5 yd paste offset.
static void RenderSketchFilmstrip(string outPath,
    (string Label, M2Model Model, Dictionary<string, byte[]> Blps, float Length,
     Vector3 Focus, Vector3 EyeOffset, float ViewWidth)[] rows)
{
    const int FrameW = 300, FrameH = 200, Frames = 6, Gutter = 18;
    int stripW = FrameW * Frames, stripH = (FrameH + Gutter) * rows.Length;
    var canvas = new float[stripW * stripH * 3];

    for (int r = 0; r < rows.Length; r++)
    {
        var (_, m2, blps, length, focus, eyeOffset, viewWidth) = rows[r];

        var animator = MSUIClient.World.Units.M2Animator.Build(m2, Array.Empty<int>(), true);
        var clip = animator?.FindSequenceOrBake(0, true);
        var skin = new Matrix4x4[Math.Max(m2.Bones.Count, 1)];

        // Decode each texture once. A sketch texture is white, so only alpha matters.
        var decoded = new Dictionary<string, (byte[] Bgra, int W, int H)>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, byte[] blp) in blps)
        {
            byte[] px = BlpDecoder.GetPixels(blp, 0, out int tw, out int th);
            decoded[path] = (px, tw, th);
        }

        // Model space after M2Reader's swap: X = forward, Y = up, Z = right.
        Vector3 eye = focus + eyeOffset;
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, focus, new Vector3(0f, 1f, 0f));
        Matrix4x4 viewProj = view * Matrix4x4.CreateOrthographic(
            viewWidth, viewWidth * FrameH / FrameW, 0.01f, 64f);

        for (int f = 0; f < Frames; f++)
        {
            // Sample just inside the window: the effect is dead exactly at t = length,
            // where the fade key is zero and there is by definition nothing to see.
            float t = MathF.Min(length * f / (Frames - 1), length - 0.001f);
            animator?.Evaluate(clip, t, 0f, skin);
            if (clip is null) for (int i = 0; i < skin.Length; i++) skin[i] = Matrix4x4.Identity;

            // Billboards are applied by the RENDERER, after the animator, so a preview that
            // stops at Evaluate shows a camera-facing piece in its un-billboarded pose — i.e.
            // it cannot see the one bug that makes such a piece invisible. Run the real law.
            //
            // ApplyBillboardBones works in model space but reads the camera in WORLD space
            // and assumes the world is Z-up, so it needs the model-to-world transform that
            // undoes M2Reader's swap: model (fwd, up, right) -> world (fwd, left, up).
            var modelToWorld = new Matrix4x4(
                1f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f,
                0f, -1f, 0f, 0f,
                0f, 0f, 0f, 1f);
            MSUIClient.World.Units.SpellMeshSkinningLaw.ApplyBillboardBones(
                m2, modelToWorld,
                Vector3.Transform(eye, modelToWorld),
                Vector3.Normalize(Vector3.TransformNormal(Vector3.Normalize(focus - eye), modelToWorld)),
                m2.Bones.Count, skin);

            int ox = f * FrameW, oy = r * (FrameH + Gutter);
            DrawSketchGround(canvas, stripW, ox, oy, FrameW, FrameH, viewProj);

            for (int bi = 0; bi < m2.Batches.Count; bi++)
            {
                M2Batch batch = m2.Batches[bi];
                if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;
                M2Submesh sub = m2.Submeshes[batch.SubmeshIndex];

                Vector3 tint = Vector3.One;
                float alpha = 1f;
                if (batch.ColorIndex >= 0 && batch.ColorIndex < m2.Colors.Count)
                {
                    tint = M2TrackSampling.Vector(m2.Colors[batch.ColorIndex].Color, m2, 0, t, Vector3.One);
                    alpha = M2TrackSampling.Fixed16(m2.Colors[batch.ColorIndex].Alpha, m2, 0, t);
                }
                alpha *= m2.GetStaticAlphaForBatch(batch);
                if (alpha <= 0.001f) continue;

                int texSlot = batch.TextureIndex < m2.TextureLookup.Count
                    ? m2.TextureLookup[batch.TextureIndex] : -1;
                if (texSlot < 0 || texSlot >= m2.Textures.Count) continue;
                if (!decoded.TryGetValue(m2.Textures[texSlot].Filename, out var tex)) continue;

                int end = Math.Min(m2.Indices.Count, sub.IndexStart + sub.IndexCount);
                for (int i = sub.IndexStart; i + 2 < end; i += 3)
                {
                    Span<Vector3> pos = stackalloc Vector3[3];
                    Span<Vector2> uv = stackalloc Vector2[3];
                    bool ok = true;
                    for (int k = 0; k < 3; k++)
                    {
                        int idx = m2.Indices[i + k];
                        if (idx >= m2.Vertices.Count) { ok = false; break; }
                        M2Vertex vert = m2.Vertices[idx];
                        Matrix4x4 bone = vert.BoneIndex0 < skin.Length ? skin[vert.BoneIndex0] : Matrix4x4.Identity;
                        pos[k] = Vector3.Transform(new Vector3(vert.PosX, vert.PosY, vert.PosZ), bone);
                        uv[k] = new Vector2(vert.TexU, vert.TexV);
                    }
                    if (!ok) continue;
                    RasterizeSketchTriangle(canvas, stripW, ox, oy, FrameW, FrameH,
                        viewProj, pos, uv, tex, tint * alpha);
                }
            }
        }
    }

    // Additive accumulation -> 8-bit, clamped.
    var info = new SKImageInfo(stripW, stripH, SKColorType.Rgba8888, SKAlphaType.Opaque);
    using var bitmap = new SKBitmap(info);
    var pixels = new SKColor[stripW * stripH];
    for (int i = 0; i < pixels.Length; i++)
        pixels[i] = new SKColor(
            (byte)Math.Clamp((int)MathF.Round(canvas[i * 3 + 0] * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(canvas[i * 3 + 1] * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(canvas[i * 3 + 2] * 255f), 0, 255), 255);
    bitmap.Pixels = pixels;

    // Labels last, so nothing additive washes them out.
    using (var skc = new SKCanvas(bitmap))
    using (var font = new SKFont(SKTypeface.Default, 13f))
    using (var small = new SKFont(SKTypeface.Default, 11f))
    using (var white = new SKPaint { Color = new SKColor(210, 210, 215), IsAntialias = true })
    using (var grey = new SKPaint { Color = new SKColor(120, 120, 128), IsAntialias = true })
    {
        for (int r = 0; r < rows.Length; r++)
        {
            int oy = r * (FrameH + Gutter);
            skc.DrawText(rows[r].Label, 8f, oy + 16f, font, white);
            for (int f = 0; f < Frames; f++)
            {
                float t = MathF.Min(rows[r].Length * f / (Frames - 1), rows[r].Length - 0.001f);
                skc.DrawText($"t = {t:0.00}s", f * FrameW + 8f, oy + FrameH + 13f, small, grey);
            }
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream fs = File.Create(outPath);
    png.SaveTo(fs);
}

/// <summary>A dim ground line and one tick per yard of travel, so the flight is readable.</summary>
static void DrawSketchGround(float[] canvas, int stripW, int ox, int oy, int fw, int fh,
    Matrix4x4 viewProj)
{
    for (int yard = 0; yard <= 4; yard++)
    {
        float shade = yard == 0 ? 0.30f : 0.11f;
        for (int step = 0; step <= 40; step++)
        {
            var world = new Vector3(yard, step / 40f * 0.14f, 0f);
            if (!ProjectSketch(world, viewProj, fw, fh, out int px, out int py)) continue;
            PlotSketch(canvas, stripW, ox + px, oy + py, new Vector3(shade));
        }
    }
    for (int step = 0; step <= 260; step++)
    {
        var world = new Vector3(-0.6f + step / 260f * 4.8f, 0f, 0f);
        if (!ProjectSketch(world, viewProj, fw, fh, out int px, out int py)) continue;
        PlotSketch(canvas, stripW, ox + px, oy + py, new Vector3(0.13f));
    }
}

static bool ProjectSketch(Vector3 world, Matrix4x4 viewProj, int fw, int fh, out int px, out int py)
{
    Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProj);
    px = (int)MathF.Round((clip.X * 0.5f + 0.5f) * fw);
    py = (int)MathF.Round((1f - (clip.Y * 0.5f + 0.5f)) * fh);
    return px >= 0 && px < fw && py >= 0 && py < fh;
}

static void PlotSketch(float[] canvas, int stripW, int x, int y, Vector3 add)
{
    int o = (y * stripW + x) * 3;
    if (o < 0 || o + 2 >= canvas.Length) return;
    canvas[o + 0] += add.X; canvas[o + 1] += add.Y; canvas[o + 2] += add.Z;
}

/// <summary>Barycentric fill with UV interpolation, additive - blend mode 4 is what nine
/// in ten spell effects use and what every sketch preset defaults to.</summary>
static void RasterizeSketchTriangle(float[] canvas, int stripW, int ox, int oy, int fw, int fh,
    Matrix4x4 viewProj, ReadOnlySpan<Vector3> pos, ReadOnlySpan<Vector2> uv,
    (byte[] Bgra, int W, int H) tex, Vector3 tint)
{
    Span<Vector2> screen = stackalloc Vector2[3];
    for (int k = 0; k < 3; k++)
    {
        Vector4 clip = Vector4.Transform(new Vector4(pos[k], 1f), viewProj);
        screen[k] = new Vector2((clip.X * 0.5f + 0.5f) * fw, (1f - (clip.Y * 0.5f + 0.5f)) * fh);
    }

    float area = (screen[1].X - screen[0].X) * (screen[2].Y - screen[0].Y) -
                 (screen[2].X - screen[0].X) * (screen[1].Y - screen[0].Y);
    if (MathF.Abs(area) < 1e-6f) return;                 // edge-on: nothing to fill

    int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(screen[0].X, MathF.Min(screen[1].X, screen[2].X))));
    int maxX = Math.Min(fw - 1, (int)MathF.Ceiling(MathF.Max(screen[0].X, MathF.Max(screen[1].X, screen[2].X))));
    int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(screen[0].Y, MathF.Min(screen[1].Y, screen[2].Y))));
    int maxY = Math.Min(fh - 1, (int)MathF.Ceiling(MathF.Max(screen[0].Y, MathF.Max(screen[1].Y, screen[2].Y))));

    for (int y = minY; y <= maxY; y++)
    for (int x = minX; x <= maxX; x++)
    {
        var p = new Vector2(x + 0.5f, y + 0.5f);
        float w0 = ((screen[1].X - p.X) * (screen[2].Y - p.Y) - (screen[2].X - p.X) * (screen[1].Y - p.Y)) / area;
        float w1 = ((screen[2].X - p.X) * (screen[0].Y - p.Y) - (screen[0].X - p.X) * (screen[2].Y - p.Y)) / area;
        float w2 = 1f - w0 - w1;
        if (w0 < 0f || w1 < 0f || w2 < 0f) continue;

        Vector2 t = uv[0] * w0 + uv[1] * w1 + uv[2] * w2;
        int tx = Math.Clamp((int)(t.X * tex.W), 0, tex.W - 1);
        int ty = Math.Clamp((int)(t.Y * tex.H), 0, tex.H - 1);
        int to = (ty * tex.W + tx) * 4;
        if (to + 3 >= tex.Bgra.Length) continue;
        float a = tex.Bgra[to + 3] / 255f;
        if (a <= 0.002f) continue;
        PlotSketch(canvas, stripW, ox + x, oy + y, tint * a);
    }
}

static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';
static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
static string V(Vector3 value) => $"{F(value.X)}|{F(value.Y)}|{F(value.Z)}";
static bool Near(float a, float b) => float.IsFinite(a) && float.IsFinite(b) &&
                                      MathF.Abs(a - b) <= 1e-5f;
static bool Near2(float a, float b) => float.IsFinite(a) && float.IsFinite(b) &&
                                       MathF.Abs(a - b) <= 1e-3f;
static bool NearV(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
// q and -q are one rotation.
static bool NearQ(Vector4 a, Vector4 b) =>
    (Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z) && Near(a.W, b.W)) ||
    (Near(a.X, -b.X) && Near(a.Y, -b.Y) && Near(a.Z, -b.Z) && Near(a.W, -b.W));
static bool NearM(Matrix4x4 a, Matrix4x4 b) =>
    Near(a.M11, b.M11) && Near(a.M12, b.M12) && Near(a.M13, b.M13) &&
    Near(a.M21, b.M21) && Near(a.M22, b.M22) && Near(a.M23, b.M23) &&
    Near(a.M31, b.M31) && Near(a.M32, b.M32) && Near(a.M33, b.M33);

internal sealed record Row(string Model, string Uses, int Emitter, ushort ShapeId,
    string Shape, string Texture, byte Blend, uint Flags, Vector3 Position, ushort Bone,
    float Rate, float Life, float Population, float Speed, float SpeedVariation,
    float AreaLength, float AreaWidth, float VerticalRange, float HorizontalRange,
    float Gravity, float ZSource, float Drag, float Spin, bool BoneSpin, bool BoneMotion,
    Vector3 AngularMin, Vector3 AngularMax, string GeometryModel, string RecursionModel,
    byte HeadTail, float MidPoint, int EnableKeys, int AnimatedScalarTracks);
