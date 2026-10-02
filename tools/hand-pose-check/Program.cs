using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Formats;
using MSUIClient.World.Units;

if (args.Length != 3 || args[0] is not ("--census" or "--verify" or "--baseline"))
{
    Console.Error.WriteLine("usage: hand-pose-check --census|--baseline|--verify <client Data directory> <NEW report.json>");
    return 2;
}
string dataRoot = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
if (File.Exists(output)) throw new InvalidOperationException("Refusing to overwrite evidence.");
using var mpq = new MpqMount(dataRoot);
int checks = 0;
void Check(bool pass, string why) { checks++; if (!pass) throw new InvalidOperationException(why); }
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
object FileBinding(string file) => new { path = Path.GetFullPath(file), sha256 = Hash(File.ReadAllBytes(file)) };
var animationData = mpq.ReadFileWithSupplier(@"DBFilesClient\AnimationData.dbc") ?? throw new Exception("AnimationData missing");
var dbc = DbcFile.Parse(animationData.Data) ?? throw new Exception("Invalid AnimationData");
var animationNames = Enumerable.Range(0, dbc.RecordCount).ToDictionary(i => (int)dbc.GetUInt(i, 0), i => dbc.GetString(i, 1));
Check(animationNames[15] == "HandsClosed", "AnimationData15 is not HandsClosed");
int[] ids = [0, 15, 17, 18, 19, 26, 27, 28, 29, 37, 38, 39, 40, 107, 111];
string[] races = ["Human", "Orc", "Dwarf", "NightElf", "Scourge", "Tauren", "Gnome", "Troll"];
string[] sexes = ["Male", "Female"];
var bodies = new List<object>();
var regression = args[0] == "--census" ? null : new HandPoseRegression(args[0] == "--verify");
var errors = new List<string>();
foreach (string race in races) foreach (string sex in sexes)
{
    string path = $@"Character\{race}\{sex}\{race}{sex}.m2";
    var member = mpq.ReadFileWithSupplier(path) ?? throw new Exception("Missing " + path);
    var model = M2Reader.Parse(member.Data) ?? throw new Exception("Invalid " + path);
    var animator = M2Animator.Build(model, ids, includeStaticSequences: true) ?? throw new Exception("No skeleton " + path);
    var closed = animator.Find(15) ?? throw new Exception("Missing HandsClosed " + path);
    try { regression?.RunBody(model, animator, path, animationNames); }
    catch (Exception ex) { errors.Add(path + ": " + ex.Message); }
    var keys = Enumerable.Range(8, 10).Select(key => new { key, bone = key < model.KeyBoneLookup.Count ? (int)model.KeyBoneLookup[key] : -1 }).ToArray();
    Check(keys.Take(5).Any(k => k.bone >= 0 && k.bone < model.Bones.Count) && keys.Skip(5).Any(k => k.bone >= 0 && k.bone < model.Bones.Count), path + " no authored roots for one hand");
    bool Descendant(int bone, int parent)
    {
        var seen = new HashSet<int>();
        for (int b = bone; b >= 0 && b < model.Bones.Count && seen.Add(b); b = model.Bones[b].ParentBone)
            if (b == parent) return true;
        return false;
    }
    var right = Enumerable.Range(0, model.Bones.Count).Where(b => keys.Take(5).Any(k => Descendant(b, k.bone))).ToArray();
    var left = Enumerable.Range(0, model.Bones.Count).Where(b => keys.Skip(5).Any(k => Descendant(b, k.bone))).ToArray();
    Check(!right.Intersect(left).Any(), path + " overlapping hand trees");
    var keyed = Enumerable.Range(0, model.Bones.Count).Where(b => closed.Bones[b].TranslationKeys.Length + closed.Bones[b].RotationKeys.Length + closed.Bones[b].ScaleKeys.Length > 0).ToArray();
    var clips = ids.Select(id =>
    {
        var clip = animator.Find(id);
        return new { id, name = animationNames.GetValueOrDefault(id), present = clip is not null,
            sequenceIndex = clip?.SequenceIndex, durationSeconds = clip?.DurationSeconds,
            bones = clip is null ? null : Enumerable.Range(0, model.Bones.Count).Where(b => right.Contains(b) || left.Contains(b)).Select(b =>
            {
                var c = clip.Bones[b];
                return new { bone = b, keyBone = model.Bones[b].KeyBoneId, parent = model.Bones[b].ParentBone,
                    translation = c.TranslationKeys.Length, rotation = c.RotationKeys.Length, scale = c.ScaleKeys.Length,
                    firstRotation = c.RotationKeys.Length == 0 ? null : new float[] { c.RotationKeys[0].X, c.RotationKeys[0].Y, c.RotationKeys[0].Z, c.RotationKeys[0].W },
                    rotationSpread = c.RotationKeys.Length == 0 ? 0 : c.RotationKeys.Max(q => 1 - MathF.Abs(Quaternion.Dot(c.RotationKeys[0], q))) };
            }).ToArray() };
    }).ToArray();
    bodies.Add(new { race, sex, path, supplier = member.Supplier, sha256 = Hash(member.Data), boneCount = model.Bones.Count,
        fingerKeyBones = keys, rightFingerBones = right, leftFingerBones = left,
        handsClosedKeyedBones = keyed, handsClosedOutsideFingerBones = keyed.Except(right).Except(left).ToArray(),
        handAttachments = model.Attachments.Where(a => a.Id is 1 or 2 or 0).Select(a => new { a.Id, a.BoneIndex }).ToArray(),
        closedSequence = model.Sequences[closed.SequenceIndex],
        rawFingerTracks = right.Concat(left).Select(b => new { bone = b,
            translation = Track(model.Bones[b].Translation, closed.SequenceIndex, model.Sequences[closed.SequenceIndex]),
            rotation = Track(model.Bones[b].Rotation, closed.SequenceIndex, model.Sequences[closed.SequenceIndex]),
            scale = Track(model.Bones[b].Scale, closed.SequenceIndex, model.Sequences[closed.SequenceIndex]) }).ToArray(), clips });
    Console.WriteLine($"[hand-census] {race}{sex}: HandsClosed {closed.DurationSeconds}s; right={right.Length} left={left.Length}; keyed={keyed.Length}; outside={keyed.Except(right).Except(left).Count()}");
}
try { if (args[0] == "--verify") regression?.Synthetic(); }
catch (Exception ex) { errors.Add("synthetic: " + ex.Message); }
try { if (args[0] == "--verify") regression?.PresentedAnimation(animationData.Data); }
catch (Exception ex) { errors.Add("presented-animation: " + (ex.InnerException?.Message ?? ex.Message)); }
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
    JsonSerializer.Serialize(file, new { schemaVersion = 1, mode = args[0], passed = errors.Count == 0, checks, errors,
        clientAssembly = FileBinding(typeof(M2Animator).Assembly.Location), toolAssembly = FileBinding(typeof(Program).Assembly.Location),
        animationData = new { path = @"DBFilesClient\AnimationData.dbc", supplier = animationData.Supplier, sha256 = Hash(animationData.Data) },
        bodies, regressionChecks = regression?.Checks, regressionCases = regression?.Cases, baselinePoses = regression?.BaselinePoses,
        limitation = "Read-only offline data census and optional numerical regression; no rendered or live gameplay proof." }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"{(errors.Count == 0 ? "PASS" : "FAIL")} {checks} census checks, {regression?.Checks ?? 0} regression checks, {bodies.Count} bodies -> {output}");
