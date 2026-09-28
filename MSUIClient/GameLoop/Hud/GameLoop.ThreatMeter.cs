using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Threat Meter add-on (owner 2026-09-22). While enabled and the server advertises
/// threat-meter-v1, pulls the top of the current target's threat list twice a second and draws
/// it as class-coloured bars on the pull scale (<see cref="ThreatMeterLaw"/>): the holder, the
/// melee pull tick at 110 %, every row as a share of the holder, and your own row even when
/// you are outside the top rows. "You" is the body you drive.
/// </summary>
public sealed partial class GameLoop
{
    private bool _threatMeterAvailable;
    private ThreatMeterWire.Snapshot? _threatSnapshot;
    private double _threatSnapshotAt = double.NegativeInfinity;
    private double _threatRequestAt = double.NegativeInfinity;
    private ulong _threatRequestSubject;
    private bool _threatMeterDragDirty;

    private GameSettings.ThreatMeterSettings ThreatMeterSettings =>
        (Settings.AddOns ??= new GameSettings.AddOnSettings()).ThreatMeter ??=
            new GameSettings.ThreatMeterSettings();

    private void ApplyThreatMeterCapability(uint capabilities)
    {
        bool available = (capabilities & ThreatMeterWire.Capability) != 0;
        if (available != _threatMeterAvailable)
            Console.WriteLine(available
                ? "[threat-meter] server advertised threat-meter-v1"
                : "[threat-meter] server has no threat-meter-v1 advertisement");
        _threatMeterAvailable = available;
    }

    private void ResetThreatMeter()
    {
        _threatMeterAvailable = false;
        _threatSnapshot = null;
        _threatSnapshotAt = double.NegativeInfinity;
        _threatRequestAt = double.NegativeInfinity;
        _threatRequestSubject = 0;
    }

    private void ApplySuiThreat(byte[] body)
    {
        if (ThreatMeterWire.Parse(body) is not { } snapshot) return;
        // A late answer for the previous target must not paint over the new one.
        if (snapshot.Creature != _threatRequestSubject) return;
        _threatSnapshot = snapshot;
        _threatSnapshotAt = NowSeconds();
    }

