using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

internal static class SpellChainBeamClinicalChecks
{
    public static void Run()
    {
        const ulong caster = 0x1122334455667788UL;
        const ulong first = 0x0102030405060708UL;
        const ulong second = 0x8877665544332211UL;
        var writer = new PacketWriter();
        writer.WriteU64(caster);
        writer.WriteU32(689);
        writer.WriteU32(2);
        writer.WriteU64(first);
        writer.WriteU64(second);
        byte[] body = writer.ToArray();
        Check(Convert.ToHexString(body) ==
              "8877665544332211B10200000200000008070605040302011122334455667788",
            "chain-target packet golden bytes drift");
        SpellChainTargetsPacket packet = SpellLifecyclePacketParser.ParseChainTargets(body);
        Check(packet.Caster == caster && packet.SpellId == 689 &&
              packet.Targets.SequenceEqual([first, second]),
            "raw-guid chain-target wire order drift");
        ExpectInvalid(() => SpellLifecyclePacketParser.ParseChainTargets(body[..^1]));
        ExpectInvalid(() => SpellLifecyclePacketParser.ParseChainTargets([.. body, 0]));

        Check(SpellVisualCatalog.CharProcSmallInt(0f) == 0 &&
              SpellVisualCatalog.CharProcSmallInt(1f) == 1 &&
              SpellVisualCatalog.CharProcSmallInt(3f) == 3,
            "CharProc small-int bit decode drift");
        Check(SpellChainBeamLaw.SubdivisionCount(1f, 2.78f) == 2 &&
              SpellChainBeamLaw.SubdivisionCount(30f, 2.78f) == 12,
            "chain-beam subdivision +2 floor drift");
        uint random = 1;
        var fresh = new List<Vector3>();
        SpellChainBeamLaw.FreshPolyline(Vector3.Zero, new Vector3(10, 0, 0), .04f,
            ref random, fresh, 2.78f);
        Check(fresh.Count == 6 && fresh[0] == Vector3.Zero &&
              fresh[^1] == new Vector3(10, 0, 0),
            "chain-beam endpoints or subdivision geometry drift");
        var live = fresh.Select(p => p + Vector3.One).ToList();
        SpellChainBeamLaw.Advect(live, fresh);
        Check(live[0] == fresh[0] && live[^1] == fresh[^1] &&
              Vector3.DistanceSquared(live[1], fresh[1] + Vector3.One * .75f) < .000001f,
            "chain-beam 0.75/0.25 advection drift");

        CheckActualDataAndLifecycle();
        CheckRuntimeWiring();
    }

    private static void CheckActualDataAndLifecycle()
    {
        string root = ClientConfig.FindRepoRoot();
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        SpellCatalog spells = SpellCatalog.Load(mpq) ??
            throw new InvalidDataException("Spell.dbc unavailable");
        SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq) ??
            throw new InvalidDataException("SpellVisual chain tables unavailable");
        Check(visuals.ChainEffectCount == 18 &&
              visuals.TryGetChainEffect(1, out SpellChainEffectInfo lightning) &&
              lightning.Texture == @"Textures\SpellChainEffects\Lightning.blp" &&
              Near(lightning.AverageSegmentLength, 2.78f) &&
              lightning.BoltStaggerMs == 300,
            "actual SpellChainEffects table/canonical lightning row drift");

        SpellInfo chainSpell = spells.TryGet(421, out SpellInfo chain) ? chain
            : throw new InvalidDataException("Chain Lightning fixture spell 421 unavailable");
        Check(visuals.TryGetStageKit(chainSpell.VisualId, SpellStage.Cast,
                  out SpellVisualKitInfo castKit, out _) &&
              SpellVisualCatalog.TryGetChainProc(castKit, out SpellChainProcInfo castProc) &&
              castProc.EffectId == 1 && !castProc.Persistent,
            "actual Chain Lightning cast kit lost its type-12 beam proc");

        var source = new SpellChainBeamSource(visuals);
        const ulong caster = 10, one = 20, two = 30, three = 40;
        source.StoreHops(caster, [caster, one, two, three]);
        Check(source.Play(caster, chainSpell.Id, chainSpell.VisualId, castKit,
                  0, null, 10) && source.PendingCasterCount == 0,
            "GO-derived hop list was not consumed by the cast chain proc");
        SpellChainBeamInstance beam = source.Snapshot(10).Single();
        Check(beam.Targets.SequenceEqual([one, two, three]) &&
              SpellChainBeamLaw.HopVisible(beam, 0, 10) &&
              !SpellChainBeamLaw.HopVisible(beam, 1, 10) &&
              SpellChainBeamLaw.HopVisible(beam, 1, 10.31),
            "chain ordering, caster filtering, or per-hop stagger drift");
        Check(source.Snapshot(beam.Expires).Count == 0,
            "one-shot chain beam did not expire at hopCount * BoltLife");

        SpellInfo drainSpell = spells.TryGet(689, out SpellInfo drain) ? drain
            : throw new InvalidDataException("Drain Life fixture spell 689 unavailable");
        Check(visuals.TryGetStageKit(drainSpell.VisualId, SpellStage.Channel,
                  out SpellVisualKitInfo channelKit, out _) &&
              SpellVisualCatalog.TryGetChainProc(channelKit, out SpellChainProcInfo channelProc) &&
              channelProc.Persistent,
            "actual Drain Life channel kit lost its type-0 persistent beam proc");
        source.StoreHops(caster, []);
        Check(source.Play(caster, drainSpell.Id, drainSpell.VisualId, channelKit,
                  drainSpell.Id, one, 20) &&
              source.Snapshot(200).Single().Targets.SequenceEqual([one]),
            "channel-object single-target selection or persistent lifetime drift");
        source.Reap(caster, drainSpell.Id);
        Check(source.Snapshot(200).Count == 0,
            "channel teardown did not reap its persistent beam");
    }

    private static void CheckRuntimeWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string casting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Casting.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(net.Contains("case Op.SMSG_SPELL_UPDATE_CHAIN_TARGETS:", StringComparison.Ordinal) &&
              net.Contains("ParseChainTargets(body)", StringComparison.Ordinal) &&
              casting.Contains("StoreHops(packet.Caster, packet.Hits)", StringComparison.Ordinal) &&
              casting.Contains("ApplySpellChainTargets", StringComparison.Ordinal) &&
              casting.Contains("_spellChainBeams?.Play", StringComparison.Ordinal) &&
              program.Contains("_spellChainBeamRenderer.Render", StringComparison.Ordinal),
            "chain-target packet, GO producer, lifecycle, or world-render wiring drift");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (EndOfStreamException) { return; }
        throw new InvalidDataException("malformed chain-target packet was accepted");
    }

    private static bool Near(float left, float right) => MathF.Abs(left - right) < .0001f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
