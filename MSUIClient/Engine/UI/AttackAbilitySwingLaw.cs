using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Which swing pressing an ability also starts.</summary>
public enum AbilitySwing
{
    None,
    /// <summary>Melee auto-attack (CMSG_ATTACKSWING) on the ability's enemy.</summary>
    Melee,
    /// <summary>Auto Shot on the ability's enemy.</summary>
    AutoShot,
}

/// <summary>
/// Owner 2026-09-22: every attack ability a melee class or a hunter presses is also a press of
/// Attack, whether or not the ability itself goes out. Out of rage or energy, on cooldown or
/// out of range, the swing still starts, so a fight never stalls with the character standing
/// idle because the opening ability was refused.
///
/// Classified from Spell.dbc alone: DmgClass 2 (melee) or an on-next-swing ability starts the
/// melee swing; a DmgClass 3 ranged-weapon ability (Arcane Shot, Multi-Shot, the stings)
/// starts Auto Shot. Only abilities aimed at an enemy qualify, so shouts, stances and
/// self-centred abilities never start a fight. Auto-repeat spells (Auto Shot, wand Shoot) are
/// their own toggle and are never classified.
/// </summary>
public static class AttackAbilitySwingLaw
{
    public const uint AutoShotSpellId = 75;

    public static AbilitySwing Resolve(in SpellInfo spell, bool requiresHostileUnit)
    {
        if (!requiresHostileUnit || spell.Passive || spell.AutoRepeat) return AbilitySwing.None;
        if (spell.DamageClass == 2 || spell.OnNextSwing) return AbilitySwing.Melee;
        if (spell.DamageClass == 3 && (spell.Attributes & 0x2) != 0) return AbilitySwing.AutoShot;
        return AbilitySwing.None;
    }
}