foreach (string error in errors) Console.Error.WriteLine(error);
return errors.Count == 0 ? 0 : 1;

object Track<T>(M2AnimTrack<T> track, int slot, M2Sequence seq) where T : struct
{
    int lo = 0, hi = Math.Min(track.Keys.Count, track.Timestamps.Count) - 1;
    if (slot < track.Ranges.Count)
    {
        var r = track.Ranges[slot];
        if (r.Start <= r.End && r.End <= hi) { lo = (int)r.Start; hi = (int)r.End; }
    }
    int before = -1, after = -1;
    for (int i = lo; i <= hi; i++)
    {
        if (track.Timestamps[i] <= seq.StartTimestamp) before = i;
        if (after < 0 && track.Timestamps[i] >= seq.StartTimestamp) after = i;
    }
    object? Key(int index) => index < 0 ? null : new { index, time = track.Timestamps[index], value = JsonSerializer.SerializeToElement(track.Keys[index], new JsonSerializerOptions { IncludeFields = true }) };
    return new { track.InterpolationType, track.GlobalSequence, rangeStart = lo, rangeEnd = hi, totalKeys = track.Keys.Count,
        inBand = Enumerable.Range(Math.Max(0,lo), Math.Max(0,hi-lo+1)).Count(i => track.Timestamps[i] >= seq.StartTimestamp && track.Timestamps[i] <= seq.EndTimestamp), before = Key(before), after = Key(after) };
}
