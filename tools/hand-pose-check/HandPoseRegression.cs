using System.Numerics;
using System.Reflection;
using System.Collections;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using MSUIClient.Formats;
using MSUIClient.World.Units;

// The oracle samples the original M2 rotation arrays, not HandGripPose, baked HandsClosed,
// or the implementation's mask. Baseline local transforms come from the ordinary evaluator.
sealed class HandPoseRegression
{
    public int Checks { get; private set; }
    public List<object> Cases { get; } = [];
    public List<object> BaselinePoses { get; } = [];
    readonly bool _verify;
    readonly Type _grip;
    readonly bool _legacy;
    readonly MethodInfo _plain, _blend, _overlays;
    public HandPoseRegression(bool verify)
    {
        _verify = verify;
        var grip = typeof(M2Animator).Assembly.GetType("MSUIClient.World.Units.HandGrip");
        _legacy = grip is null; _grip = grip ?? typeof(int);
        MethodInfo Find(string name, int count) => typeof(M2Animator).GetMethods().Single(m => m.Name == name && m.GetParameters().Length == count - (_legacy ? 1 : 0));
        _plain = Find("Evaluate", 5); _blend = Find("Evaluate", 8); _overlays = Find("EvaluateWithArmOverlays", 19);
    }
    void Check(bool ok, string why) { Checks++; if (!ok) throw new InvalidOperationException(why); }
    Matrix4x4[] Evaluate(M2Animator a, M2Animator.Clip? c, float t, int hand, int mode = 0)
    {
        var skin = new Matrix4x4[a.BoneCount]; object flag = _legacy ? hand : Enum.ToObject(_grip, hand);
        object?[] values = mode == 0 ? [c, t, 3.125f, skin, flag] : mode == 1
            ? [c, t, a.Find(0), .2f, .35f, 3.125f, skin, flag]
            : [c, t, a.Find(0), .2f, .2f, a.Find(27), .1f, a.Find(28), .2f, a.Find(38), .15f, 3.125f, skin, a.Find(26), .05f, .25f, true, .75f, flag];
        if (_legacy) values = values[..^1];
        (mode == 0 ? _plain : mode == 1 ? _blend : _overlays).Invoke(a, values);
        return skin;
    }
    public void RunBody(M2Model m, M2Animator a, string label, IReadOnlyDictionary<int,string> names)
    {
        if (_verify && !_legacy) Occupancy(m, label);
        int tested = 0, changes = 0;
        var coverage = new List<object>();
        foreach (int id in new[] { 0, 4, 5, 17, 18, 19, 26, 27, 28, 29, 37, 38, 39, 40, 50, 51, 52, 53, 107, 111 })
        {
            var clip = a.FindOrBake(id, includeStaticSequences: true);
            coverage.Add(new { animationId=id, name=names.GetValueOrDefault(id), present=clip is not null, sequenceIndex=clip?.SequenceIndex });
            if (clip is null) continue;
            foreach (float phase in new[] { 0f, .37f, .81f }) foreach (int mode in new[] { 0, 1, 2 })
            {
                float t = clip.DurationSeconds * phase;
                var baseline = Evaluate(a, clip, t, 0, mode);
                using var bytes = new MemoryStream();
                using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
                    foreach (var matrix in baseline) foreach (float value in Values(matrix)) writer.Write(value);
                BaselinePoses.Add(new { body = label, animationId = id, phase, mode, sha256 = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant() });
                if (!_verify) continue;
                foreach (int hand in new[] { 1, 2, 3 })
                {
                    var actual = Evaluate(a, clip, t, hand, mode);
                    var mask = Mask(m, hand);
                    var expected = Oracle(m, baseline, mask);
                    Check(actual.Zip(expected).All(p => Error(p.First, p.Second) < 3e-5f), $"{label} id{id} mode{mode} phase{phase} hand{hand}: source-rotation oracle mismatch");
                    Check(Enumerable.Range(0, a.BoneCount).Where(i => !mask[i]).All(i => actual[i] == baseline[i]), $"{label}: closure changed non-finger bone");
                    foreach (var attachment in m.Attachments.Where(x => x.Id is 0 or 1 or 2))
                        Check(actual[attachment.BoneIndex] == baseline[attachment.BoneIndex], $"{label}: moved weapon attachment {attachment.Id}");
                    Check(actual.All(Finite), label + ": nonfinite skin");
                    changes += actual.Zip(baseline).Count(p => Error(p.First, p.Second) > 1e-5f);
                    tested++;
                }
                Check(Evaluate(a, clip, t, 0, mode).SequenceEqual(baseline), label + ": None changed after closure (state leaked)");
            }
        }
        if (_verify) Check(changes > 0, label + ": closure had no effect across all poses");
        Cases.Add(new { label, tested, changedBoneObservations = changes, coverage });
    }
    static bool[] Mask(M2Model m, int hand)
    {
        var roots = new HashSet<int>();
        foreach (int key in Enumerable.Range(8, 10))
        {
            if ((key < 13 ? (hand & 1) : (hand & 2)) == 0) continue;
            int b = key < m.KeyBoneLookup.Count ? m.KeyBoneLookup[key] : -1;
            if (b < 0 || b >= m.Bones.Count) b = m.Bones.FindIndex(x => x.KeyBoneId == key);
            if (b >= 0) roots.Add(b);
        }
        return Enumerable.Range(0, m.Bones.Count).Select(i =>
        {
            var seen = new HashSet<int>();
            for (int p = i; p >= 0 && p < m.Bones.Count && seen.Add(p); p = m.Bones[p].ParentBone)
                if (roots.Contains(p)) return true;
            return false;
        }).ToArray();
    }
    static Matrix4x4[] Oracle(M2Model m, Matrix4x4[] baseline, bool[] mask)
    {
        int slot = m.TryFindSequenceIndexByAnimationId(15);
        if (slot < 0) return baseline;
        var globals = baseline.Select((x, i) => Matrix4x4.CreateTranslation(m.Bones[i].Pivot) * x).ToArray();
        var updated = new Matrix4x4[globals.Length]; var done = new bool[globals.Length];
        Matrix4x4 Visit(int i)
        {
            if (done[i]) return updated[i];
            var bone = m.Bones[i]; int p = bone.ParentBone;
            var local = globals[i];
            if (p >= 0) { Matrix4x4.Invert(globals[p], out var inverse); local *= inverse; }
            if (mask[i] && RawRotation(bone.Rotation, slot, m.Sequences[slot].StartTimestamp) is { } rotation)
            {
                if (!Matrix4x4.Decompose(local, out var scale, out _, out var translation)) throw new Exception("Cannot decompose baseline local");
                local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            }
            updated[i] = p >= 0 ? local * Visit(p) : local; done[i] = true; return updated[i];
        }
        return Enumerable.Range(0, globals.Length).Select(i => Matrix4x4.CreateTranslation(-m.Bones[i].Pivot) * Visit(i)).ToArray();
    }
    // Benilla key_anim.rs161-210: k0 is bounded to the inclusive window; k1 is
    // the next authored key in the complete track. Clamp interpolation fraction.
    // Empty/global tracks do not override. Do not clip keys to the 33 ms band.
    static Quaternion? RawRotation(M2AnimTrack<Vector4> t, int slot, uint time)
    {
        int count = Math.Min(t.Keys.Count, t.Timestamps.Count);
        if (count == 0 || t.GlobalSequence >= 0) return null;
        int lo = 0, hi = count - 1;
        if (slot < t.Ranges.Count)
        {
            var r = t.Ranges[slot];
            lo = (int)Math.Min(r.Start, (uint)(count - 1)); hi = (int)Math.Min(r.End, (uint)(count - 1));
        }
        Quaternion Q(int i) { var v = t.Keys[i]; return Quaternion.Normalize(new(v.X, v.Y, v.Z, v.W)); }
        if (lo >= hi) return Q(lo);
        int first = Enumerable.Range(lo, hi - lo + 1).TakeWhile(i => t.Timestamps[i] <= time).DefaultIfEmpty(lo).Last();
        if (t.InterpolationType == 0 || first == count - 1 || t.Timestamps[first + 1] <= t.Timestamps[first]) return Q(first);
        float fraction = Math.Clamp(((float)time - t.Timestamps[first]) / ((float)t.Timestamps[first + 1] - t.Timestamps[first]), 0, 1);
        return Quaternion.Normalize(Quaternion.Slerp(Q(first), Q(first + 1), fraction));
    }
    public void Synthetic()
    {
        var type = typeof(M2Animator).Assembly.GetType("MSUIClient.World.Units.HandGripLaw") ?? throw new Exception("Missing HandGripLaw");
        var law = type.GetMethod("ForAttachment") ?? throw new Exception("Missing attachment law");
        foreach (int id in new[] { -1, 0, 1, 2, 3, 5, 6, 15, int.MaxValue })
            Check(Convert.ToInt32(law.Invoke(null, [id])) == (id == 1 ? 1 : id == 2 ? 2 : 0), "Attachment occupancy law mismatch");
        foreach (bool step in new[] { false, true })
        {
            var m = Fixture(step); var a = M2Animator.Build(m, [0, 15], true)!;
            foreach (int hand in new[] { 1, 2, 3 })
            {
                var baseline = Evaluate(a, a.Find(0), .4f, 0);
                var actual = Evaluate(a, a.Find(0), .4f, hand);
                var expected = Oracle(m, baseline, Mask(m, hand));
                Check(actual.Zip(expected).All(p => Error(p.First, p.Second) < 1e-5f), "Synthetic inclusive bracket/rotation-only preservation failed");
            }
            // Hand closure must not leak from another clip when HandsClosed is absent.
            m.Sequences.RemoveAt(1); a = M2Animator.Build(m, [0], true)!;
            Check(Evaluate(a, a.Find(0), .4f, 0).SequenceEqual(Evaluate(a, a.Find(0), .4f, 3)), "Missing HandsClosed mutated pose");
        }
        var missing = Fixture(false); missing.KeyBoneLookup = Enumerable.Repeat((short)-1, 18).ToList();
        foreach (var b in missing.Bones) b.KeyBoneId = -1;
        var noRoots = M2Animator.Build(missing, [0, 15], true)!;
        Check(Evaluate(noRoots, noRoots.Find(0), .4f, 0).SequenceEqual(Evaluate(noRoots, noRoots.Find(0), .4f, 3)), "Missing roots mutated pose");
        var nextKey = Fixture(false);
        foreach (var b in nextKey.Bones) { b.Rotation.Ranges[1] = new() { Start = 1, End = 2 }; b.Rotation.Timestamps[2] = 1750; }
        var nextAnimator = M2Animator.Build(nextKey, [0, 15], true)!;
        var nextBaseline = Evaluate(nextAnimator, nextAnimator.Find(0), .4f, 0);
        var nextActual = Evaluate(nextAnimator, nextAnimator.Find(0), .4f, 3);
        Check(nextActual.Zip(Oracle(nextKey, nextBaseline, Mask(nextKey, 3))).All(p => Error(p.First, p.Second) < 1e-5f), "Reference next-key beyond inclusive k0 window changed");
        foreach (bool global in new[] { false, true })
        {
            var absentRotation = Fixture(false);
            foreach (int bone in new[] { 2, 3, 5, 6 })
                absentRotation.Bones[bone].Rotation = global
                    ? new() { GlobalSequence = 0, Timestamps = [0], Keys = [new(0,0,1,0)] }
                    : new();
            var emptyAnimator = M2Animator.Build(absentRotation, [0,15], true)!;
            Check(Evaluate(emptyAnimator,emptyAnimator.Find(0),.4f,0).SequenceEqual(Evaluate(emptyAnimator,emptyAnimator.Find(0),.4f,3)), "Missing/plain-global rotation synthesized closure");
        }
        var tagged = Fixture(false); tagged.KeyBoneLookup = [];
        tagged.Bones[2].KeyBoneId = 8; tagged.Bones[5].KeyBoneId = 13;
        var taggedAnimator = M2Animator.Build(tagged,[0,15],true)!;
        var taggedBase = Evaluate(taggedAnimator,taggedAnimator.Find(0),.4f,0);
        Check(Evaluate(taggedAnimator,taggedAnimator.Find(0),.4f,3).Zip(Oracle(tagged,taggedBase,Mask(tagged,3))).All(p=>Error(p.First,p.Second)<1e-5f),"Tagged key-bone fallback failed");
        SnapshotIsolation();
        Cases.Add(new { label = "synthetic", scope = "attachment0 shield exclusion; missing clip/roots; sparse bracketing inclusive range; step/linear; rotation only with conflicting authored translation/scale" });
    }
    /// <summary>
    /// HandGripLaw.ForPresentedAnimation: a palm the original client empties for a stow animation
    /// (AnimationData WeaponFlags 0x4, or 0x10 outside the drawn-ranged handling plays) keeps the
    /// animation's own fingers. Synthetic tables first, then the real mounted AnimationData.dbc.
    /// Reflection keeps the tool buildable against clients that predate the law.
    /// </summary>
    public void PresentedAnimation(byte[] animationData)
    {
        var assembly = typeof(M2Animator).Assembly;
        MethodInfo law = assembly.GetType("MSUIClient.World.Units.HandGripLaw")?.GetMethod("ForPresentedAnimation")
            ?? throw new InvalidOperationException("HandGripLaw.ForPresentedAnimation missing");
        MethodInfo parse = assembly.GetType("MSUIClient.Formats.AnimationDataCatalog")?.GetMethod("ParseWeaponFlags")
            ?? throw new InvalidOperationException("AnimationDataCatalog.ParseWeaponFlags missing");
        int Grip(int occupied, int animation, IReadOnlyDictionary<int, uint>? flags, byte sheath) =>
            Convert.ToInt32(law.Invoke(null, [Enum.ToObject(_grip, occupied), animation, flags, sheath]));
        var table = new Dictionary<int, uint> { [0] = 0, [17] = 0x20, [53] = 4, [60] = 0x10, [46] = 0x10, [131] = 0x14 };
        Check(Grip(1, 53, table, 1) == 0, "Stow-always cast kept a closed palm");
        Check(Grip(3, 53, table, 1) == 0, "Stow-always cast kept either palm closed");
        Check(Grip(3, 17, table, 1) == 3, "Draw-melee attack opened an occupied palm");
        Check(Grip(1, 0, table, 1) == 1, "Flagless stand opened an occupied palm");
        Check(Grip(1, 60, table, 1) == 0, "Stow emote kept a closed palm");
        Check(Grip(2, 46, table, 2) == 2, "Drawn-ranged bow shot opened the bow hand");
        Check(Grip(2, 46, table, 1) == 0, "Ranged exemption applied without a drawn ranged weapon");
        Check(Grip(1, 131, table, 2) == 0, "Stow-always bit lost when combined with 0x10");
        Check(Grip(1, 999, table, 1) == 1 && Grip(1, -1, table, 1) == 1, "Unknown animation changed occupancy");
        Check(Grip(1, 53, null, 1) == 1, "Missing table changed occupancy");
        Check(Grip(0, 17, table, 1) == 0, "Empty palms closed");
        var real = (IReadOnlyDictionary<int, uint>)parse.Invoke(null, [animationData])!;
        Check(real.Count == 208, $"Real AnimationData rows {real.Count}");
        Check(real[53] == 4 && real[51] == 4 && real[54] == 4 && real[124] == 4 && real[42] == 4 && real[50] == 4 && real[91] == 4,
            "Real casts/swim/loot/mount are not stow-always");
        Check(real[17] == 0x20 && real[18] == 0x20 && real[26] == 0x20 && real[133] == 0x20, "Real armed attacks/ready/fishing are not draw-melee");
        Check(real[0] == 0 && real[4] == 0 && real[5] == 0 && real[15] == 0, "Real stand/walk/run/HandsClosed carry flags");
        Check(real[60] == 0x10 && real[16] == 0x10 && real[46] == 0x10 && real[137] == 0 && real[136] == 0x20,
            "Real emote/unarmed/bow or NoSheathe variants changed");
        Check(Grip(1, 53, real, 1) == 0 && Grip(1, 17, real, 1) == 1 && Grip(2, 46, real, 2) == 2 && Grip(1, 137, real, 1) == 1,
            "Real-table grip decisions");
        Cases.Add(new { label = "presented-animation-law", checks = 17, scope = "Synthetic WeaponFlags tables plus the mounted AnimationData.dbc: stow-always 0x4, stow 0x10 with the drawn-ranged handling exemption, draw 0x20 and flagless animations keep palm occupancy; unknown ids and a missing table keep occupancy." });
    }

