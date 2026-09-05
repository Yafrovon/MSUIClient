using MSUIClient.Formats;

namespace MSUIClient.Net;

public readonly record struct ActionSlot(byte Kind, uint ActionId)
{
    public const byte Spell = 0x00;
    public const byte Macro = 0x40;
    public const byte Item = 0x80;
    public uint Packed => ActionId | ((uint)Kind << 24);
}

public readonly record struct SpellCooldown(uint SpellId, uint Category, double StartedAt, double DurationSeconds);
public readonly record struct CooldownDisplay(float? SweepFraction, float? FlashProgress)
{
    public const double FinishFlashSeconds = 1.0;
}

/// <summary>Authoritative local 120-slot bar plus the server-fed spellbook.</summary>
public sealed class PlayerActions
{
    private sealed class CooldownRecord
    {
        public uint SpellId;
        public uint ItemEntry;
        public uint Category;
        public bool CategoryWildcard;
        public bool OnHold;
        public uint GcdCategory;
        public double RecoveryStartedAt;
        public double RecoverySeconds;
        public double CategoryStartedAt;
        public double CategorySeconds;
        public double GcdStartedAt;
        public double GcdSeconds;
    }

    private readonly record struct CooldownSample(double StartedAt, double DurationSeconds,
        double RemainingSeconds, bool Enabled);

    private readonly ActionSlot?[] _slots = new ActionSlot?[120];
    private readonly HashSet<uint> _knownSpells = new();
    private readonly List<CooldownRecord> _cooldowns = [];

    public IReadOnlySet<uint> KnownSpells => _knownSpells;
    public int OccupiedCount => _slots.Count(s => s.HasValue);
    public ActionSlot? this[int wireSlot] => wireSlot is >= 0 and < 120 ? _slots[wireSlot] : null;

    public void Clear()
    {
        Array.Clear(_slots);
        _knownSpells.Clear();
        _cooldowns.Clear();
    }

    public void ApplyButtons(byte[] body)
    {
        Array.Clear(_slots);
        var r = new PacketReader(body);
        int slot = 0;
        while (r.Remaining >= 4 && slot < _slots.Length)
        {
            uint packed = r.ReadU32();
            if (packed != 0)
                _slots[slot] = new ActionSlot((byte)(packed >> 24), packed & 0x00ff_ffffu);
            slot++;
        }
    }

    public void ApplyInitialSpells(byte[] body, double nowSeconds)
    {
        var r = new PacketReader(body);
        r.ReadU8();
        int count = r.ReadU16();
        _knownSpells.Clear();
        for (int i = 0; i < count && r.Remaining >= 4; i++)
        {
            _knownSpells.Add(r.ReadU16());
            r.ReadU16();
        }

        _cooldowns.Clear();
        if (r.Remaining < 2) return;
        int cooldownCount = r.ReadU16();
        for (int i = 0; i < cooldownCount && r.Remaining >= 14; i++)
        {
            uint spell = r.ReadU16();
            uint itemEntry = r.ReadU16();
            uint category = r.ReadU16() & 0x7fffu;
            uint spellMs = r.ReadU32();
            uint categoryMs = r.ReadU32();
            AddRecord(spell, itemEntry, category, categoryWildcard: false,
                spellMs, categoryMs, onHold: false, gcdCategory: 0, gcdMs: 0, nowSeconds);
        }
    }

    /// <summary>Seed the spellbook from a client-side cache (free-view bot bars).</summary>
    public void SeedSpells(IEnumerable<uint> spells)
    {
        _knownSpells.Clear();
        foreach (uint spell in spells) _knownSpells.Add(spell);
    }