    /// <summary>The creature whose list we show: your target, or - when you target a friend,
    /// as a healer or an off-tank watching the tank - that friend's target.</summary>
    private ulong ThreatMeterSubject()
    {
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity target)) return 0;
        if (target.IsCreature && !target.Fields.ReadsDead && CanAttack(target)) return target.Guid;
        if (target.Fields.Target is ulong theirs && _entities.TryGet(theirs, out WorldEntity other) &&
            other.IsCreature && !other.Fields.ReadsDead && CanAttack(other))
            return other.Guid;
        return 0;
    }

    /// <summary>Players by name query, creatures and pets by their template name.</summary>
    private string ThreatMeterName(ulong guid)
    {
        string name = ResolveWorldUnitName(guid);
        if (name != "target") return name;
        // A player guid carries no high type bits; ask once, show a placeholder meanwhile.
        if (guid >> 48 == 0 && _queriedPlayerNames.Add(guid)) _net?.NameQuery(guid);
        return "...";
    }

    private void DrawThreatMeter()
    {
        if (_net is null) return;
        var cfg = ThreatMeterSettings;
        if (!cfg.Enabled) return;
        double now = NowSeconds();
        int rows = ThreatMeterLaw.ClampRows(cfg.Rows);

        ulong subject = _threatMeterAvailable ? ThreatMeterSubject() : 0;
        if (subject != _threatRequestSubject)
        {
            _threatRequestSubject = subject;
            _threatSnapshot = null;
            _threatRequestAt = double.NegativeInfinity;
        }
        if (subject != 0 && now - _threatRequestAt >= ThreatMeterLaw.RequestIntervalSeconds)
        {
            _threatRequestAt = now;
            _net.SuiThreat(subject, rows);
        }

        ThreatMeterWire.Snapshot? snapshot =
            _threatSnapshot is { } live && now - _threatSnapshotAt <= ThreatMeterLaw.StaleSeconds ? live : null;
        IReadOnlyList<ThreatMeterLaw.Entry> entries = snapshot is { Available: true }
            ? ThreatMeterLaw.Entries(snapshot, ControlledGuid) : [];
        if (!ThreatMeterLaw.Visible(cfg.Enabled, cfg.Unlocked, cfg.HideWhenIdle, entries.Count > 0))
            return;

        float s = GameplayUiScale();
        var size = new Vector2(ThreatMeterLaw.Width, ThreatMeterLaw.Height(entries.Count));
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        var authored = new Vector2(logicalDisplay.X - size.X - 24f, logicalDisplay.Y * 0.42f);
        Vector2 origin = ClampSwingTimerOrigin(authored + new Vector2(cfg.OffsetX, cfg.OffsetY),
            logicalDisplay, size);

        ImGui.SetNextWindowPos(origin * s, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing;
        if (!cfg.Unlocked) flags |= ImGuiWindowFlags.NoInputs;
        if (!ImGui.Begin("##threat-meter", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 min = origin * s, max = (origin + size) * s;

        dl.AddRectFilled(min + new Vector2(2, 2) * s, max - new Vector2(2, 2) * s, 0xD0080808u, 3 * s);
        _skin?.DrawBackdrop(dl, min, max, WowSkin.Tooltip, Vector4.Zero, new Vector4(0.8f, 0.8f, 0.8f, 1f));
        if (cfg.Unlocked) dl.AddRect(min, max, 0xFF00CCFFu, 3 * s);

        string title = subject != 0 ? $"Threat: {ThreatMeterName(subject)}" : "Threat";
        if (!_threatMeterAvailable) title = "Threat (server has no threat meter)";
        Vector2 pad = new Vector2(ThreatMeterLaw.Padding, ThreatMeterLaw.Padding - 1) * s;
        dl.PushClipRect(min, max, true);
        GameText.Draw(dl, "GameFontNormalSmall", title, min + pad, s);

        float holder = ThreatMeterLaw.HolderThreat(entries);
        float scale = ThreatMeterLaw.Scale(entries, holder);
        float barWidth = (ThreatMeterLaw.Width - ThreatMeterLaw.Padding * 2) * s;
        float y = min.Y + (ThreatMeterLaw.Padding + ThreatMeterLaw.TitleHeight) * s;
        if (entries.Count == 0)
        {
            string hint = cfg.Unlocked ? "Drag to move; target an enemy in combat" : "No threat on this target";
            GameText.Draw(dl, "GameFontDisableSmall", hint, new Vector2(min.X + pad.X, y + 1 * s), s);
        }
        foreach (ThreatMeterLaw.Entry entry in entries)
        {
            var barMin = new Vector2(min.X + pad.X, y);
            var barMax = barMin + new Vector2(barWidth, ThreatMeterLaw.RowHeight * s);
            string name = ThreatMeterName(entry.Guid);
            uint classColor = ClassColorForGuid(entry.Guid, name);
            dl.AddRectFilled(barMin, barMax, 0x70000000u);
            float fill = ThreatMeterLaw.Fraction(entry.Threat, scale);
            dl.AddRectFilled(barMin, new Vector2(barMin.X + barWidth * fill, barMax.Y),
                WithAlphaByte(ThreatMeterLaw.BarColor(entry, holder, classColor), 0xB0));
            if (entry.IsSelf) dl.AddRect(barMin, barMax, 0xFFFFFFFFu);
            // The holder: a gold edge on the left. Everyone else: the melee pull tick.
            if (entry.IsHolder)
                dl.AddRectFilled(barMin, new Vector2(barMin.X + 3 * s, barMax.Y), 0xFF00D1FFu);
            float pullX = barMin.X + barWidth * ThreatMeterLaw.Fraction(holder * ThreatMeterLaw.MeleePull, scale);
            dl.AddLine(new Vector2(pullX, barMin.Y), new Vector2(pullX, barMax.Y), 0xA0FFFFFFu, 1f);

            GameText.Draw(dl, "GameFontHighlightSmall", $"{entry.Rank}. {name}",
                barMin + new Vector2(5, 1) * s, s);
            string value = ThreatMeterLaw.FormatThreat(entry.Threat);
            if (ThreatMeterLaw.PercentOfHolder(entry.Threat, holder) is int percent) value += $"  {percent}%";
            GameText.DrawRightAligned(dl, "GameFontHighlightSmall", value,
                new Vector2(barMax.X - 3 * s, barMin.Y + 1 * s), s);
            y += (ThreatMeterLaw.RowHeight + ThreatMeterLaw.RowGap) * s;
        }
        dl.PopClipRect();

        if (cfg.Unlocked)
        {
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##threat-meter-drag", max - min);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                origin = ClampSwingTimerOrigin(origin + ImGui.GetIO().MouseDelta / MathF.Max(.01f, s),
                    logicalDisplay, size);
                cfg.OffsetX = origin.X - authored.X;
                cfg.OffsetY = origin.Y - authored.Y;
                _threatMeterDragDirty = true;
            }
            if (_threatMeterDragDirty && ImGui.IsItemDeactivated())
            {
                SettingsFile?.Save();
                _threatMeterDragDirty = false;
            }
        }
        ImGui.End();
    }
}