    void SnapshotIsolation()
    {
        Type renderer = typeof(M2Animator).Assembly.GetType("MSUIClient.World.Units.AttachedItemRenderer")!;
        Type mountType = renderer.GetNestedType("Mount",BindingFlags.NonPublic)!;
        Type modelType = renderer.GetNestedType("ItemModel",BindingFlags.NonPublic)!;
        Type setType = renderer.GetNestedType("MountSet",BindingFlags.Public)!;
        // Only the pure snapshot/visibility seam is called; never the GL constructor/render/dispose.
        object owner = RuntimeHelpers.GetUninitializedObject(renderer);
        var listField = renderer.GetField("_mounts",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var liveList = (IList)Activator.CreateInstance(listField.FieldType)!; listField.SetValue(owner,liveList);
        var liveErrors = new List<string> { "retained-first" };
        renderer.GetField("_resolutionErrors",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,liveErrors);
        object mount = Activator.CreateInstance(mountType)!, resource = Activator.CreateInstance(modelType)!;
        void Set(string field, object value) => mountType.GetField(field)!.SetValue(mount,value);
        Set("Label","snapshot-test"); Set("Model",resource); Set("HeldSlot",0); Set("InventoryType",13); Set("ItemSheath",(byte)3); Set("Visible",true);
        liveList.Add(mount);
        var snapshot = renderer.GetMethod("SnapshotMountSet")!;
        var visible = renderer.GetMethod("SetMountVisible")!;
        var resolve = renderer.GetMethods().Single(x=>x.Name=="ResolveHandGrip"&&x.IsStatic&&x.IsPublic);
        object Snapshot() => snapshot.Invoke(owner,null)!;
        IList Items(object set) => (IList)setType.GetField("Items",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(set)!;
        IList Errors(object set) => (IList)setType.GetField("Errors",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(set)!;
        int Hands(object set) => Convert.ToInt32(resolve.Invoke(null,[Fixture(false),set,(byte)1]));
        object old = Snapshot(); object oldMount = Items(old)[0]!;
        Check(ReferenceEquals(old,Snapshot()),"Unchanged snapshots allocate new presentation");
        Check(!ReferenceEquals(mount,oldMount),"Snapshot aliases mutable mount entry");
        Check(ReferenceEquals(mountType.GetField("Model")!.GetValue(oldMount),resource),"Snapshot duplicated/shared resource identity changed");
        // Drawn clones must map back to their built mount (the equipment mesh probe relies on it).
        var origin = mountType.GetField("SnapshotOrigin",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Check(origin.GetValue(mount) is null && ReferenceEquals(origin.GetValue(oldMount),mount),"Snapshot clone lost its built-mount origin");
        object again = mountType.GetMethod("Snapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(oldMount,null)!;
        Check(ReferenceEquals(origin.GetValue(again),mount),"Snapshot of a snapshot changed its built-mount origin");
        Check(Hands(old)==1,"Snapshot initial right hand");
        liveErrors.Add("later-error"); visible.Invoke(owner,["snapshot-test",false]);
        object hidden=Snapshot();
        Check(!ReferenceEquals(old,hidden),"Visibility change failed snapshot invalidation");
        Check(Hands(old)==1 && Hands(hidden)==0,"Visibility refresh changed frozen hand presentation");
        Check((bool)mountType.GetField("Visible")!.GetValue(oldMount)! && !(bool)mountType.GetField("Visible")!.GetValue(Items(hidden)[0]!)!,"Frozen clone visibility mutated");
        Check(Errors(old).Count==1 && Errors(hidden).Count==2,"Snapshot error list aliases live list");
        Set("HeldSlot",1); Set("AttachmentId",2); visible.Invoke(owner,["snapshot-test",true]);
        object changed=Snapshot();
        Check(Hands(old)==1 && Hands(changed)==2,"Refreshed mount entry changed retained grip snapshot");
        Check(ReferenceEquals(changed,Snapshot()),"Refreshed snapshot not cached");
        Cases.Add(new { label="snapshot-isolation", checks=12, scope="Pure SnapshotMountSet/SetMountVisible on uninitialized renderer, no GL constructor. Old entry/visibility/error list remain retained while new mount presentation updates; every clone maps to its built mount. Rebuild/render integration is separate native/source proof." });
    }
    void Occupancy(M2Model character, string label)
    {
        Type renderer = typeof(M2Animator).Assembly.GetType("MSUIClient.World.Units.AttachedItemRenderer")!;
        Type setType = renderer.GetNestedType("MountSet", BindingFlags.Public)!;
        Type mountType = renderer.GetNestedType("Mount", BindingFlags.NonPublic)!;
        MethodInfo resolve = renderer.GetMethods().Single(x => x.Name == "ResolveHandGrip" && x.IsStatic && x.IsPublic);
        object Mount(int slot, int inventory, int attachment = 1, bool visible = true)
        {
            var mount = Activator.CreateInstance(mountType)!;
            foreach (var entry in new Dictionary<string,object> { ["HeldSlot"]=slot, ["InventoryType"]=inventory, ["AttachmentId"]=attachment, ["Visible"]=visible, ["ItemSheath"]=(byte)3 })
                mountType.GetField(entry.Key)!.SetValue(mount,entry.Value);
            return mount;
        }
        int Resolve(byte sheath, params object[] mounts)
        {
            var set = Activator.CreateInstance(setType)!;
            var list = (IList)setType.GetField("Items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(set)!;
            foreach (var m in mounts) list.Add(m);
            return Convert.ToInt32(resolve.Invoke(null,[character,set,sheath]));
        }
        Check(Resolve(1) == 0, label+": empty hands");
        Check(Resolve(1,Mount(0,13)) == 1, label+": drawn mainhand");
        Check(Resolve(0,Mount(0,13)) == 0, label+": stowed mainhand");
        Check(Resolve(1,Mount(1,13,2)) == 2, label+": drawn offhand");
        Check(Resolve(1,Mount(0,13),Mount(1,13,2)) == 3, label+": dual wield");
        Check(Resolve(1,Mount(1,14,0)) == 0, label+": shield wrist incorrectly closes fingers");
        Check(Resolve(1,Mount(0,13),Mount(1,14,0)) == 1, label+": sword and shield");
        Check(Resolve(1,Mount(0,17)) == 1, label+": two-hand main mount incorrectly synthesizes offhand");
        Check(Resolve(2,Mount(2,15)) == 2, label+": bow left hand");
        Check(Resolve(2,Mount(2,26)) == 1, label+": shooting wand right hand");
        Check(Resolve(0,Mount(2,26)) == 0 && Resolve(1,Mount(2,26)) == 0, label+": hidden wand closes fingers");
        Check(Resolve(1,Mount(0,13,1,false)) == 0, label+": hidden mount closes fingers");
        Check(Resolve(1,Mount(-1,0,11),Mount(-1,0,5)) == 0, label+": helm/shoulder closes fingers");
        Check(Convert.ToInt32(resolve.Invoke(null,[character,null,(byte)1])) == 0, label+": null mounts");
        Check(Convert.ToInt32(resolve.Invoke(null,[null,Activator.CreateInstance(setType),(byte)1])) == 0, label+": null character");
        var invalid = new M2Model { Bones = character.Bones, Attachments = [new() { Id = 1, BoneIndex = (uint)character.Bones.Count }] };
        var badSet = Activator.CreateInstance(setType)!;
        ((IList)setType.GetField("Items",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(badSet)!).Add(Mount(0,13));
        Check(Convert.ToInt32(resolve.Invoke(null,[invalid,badSet,(byte)1])) == 0,label+": out-of-range attachment closes hand");
        invalid.Attachments.Clear();
        Check(Convert.ToInt32(resolve.Invoke(null,[invalid,badSet,(byte)1])) == 0,label+": missing attachment closes hand");
    }
    static M2Model Fixture(bool step)
    {
        var m = new M2Model { KeyBoneLookup = Enumerable.Repeat((short)-1, 18).ToList(), Sequences = [
            new M2Sequence { AnimationId = 0, StartTimestamp = 0, EndTimestamp = 1000 },
            new M2Sequence { AnimationId = 15, StartTimestamp = 2000, EndTimestamp = 2033 }] };
        short[] parents = [-1, 0, 1, 2, 0, 4, 5];
        for (int i = 0; i < parents.Length; i++)
        {
            var b = new M2Bone { ParentBone = parents[i], KeyBoneId = -1, Pivot = new Vector3(i * .1f, i * .3f, 0) };
            b.Rotation = new() { InterpolationType = (ushort)(step ? 0 : 1), Ranges = [new() { Start=0, End=1 }, new() { Start=2, End=3 }], Timestamps = [0,1000,1500,2500],
                Keys = [new(0,0,0,1),new(0,0,0,1),new(0,0,0,1),new(0,0,1,0)] };
            b.Translation = new() { InterpolationType = 1, Ranges = [new() { Start=0, End=1 },new() { Start=2, End=3 }], Timestamps = [0,1000,1500,2500], Keys = [new(.01f,0,0),new(.01f,0,0),new(99,88,77),new(99,88,77)] };
            b.Scale = new() { InterpolationType = 1, Ranges = [new() { Start=0, End=1 },new() { Start=2, End=3 }], Timestamps = [0,1000,1500,2500], Keys = [Vector3.One,Vector3.One,new(9,8,7),new(9,8,7)] };
            m.Bones.Add(b);
        }
        m.KeyBoneLookup[8] = 2; m.KeyBoneLookup[13] = 5;
        m.Attachments = [new() { Id = 1, BoneIndex = 1 }, new() { Id = 2, BoneIndex = 4 }];
        return m;
    }
    static bool Finite(Matrix4x4 m) => Values(m).All(float.IsFinite);
    static float Error(Matrix4x4 a, Matrix4x4 b) => Values(a).Zip(Values(b)).Max(p => MathF.Abs(p.First - p.Second));
    static float[] Values(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
}