    public void Learn(uint spell) => _knownSpells.Add(spell);
    public void Remove(uint spell)
    {
        _knownSpells.Remove(spell);
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i] is { Kind: ActionSlot.Spell, ActionId: var id } && id == spell)
                _slots[i] = null;
    }

    public void Supercede(uint oldSpell, uint newSpell)
    {
        _knownSpells.Remove(oldSpell);
        _knownSpells.Add(newSpell);
        for (int i = 0; i < _slots.Length; i++)
            if (_slots[i] is { Kind: ActionSlot.Spell, ActionId: var id } && id == oldSpell)
                _slots[i] = new ActionSlot(ActionSlot.Spell, newSpell);
    }

    public void Set(int wireSlot, ActionSlot? value)
    {
        if (wireSlot is >= 0 and < 120) _slots[wireSlot] = value;
    }

    public void StartCooldown(uint spell, uint category, uint durationMs, double nowSeconds) =>
        StartCooldown(spell, category, durationMs, 0, nowSeconds);

    public void StartCooldown(uint spell, uint category, uint spellDurationMs,
        uint categoryDurationMs, double nowSeconds) =>
        StartCooldown(spell, category, spellDurationMs, categoryDurationMs, nowSeconds,
            onHold: false);

    /// <summary>Append one recovery node; build 5875 never replaces a matching spell node.</summary>
    public void StartCooldown(uint spell, uint category, uint spellDurationMs,
        uint categoryDurationMs, double nowSeconds, bool onHold,
        bool categoryWildcard = false) =>
        AddRecord(spell, itemEntry: 0, category, categoryWildcard, spellDurationMs,
            categoryDurationMs, onHold, gcdCategory: 0, gcdMs: 0, nowSeconds);

    /// <summary>Local SMSG_SPELL_GO self-insert, including the ranged-weapon category pad.</summary>
    public void StartSpellCooldown(uint spell, in SpellInfo info, uint rangedAttackTimeMs,
        double nowSeconds)
    {
        // An ITEM-triggered cast is already governed by the item's own recovery, authored by the
        // server in item_template (spellcooldown / spellcategorycooldown) and recorded by
        // StartItemUseCooldown when the use went out. Spell.dbc's own numbers are only the
        // fallback for the -1 "use the spell's" case, so re-applying them here would let the DBC
        // overrule an item that deliberately says otherwise - and because AddRecord APPENDS
        // rather than replaces, the longer of the two then governs.
        //
        // Food and drink are the case that exposed this. Spell.dbc gives Food (category 11) and
        // Drink (category 59) a CategoryRecoveryTime of 60000, while the server's item_template
        // authors 1000. Both nodes were being kept, so a drink could not be repeated for a
        // minute (reported 2026-09-04) even though the server would have allowed it a second
        // later - the local gate never asked, it just refused.
        // Prune first: the suppression must key off a LIVE item node, never one that has already
        // run out, or an item used once would silence this spell's own cooldown for good.
        Prune(nowSeconds);
        if (_cooldowns.Any(record => record.SpellId == spell && record.ItemEntry != 0)) return;

        ulong categoryMs = (ulong)info.CategoryRecoveryMs +
            (info.RangedSpeedCooldown ? rangedAttackTimeMs : 0u);
        AddRecord(spell, itemEntry: 0, info.Category, info.CategoryWildcard, info.RecoveryMs,
            (uint)Math.Min(categoryMs, uint.MaxValue), info.CooldownOnEvent,
            gcdCategory: 0, gcdMs: 0, nowSeconds);
    }

    /// <summary>Cast-send GCD node. It remains separate from the later GO recovery node.</summary>
    public void StartGlobalCooldown(uint spell, in SpellInfo info, double nowSeconds)
    {
        if (info.StartRecoveryMs == 0) return;
        AddRecord(spell, itemEntry: 0, category: 0, categoryWildcard: false,
            spellDurationMs: 0, categoryDurationMs: 0, info.CooldownOnEvent,
            info.StartRecoveryCategory, info.StartRecoveryMs, nowSeconds);
    }

    /// <summary>Client-computed item-use recovery from the selected item spell slot.</summary>
    public void StartItemUseCooldown(uint itemEntry, in ItemSpellTemplate useSpell,
        SpellInfo? spell, double nowSeconds)
    {
        uint recoveryMs = useSpell.CooldownMs >= 0
            ? (uint)useSpell.CooldownMs : spell?.RecoveryMs ?? 0;
        uint categoryMs = useSpell.CategoryCooldownMs >= 0
            ? (uint)useSpell.CategoryCooldownMs : spell?.CategoryRecoveryMs ?? 0;
        bool wildcard = spell is { } resolved && resolved.Category == useSpell.Category &&
            resolved.CategoryWildcard;
        AddRecord(useSpell.SpellId, itemEntry, useSpell.Category, wildcard, recoveryMs,
            categoryMs, spell?.CooldownOnEvent ?? false, gcdCategory: 0, gcdMs: 0,
            nowSeconds);
    }

    /// <summary>SMSG_SPELL_COOLDOWN server override/refresh node.</summary>
    public void ApplyWireCooldown(uint spell, uint wireDurationMs, SpellInfo? info,
        double nowSeconds)
    {
        bool held = info?.CooldownOnEvent ?? false;
        uint recoveryMs = wireDurationMs != 0 ? wireDurationMs : info?.RecoveryMs ?? 0;
        uint category = info?.Category ?? 0;
        uint categoryMs = wireDurationMs == 0 ? info?.CategoryRecoveryMs ?? 0 : 0;
        uint gcdCategory = held ? 0 : info?.StartRecoveryCategory ?? 0;
        uint gcdMs = held ? 0 : info?.StartRecoveryMs ?? 0;
        AddRecord(spell, itemEntry: 0, category,
            info is { } resolved && resolved.Category == category && resolved.CategoryWildcard,
            recoveryMs, categoryMs, held, gcdCategory, gcdMs, nowSeconds);
    }

    /// <summary>SMSG_ITEM_COOLDOWN's fixed 30-second item/spell-pair recovery.</summary>
    public void StartItemPacketCooldown(uint spell, uint itemEntry, double nowSeconds) =>
        AddRecord(spell, itemEntry, category: 0, categoryWildcard: false,
            spellDurationMs: 30_000, categoryDurationMs: 0, onHold: false,
            gcdCategory: 0, gcdMs: 0, nowSeconds);

    public void StartCooldownEvent(uint spell, double nowSeconds)
    {
        foreach (CooldownRecord record in _cooldowns.Where(r => r.SpellId == spell && r.OnHold))
        {
            record.RecoveryStartedAt = nowSeconds;
            record.CategoryStartedAt = nowSeconds;
            record.OnHold = false;
        }
    }

    /// <summary>Cast-failure revert: clear only this cast's GCD arm.</summary>
    public void ClearGlobalCooldown(uint spell)
    {
        foreach (CooldownRecord record in _cooldowns.Where(r => r.SpellId == spell))
        {
            record.GcdCategory = 0;
            record.GcdSeconds = 0;
        }
        _cooldowns.RemoveAll(Empty);
    }

    public void ClearCooldown(uint spell) => _cooldowns.RemoveAll(r => r.SpellId == spell);

    public void ClearAllCooldowns() => _cooldowns.Clear();

    public bool HasOnHoldRecord(uint spell, uint category = 0) => _cooldowns.Any(r =>
        r.OnHold && ((r.SpellId == spell && r.ItemEntry == 0) ||
                     (category != 0 && r.Category == category && r.CategorySeconds > 0)));

    public float CooldownFraction(uint spell, double nowSeconds, uint category = 0)
    {
        if (!TryActiveCooldown(spell, itemEntry: 0, category, startRecoveryCategory: 0,
                excluded: false, nowSeconds, out CooldownSample sample)) return 0f;
        return (float)Math.Clamp((nowSeconds - sample.StartedAt) / sample.DurationSeconds, 0, 1);
    }

    public bool TryCooldownDisplay(uint spell, double nowSeconds, uint category,
        out CooldownDisplay display) =>
        TryCooldownDisplay(spell, itemEntry: 0, category, startRecoveryCategory: 0,
            excluded: false, nowSeconds, out display);

    public bool TryCooldownDisplay(uint spell, uint itemEntry, uint category,
        double nowSeconds, out CooldownDisplay display) =>
        TryCooldownDisplay(spell, itemEntry, category, startRecoveryCategory: 0,
            excluded: false, nowSeconds, out display);

    public bool TryCooldownDisplay(uint spell, uint itemEntry, in SpellInfo info,
        double nowSeconds, out CooldownDisplay display) =>
        TryCooldownDisplay(spell, itemEntry, info.Category, info.StartRecoveryCategory,
            info.CooldownQueryExcluded, nowSeconds, out display);

    public bool IsOnCooldown(uint spell, double nowSeconds, uint category = 0) =>
        TryActiveCooldown(spell, itemEntry: 0, category, startRecoveryCategory: 0,
            excluded: false, nowSeconds, out _);

    public bool IsOnCooldown(uint spell, uint itemEntry, uint category, double nowSeconds) =>
        TryActiveCooldown(spell, itemEntry, category, startRecoveryCategory: 0,
            excluded: false, nowSeconds, out _);

    public bool IsOnCooldown(uint spell, uint itemEntry, in SpellInfo info, double nowSeconds) =>
        TryActiveCooldown(spell, itemEntry, info.Category, info.StartRecoveryCategory,
            info.CooldownQueryExcluded, nowSeconds, out _);

    public double CooldownRemaining(uint spell, double nowSeconds, uint category = 0) =>
        TryActiveCooldown(spell, itemEntry: 0, category, startRecoveryCategory: 0,
            excluded: false, nowSeconds, out CooldownSample sample)
            ? sample.RemainingSeconds : 0;

    public double CooldownRemaining(uint spell, uint itemEntry, in SpellInfo info,
        double nowSeconds) =>
        TryActiveCooldown(spell, itemEntry, info.Category, info.StartRecoveryCategory,
            info.CooldownQueryExcluded, nowSeconds, out CooldownSample sample)
            ? sample.RemainingSeconds : 0;

    private void AddRecord(uint spell, uint itemEntry, uint category, bool categoryWildcard,
        uint spellDurationMs, uint categoryDurationMs, bool onHold, uint gcdCategory,
        uint gcdMs, double nowSeconds)
    {
        if (spellDurationMs == 0 && categoryDurationMs == 0 && !onHold && gcdMs == 0) return;
        Prune(nowSeconds);
        _cooldowns.Add(new CooldownRecord
        {
            SpellId = spell,
            ItemEntry = itemEntry,
            Category = category,
            CategoryWildcard = categoryWildcard,
            OnHold = onHold,
            GcdCategory = gcdCategory,
            RecoveryStartedAt = nowSeconds,
            RecoverySeconds = spellDurationMs / 1000.0,
            CategoryStartedAt = nowSeconds,
            CategorySeconds = categoryDurationMs / 1000.0,
            GcdStartedAt = nowSeconds,
            GcdSeconds = gcdMs / 1000.0,
        });
    }

    private bool TryActiveCooldown(uint spell, uint itemEntry, uint category,
        uint startRecoveryCategory, bool excluded, double nowSeconds, out CooldownSample best)
    {
        best = default;
        if (excluded) return false;
        Prune(nowSeconds);
        CooldownSample winner = default;
        foreach (CooldownRecord record in _cooldowns)
        {
            if (record.SpellId == spell && record.ItemEntry == itemEntry)
                Consider(record.RecoveryStartedAt, record.RecoverySeconds, record.OnHold);
            if ((record.Category != 0 && record.Category == category) || record.CategoryWildcard)
                Consider(record.CategoryStartedAt, record.CategorySeconds, record.OnHold);
            if (record.GcdCategory == startRecoveryCategory && record.GcdSeconds > 0)
                Consider(record.GcdStartedAt, record.GcdSeconds, held: false);
        }
        best = winner;
        return winner.RemainingSeconds > 0;

        void Consider(double startedAt, double duration, bool held)
        {
            if (duration <= 0) return;
            double remaining = held ? duration : startedAt + duration - nowSeconds;
            if (remaining > winner.RemainingSeconds)
                winner = new CooldownSample(startedAt, duration, remaining, !held);
        }
    }

    private bool TryCooldownDisplay(uint spell, uint itemEntry, uint category,
        uint startRecoveryCategory, bool excluded, double nowSeconds, out CooldownDisplay display)
    {
        display = default;
        if (excluded) return false;
        Prune(nowSeconds);
        CooldownSample active = default;
        double latestExpiredEnd = double.NegativeInfinity;
        foreach (CooldownRecord record in _cooldowns)
        {
            if (record.SpellId == spell && record.ItemEntry == itemEntry)
                Consider(record.RecoveryStartedAt, record.RecoverySeconds, record.OnHold);
            if ((record.Category != 0 && record.Category == category) || record.CategoryWildcard)
                Consider(record.CategoryStartedAt, record.CategorySeconds, record.OnHold);
            if (record.GcdCategory == startRecoveryCategory && record.GcdSeconds > 0)
                Consider(record.GcdStartedAt, record.GcdSeconds, held: false);
        }
        if (active.RemainingSeconds > 0)
        {
            if (!active.Enabled) return false;
            display = new CooldownDisplay((float)Math.Clamp(
                (nowSeconds - active.StartedAt) / active.DurationSeconds, 0.0, 1.0), null);
            return true;
        }
        if (!double.IsFinite(latestExpiredEnd)) return false;
        display = new CooldownDisplay(null,
            (float)Math.Clamp(nowSeconds - latestExpiredEnd, 0.0, 1.0));
        return true;

        void Consider(double startedAt, double duration, bool held)
        {
            if (duration <= 0) return;
            if (held)
            {
                if (duration > active.RemainingSeconds)
                    active = new CooldownSample(startedAt, duration, duration, Enabled: false);
                return;
            }
            double end = startedAt + duration;
            double remaining = end - nowSeconds;
            if (remaining > active.RemainingSeconds)
                active = new CooldownSample(startedAt, duration, remaining, Enabled: true);
            else if (remaining <= 0 && remaining > -CooldownDisplay.FinishFlashSeconds)
                latestExpiredEnd = Math.Max(latestExpiredEnd, end);
        }
    }

    private void Prune(double nowSeconds) => _cooldowns.RemoveAll(record =>
        !record.OnHold &&
        Finished(record.RecoveryStartedAt, record.RecoverySeconds, nowSeconds) &&
        Finished(record.CategoryStartedAt, record.CategorySeconds, nowSeconds) &&
        Finished(record.GcdStartedAt, record.GcdSeconds, nowSeconds));

    private static bool Finished(double startedAt, double duration, double nowSeconds) =>
        duration <= 0 || nowSeconds >= startedAt + duration + CooldownDisplay.FinishFlashSeconds;

    private static bool Empty(CooldownRecord record) => !record.OnHold &&
        record.RecoverySeconds <= 0 && record.CategorySeconds <= 0 && record.GcdSeconds <= 0;
}
