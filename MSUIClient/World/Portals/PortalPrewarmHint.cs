using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Server-authored destination data which may be used to warm portal assets
/// before a gameobject exists. It is deliberately not an authorization to
/// render, publish READY, or teleport; those remain descriptor-bound actions.
/// </summary>
public readonly record struct PortalPrewarmHint(
    uint SummonSpellId,
    uint PortalEntry,
    uint TeleportSpellId,
    uint PreviewMapId,
    Vector3 PreviewPosition,
    float PreviewOrientation)
{
    public bool IsValid => PortalPrewarmLaw.IsValid(this);
}

/// <summary>One permitted stock Mage portal summon/object/use identity.</summary>
public readonly record struct PortalPrewarmMapping(
    uint SummonSpellId,
    uint PortalEntry,
    uint TeleportSpellId);

/// <summary>
/// Pure validation boundary for speculative portal warming. Destination
/// coordinates remain server-authored, while the identity triplet must be one
/// of the six stock 1.12 Mage portals. A later descriptor may adopt warmed
/// assets only when every destination-affecting hint field matches exactly.
/// </summary>
public static class PortalPrewarmLaw
{
    private static readonly PortalPrewarmMapping[] StockMappings =
    [
        new(10059, 176296, 17334), // Stormwind
        new(11416, 176497, 17607), // Ironforge
        new(11417, 176499, 17609), // Orgrimmar
        new(11418, 176501, 17611), // Undercity
        new(11419, 176498, 17608), // Darnassus
        new(11420, 176500, 17610), // Thunder Bluff
    ];

    public const int CatalogCount = 6;
    public static ReadOnlySpan<PortalPrewarmMapping> Mappings => StockMappings;

    public static bool TryGetMapping(
        uint summonSpellId,
        out PortalPrewarmMapping mapping)
    {
        foreach (PortalPrewarmMapping candidate in StockMappings)
        {
            if (candidate.SummonSpellId != summonSpellId) continue;
            mapping = candidate;
            return true;
        }

        mapping = default;
        return false;
    }

    public static bool TryGetMapping(
        uint portalEntry,
        uint teleportSpellId,
        out PortalPrewarmMapping mapping)
    {
        foreach (PortalPrewarmMapping candidate in StockMappings)
        {
            if (candidate.PortalEntry != portalEntry ||
                candidate.TeleportSpellId != teleportSpellId)
                continue;
            mapping = candidate;
            return true;
        }

        mapping = default;
        return false;
    }

    public static bool IsValid(in PortalPrewarmHint hint) =>
        hint.PreviewMapId <= int.MaxValue &&
        Finite(hint.PreviewPosition) &&
        float.IsFinite(hint.PreviewOrientation) &&
        (IsPlacedPackPortal(hint) ||
         TryGetMapping(hint.SummonSpellId, out PortalPrewarmMapping mapping) &&
         mapping.PortalEntry == hint.PortalEntry &&
         mapping.TeleportSpellId == hint.TeleportSpellId);

    /// <summary>World Content Pack templates (MangosSuperUI WorldPackContent.TemplateBase).</summary>
    public const uint PackPortalEntryBase = 7_000_000;

    /// <summary>
    /// A PLACED public World Content Pack portal (WoW Karting, shared_docs/WOW_KARTING.md): no summon
    /// spell, a pack template entry and a pack teleport spell. The core accepts the same identity
    /// (SuiPortal::IsPublicPackPortal); it never joins the six-row cast-prewarm catalog.
    /// </summary>
    public static bool IsPackPortalIdentity(uint portalEntry, uint teleportSpellId) =>
        portalEntry >= PackPortalEntryBase &&
        teleportSpellId is >= Formats.SpellCatalog.PackSpellIdBase and < Formats.SpellCatalog.PackSpellIdCeiling;

    private static bool IsPlacedPackPortal(in PortalPrewarmHint hint) =>
        hint.SummonSpellId == 0 && IsPackPortalIdentity(hint.PortalEntry, hint.TeleportSpellId);

    /// <summary>
    /// Require one valid row for every stock summon spell. Catalog row order is
    /// intentionally irrelevant, but duplicates and partial catalogs fail.
    /// </summary>
    public static bool IsCompleteCatalog(ReadOnlySpan<PortalPrewarmHint> catalog)
    {
        if (catalog.Length != CatalogCount) return false;

        int seen = 0;
        foreach (PortalPrewarmHint hint in catalog)
        {
            if (!IsValid(hint)) return false;

            int index = MappingIndex(hint.SummonSpellId);
            if (index < 0) return false;
            int bit = 1 << index;
            if ((seen & bit) != 0) return false;
            seen |= bit;
        }

        return seen == (1 << CatalogCount) - 1;
    }

    public static bool TryFind(
        ReadOnlySpan<PortalPrewarmHint> catalog,
        uint summonSpellId,
        out PortalPrewarmHint hint)
    {
        if (!IsCompleteCatalog(catalog))
        {
            hint = default;
            return false;
        }

        foreach (PortalPrewarmHint candidate in catalog)
        {
            if (candidate.SummonSpellId != summonSpellId) continue;
            hint = candidate;
            return true;
        }

        hint = default;
        return false;
    }

    public static bool TryFromAuthoritative(
        in PortalDescriptor descriptor,
        out PortalPrewarmHint hint)
    {
        hint = default;
        if (!descriptor.IsValid) return false;
        if (IsPackPortalIdentity(descriptor.PortalEntry, descriptor.TeleportSpellId))
        {
            hint = new PortalPrewarmHint(0, descriptor.PortalEntry, descriptor.TeleportSpellId,
                descriptor.PreviewMapId, descriptor.PreviewPosition, descriptor.PreviewOrientation);
            return IsValid(hint);
        }
        if (!TryGetMapping(
                descriptor.PortalEntry,
                descriptor.TeleportSpellId,
                out PortalPrewarmMapping mapping))
            return false;

        hint = new PortalPrewarmHint(
            mapping.SummonSpellId,
            mapping.PortalEntry,
            mapping.TeleportSpellId,
            descriptor.PreviewMapId,
            descriptor.PreviewPosition,
            descriptor.PreviewOrientation);
        return IsValid(hint);
    }

    public static bool MatchesAuthoritativeDestination(
        in PortalPrewarmHint hint,
        in PortalDescriptor descriptor) =>
        IsValid(hint) &&
        descriptor.IsValid &&
        hint.PortalEntry == descriptor.PortalEntry &&
        hint.TeleportSpellId == descriptor.TeleportSpellId &&
        hint.PreviewMapId == descriptor.PreviewMapId &&
        hint.PreviewPosition == descriptor.PreviewPosition &&
        hint.PreviewOrientation == descriptor.PreviewOrientation;

    private static int MappingIndex(uint summonSpellId)
    {
        for (int i = 0; i < StockMappings.Length; i++)
        {
            if (StockMappings[i].SummonSpellId == summonSpellId) return i;
        }

        return -1;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
