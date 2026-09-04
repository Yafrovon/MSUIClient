using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

internal static class AuraVisualClinicalChecks
{
    public static void Run()
    {
        AuraBodyNode[] decoded = AuraVisualLaw.Nodes(
        [
            new SpellVisualCharProc(14, [.3f, 2, 1000, 0]),
            new SpellVisualCharProc(1, [9222653f, 0, 0, 0]),
            new SpellVisualCharProc(11, [0f, 0, 0, 0]),
            new SpellVisualCharProc(9, [2f, 3f, 0, 0]),
        ]);
        Check(decoded.Length == 3 &&
              decoded[0] == new AuraBodyNode(AuraBodyNodeKind.Alpha, .3f, Vector3.One) &&
              decoded[1].Kind == AuraBodyNodeKind.Tint &&
              Near(decoded[1].Tint, new Vector3(0x8c, 0xb9, 0xfd) / 255f) &&
              decoded[2] == new AuraBodyNode(AuraBodyNodeKind.AnimationRate, 0f, Vector3.One),
            "state-kit CharProc 14/1/11 dispatch or packed-RGB tint drift");

        var state = new AuraVisualState();
        AuraBodySpell stealth = new(1784,
        [
            new AuraBodyNode(AuraBodyNodeKind.Alpha, .3f, Vector3.One),
            new AuraBodyNode(AuraBodyNodeKind.Tint, 0f, new Vector3(.2f, .4f, .8f)),
            new AuraBodyNode(AuraBodyNodeKind.AnimationRate, 0f, Vector3.One),
        ]);
        state.Reconcile(1f, [stealth], 0);
        Check(Near(state.Alpha, 1f) && Near(state.TargetAlpha, .3f) && state.Frozen &&
              Near(state.Tint, new Vector3(.2f, .4f, .8f)),
            "aura body node arm/head projection drift");
        state.Tick(.5);
        Check(Near(state.Alpha, .9125f), "aura alpha is not the one-second cubic ramp");
        state.Tick(1);
        Check(Near(state.Alpha, .3f) && state.Translucent,
            "aura alpha did not settle at the proc-14 target");

        AuraBodySpell newer = new(999,
            [new AuraBodyNode(AuraBodyNodeKind.Alpha, .5f, Vector3.One)]);
        state.Reconcile(1f, [stealth, newer], 1);
        state.Tick(2);
        Check(Near(state.Alpha, .5f), "newest alpha node does not win at the list head");
        state.Reconcile(1f, [stealth], 2);
        state.Tick(3);
        Check(Near(state.Alpha, .3f), "reap did not reveal the next alpha node");
        state.Reconcile(1f, [], 3);
        state.Tick(4);
        Check(Near(state.Alpha, 1f) && !state.Frozen && state.Tint == Vector3.One,
            "final aura reap did not restore opaque/identity/unfrozen body state");

        var authored = new AuraVisualState();
        authored.Reconcile(102f / 255f, [], 0);
        Check(Near(authored.Alpha, 1f) && Near(authored.TargetAlpha, 102f / 255f),
            "display-only CreatureModelAlpha did not begin at opaque and retarget");
        authored.Tick(1);
        Check(Near(authored.Alpha, 102f / 255f),
            "display-only CreatureModelAlpha did not complete its one-second ramp");

        var combined = new AuraVisualState();
        combined.Reconcile(.4f,
            [new AuraBodySpell(1,
                [new AuraBodyNode(AuraBodyNodeKind.Alpha, .5f, Vector3.One)])], 0);
        combined.Tick(1);
        Check(Near(combined.Alpha, .2f),
            "effective alpha is not baseAlpha times one head-node factor");

        string root = ClientConfig.FindRepoRoot();
        string casting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Casting.cs"));
        string creature = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        string mount = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.Mounts.cs"));
        string character = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        string attached = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "AttachedItemRenderer.cs"));
        string characterFrag = SourceText.Read(Path.Combine(root, "MSUIClient", "Shaders",
            "character.frag"));
        Check(casting.Contains("AuraVisualLaw.Nodes", StringComparison.Ordinal) &&
              casting.Contains("unit.AuraVisual.Reconcile", StringComparison.Ordinal) &&
              creature.Contains("e.AuraVisual.Frozen", StringComparison.Ordinal) &&
              creature.Contains("uBodyAlpha", StringComparison.Ordinal) &&
              mount.Contains("freezeAnimation", StringComparison.Ordinal) &&
              character.Contains("state.ApplyBodyVisual", StringComparison.Ordinal) &&
              attached.Contains("BodyTint", StringComparison.Ordinal) &&
              characterFrag.Contains("albedo.a * uBodyAlpha", StringComparison.Ordinal),
            "unit body/gear/mount alpha, tint, or animation-rate runtime wiring drift");

        CheckActualDataIfPresent(root);
    }

    private static void CheckActualDataIfPresent(string root)
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        CreatureDisplayInfoTable displays = CreatureDisplayInfoTable.Parse(
            mpq.ReadFile(CreatureDisplayInfoTable.MpqPath) ?? []) ??
            throw new InvalidDataException("CreatureDisplayInfo.dbc unavailable");
        Check(displays.Find(4613)?.ModelAlpha == 102,
            "actual Ghost Wolf display 4613 lost CreatureModelAlpha 102");

        SpellCatalog spells = SpellCatalog.Load(mpq) ??
            throw new InvalidDataException("Spell.dbc unavailable");
        SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq) ??
            throw new InvalidDataException("SpellVisual DBCs unavailable");

        SpellInfo stealth = spells.TryGet(1784, out SpellInfo stealthRow) ? stealthRow
            : throw new InvalidDataException("Stealth 1784 unavailable");
        Check(visuals.TryGetStageKit(stealth.VisualId, SpellStage.State,
                  out SpellVisualKitInfo stealthKit, out _) &&
              AuraVisualLaw.Nodes(stealthKit.CharProcs).Any(node =>
                  node.Kind == AuraBodyNodeKind.Alpha && Near(node.Value, .3f)),
            "actual Stealth state kit lost proc-14 alpha .3");

        SpellInfo iceBlock = spells.TryGet(11958, out SpellInfo iceRow) ? iceRow
            : throw new InvalidDataException("Ice Block 11958 unavailable");
        Check(visuals.TryGetStageKit(iceBlock.VisualId, SpellStage.State,
                  out SpellVisualKitInfo iceKit, out _) &&
              AuraVisualLaw.Nodes(iceKit.CharProcs).Any(node =>
                  node.Kind == AuraBodyNodeKind.AnimationRate && node.Value == 0f),
            "actual Ice Block state kit lost proc-11 rate zero");
    }

    private static bool Near(float left, float right) => MathF.Abs(left - right) < .0001f;
    private static bool Near(Vector3 left, Vector3 right) =>
        Vector3.DistanceSquared(left, right) < .000001f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
