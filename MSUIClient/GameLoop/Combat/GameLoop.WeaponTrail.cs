using System.Numerics;
using MSUIClient.Creator.Sketch;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// WEAPON TRAILS - SpellVisualKit CharProc type 8 (SPELL_CREATOR_PLAN.md P4.3, census order).
//
// 34 kit lanes use it: Heroic Strike, Cleave, Whirlwind, Bladestorm, Recklessness... Its
// parameters, read from the data (LAB_KIT_CENSUS): p0 a packed RGB (as a float-held integer),
// p1 20, p2 a duration in ms (600 for a strike, 10,000 for Whirlwind's spin), p3 an alpha percent.
// A coloured trail that follows the swing for that long.
//
// Built from proven parts rather than a new renderer: a tiny generated model (SketchWriter) whose
// only content is Blizzard's own "Charge" trail ribbon (copied cross-file, ~1 yd tall, the
// height of a weapon) recoloured to p0, attached to the unit's RIGHT HAND for p2 ms. The ribbon
// system commits edges behind the hand as it swings - which is what a weapon trail is. One model
// per colour, built once per session.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    public const int WeaponTrailProc = 8;
    private const ushort RightHandAttachment = 0x16;
    private readonly Dictionary<uint, string?> _weaponTrailModels = new();

    private void PlayKitWeaponTrail(ulong unit, uint spell, uint rgb, float durationMs, float alphaPercent, double now)
    {
        if (_spellEffects is null || durationMs <= 0f) return;
        string? path = WeaponTrailModel(rgb);
        if (path is null) return;
        var kit = new SpellVisualKitInfo(null, null,
            new[] { new SpellVisualKitEffect(RightHandAttachment, path) }, Array.Empty<SpellVisualCharProc>());
        int spawned = _spellEffects.SpawnKit(unit, spell, kit, StageLife.SelfTerminating, now, "WEAPON_TRAIL", lifetime: durationMs / 1000.0);
        Console.WriteLine($"[weapon-trail] spell {spell}: #{rgb:X6} for {durationMs:0} ms on {unit:X} ({spawned} model)");
    }

    private string? WeaponTrailModel(uint rgb)
    {
        if (_weaponTrailModels.TryGetValue(rgb, out string? cached)) return cached;
        string path = $@"Spells\Custom\WeaponTrail\trail_{rgb:X6}.m2";
        var doc = new SketchDoc { Stage = "state" };
        SketchPiece carrier = doc.AddPiece(SketchShapeKind.Disc);
        // The carrier itself is invisible: a speck at the hand, wearing an existing image so nothing
        // is rasterized. Only its trail is the point.
        carrier.Place.Origin = Vector3.Zero;
        carrier.Shape.Width = carrier.Shape.Height = 0.02f;
        carrier.Look.Image = @"Spells\GenericGlow2b.blp";
        carrier.Look.Alpha = 0.05f;
        carrier.Motion.FadeOut = 0f;
        carrier.Extras.Trail.Preset = SketchTrailPreset.Charge;
        carrier.Extras.Trail.Colour = new Vector3((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) / 255f;
        carrier.Extras.Trail.Length = 0.3f;
        SketchWriter.SketchBuild? build = SketchWriter.Build(doc, 0, "WeaponTrail", _sketchBlpWriter, loopForEditing: true,
            archive: p => _mpq.ReadFile(p));
        if (build is null || build.Notes is { Count: > 0 })
        {
            Console.WriteLine($"[weapon-trail] could not build the #{rgb:X6} trail: {string.Join("; ", build?.Notes ?? [])}");
            return _weaponTrailModels[rgb] = null;
        }
        _mpq.SetOverride(path, build.Model);
        _spellEffects?.SetModelOverride(path, build.Model);
        return _weaponTrailModels[rgb] = path;
    }
}
