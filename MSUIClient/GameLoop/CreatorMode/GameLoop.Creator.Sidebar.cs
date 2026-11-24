using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator sidebar + the Atlas look (owner, 2026-10-14: "a slightly more modern sidebar that scrolls within itself",
// panel on the LEFT, the classic top buttons stay; style = the web app's Quest Atlas, MangosSuperUI
// wwwroot/css/questatlas.css).
//
// Classic layout only (Settings.Creator.Workspace off): the open panel docks to the left edge under the top bar, full
// height, its content scrolling inside it; the right edge drags the width (remembered per panel), the chevron folds it
// to a small tab. While the sidebar is on, every Creator window drawn through PushCreatorStyle/DrawCreatorPanelChrome
// wears the Atlas look: warm dark glass, hairline borders, cream ink in three strengths, one gold accent.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    // The Quest Atlas palette (questatlas.css :root of .qa-stage).
    private static readonly Vector4 AtlasInk = new(0xEF / 255f, 0xE6 / 255f, 0xD2 / 255f, 1f);
    private static readonly Vector4 AtlasInkDim = new(0xB9 / 255f, 0xAC / 255f, 0x90 / 255f, 1f);
    private static readonly Vector4 AtlasInkFaint = new(0x8A / 255f, 0x7F / 255f, 0x69 / 255f, 1f);
    private static readonly Vector4 AtlasPanelSolid = new(0x1E / 255f, 0x19 / 255f, 0x13 / 255f, 1f);
    private static readonly Vector4 AtlasGold = new(0xF2 / 255f, 0xC1 / 255f, 0x4E / 255f, 1f);
    private static readonly Vector4 AtlasRed = new(0xE0 / 255f, 0x61 / 255f, 0x4F / 255f, 1f);
    private static readonly Vector4 AtlasAmber = new(0xE8 / 255f, 0xA3 / 255f, 0x3A / 255f, 1f);
    private static readonly Vector4 AtlasGreen = new(0x8C / 255f, 0xC4 / 255f, 0x6A / 255f, 1f);

    private static Vector4 AtlasA(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
    private static uint AtlasU(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
    private static uint AtlasU(Vector4 c, float a) => ImGui.ColorConvertFloat4ToU32(AtlasA(c, a));

    /// <summary>The classic layout with the docked sidebar (the default); off = floating windows.</summary>
    private bool CreatorSidebarOn => Settings.Creator.Sidebar && !Settings.Creator.Workspace;

    /// <summary>Creator windows wear the Quest Atlas look whenever the sidebar layout is on.</summary>
    private bool CreatorAtlasLook => CreatorSidebarOn;

    private float CreatorPixelScale => MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorUiScale;

    private bool _creatorSidebarDrawing;     // between BeginCreatorPanel and EndCreatorPanel, sidebar mode
    private bool _creatorStyleAtlas;         // the last PushCreatorStyle pushed the Atlas style
    private float _creatorBarBottom;         // the top bar's bottom edge this frame (screen px)
    private float _creatorSidebarRight;      // the sidebar's right edge this frame, 0 when none is drawn
    private string? _creatorSidebarPanel;
    private bool _creatorSidebarResizing;

    /// <summary>Panel opacity: the Atlas glass is .88 at the default dial (.62), following the dial from there.</summary>
    // .94 rather than the web's .88: ImGui has no backdrop blur, so world labels would read through the glass.
    private float AtlasPanelAlpha => Math.Clamp(0.94f * Settings.Creator.PanelAlpha / 0.62f, 0.3f, 1f);

    private float CreatorSidebarTop => MathF.Max(_creatorBarBottom, 40f) + 6f * CreatorPixelScale;
    private float CreatorSidebarMargin => 10f * CreatorPixelScale;

    private float CreatorSidebarMinWidth => 300f * CreatorUiScale;

    /// <summary>Widest the sidebar may be dragged: three quarters of the screen, so the world stays usable.</summary>
    private float CreatorSidebarMaxWidth => MathF.Max(320f * CreatorUiScale, ImGui.GetIO().DisplaySize.X * 0.75f);

    private float CreatorSidebarWidth(string panelId, float defaultW)
    {
        float w = (Settings.Creator.SidebarWidths.TryGetValue(panelId, out float saved) ? saved : defaultW) * CreatorUiScale;
        return Math.Clamp(w, CreatorSidebarMinWidth, CreatorSidebarMaxWidth);
    }

    /// <summary>The Atlas style: Quest Atlas colours, hairline frames, soft radii and tighter rows than the vanilla skin.</summary>
    private void PushCreatorAtlasStyle()
    {
        _creatorStyleAtlas = true;
        _creatorStyleColors = 0;
        _creatorStyleVars = 0;
        void C(ImGuiCol which, Vector4 color) { ImGui.PushStyleColor(which, color); _creatorStyleColors++; }
        void V(ImGuiStyleVar which, Vector2 value) { ImGui.PushStyleVar(which, value); _creatorStyleVars++; }
        void F(ImGuiStyleVar which, float value) { ImGui.PushStyleVar(which, value); _creatorStyleVars++; }

        var clear = new Vector4(0f, 0f, 0f, 0f);
        var line = AtlasA(AtlasInk, 0.12f);
        C(ImGuiCol.Text, AtlasInk);
        C(ImGuiCol.TextDisabled, AtlasInkDim);
        C(ImGuiCol.WindowBg, clear);                       // the chrome paints the glass
        C(ImGuiCol.ChildBg, clear);
        C(ImGuiCol.PopupBg, AtlasA(AtlasPanelSolid, 0.98f));
        C(ImGuiCol.Border, line);
        C(ImGuiCol.BorderShadow, clear);
        C(ImGuiCol.ModalWindowDimBg, new Vector4(0f, 0f, 0f, 0.35f));
        C(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0.35f));
        C(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0.45f));
        C(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0.55f));
        C(ImGuiCol.Button, AtlasA(AtlasInk, 0.07f));
        C(ImGuiCol.ButtonHovered, AtlasA(AtlasGold, 0.20f));
        C(ImGuiCol.ButtonActive, AtlasA(AtlasGold, 0.32f));
        C(ImGuiCol.Header, AtlasA(AtlasGold, 0.16f));     // selected rows/chips: .qa-q.sel
        C(ImGuiCol.HeaderHovered, AtlasA(AtlasInk, 0.07f));
        C(ImGuiCol.HeaderActive, AtlasA(AtlasGold, 0.24f));
        C(ImGuiCol.CheckMark, AtlasGold);
        C(ImGuiCol.SliderGrab, AtlasA(AtlasGold, 0.75f));
        C(ImGuiCol.SliderGrabActive, AtlasGold);
        C(ImGuiCol.ScrollbarBg, clear);
        C(ImGuiCol.ScrollbarGrab, AtlasA(AtlasInk, 0.16f));
        C(ImGuiCol.ScrollbarGrabHovered, AtlasA(AtlasInk, 0.28f));
        C(ImGuiCol.ScrollbarGrabActive, AtlasA(AtlasGold, 0.6f));
        C(ImGuiCol.Separator, line);
        C(ImGuiCol.SeparatorHovered, AtlasA(AtlasGold, 0.5f));
        C(ImGuiCol.SeparatorActive, AtlasGold);
        C(ImGuiCol.Tab, clear);
        C(ImGuiCol.TabHovered, AtlasA(AtlasGold, 0.12f));
        C(ImGuiCol.TabActive, AtlasA(AtlasGold, 0.18f));
        C(ImGuiCol.TabUnfocused, clear);
        C(ImGuiCol.TabUnfocusedActive, AtlasA(AtlasGold, 0.12f));
        C(ImGuiCol.ResizeGrip, clear);
        C(ImGuiCol.ResizeGripHovered, AtlasA(AtlasGold, 0.3f));
        C(ImGuiCol.ResizeGripActive, AtlasA(AtlasGold, 0.5f));
        C(ImGuiCol.TextSelectedBg, AtlasA(AtlasGold, 0.3f));
        C(ImGuiCol.PlotHistogram, AtlasGold);

        float ps = CreatorPixelScale;
        float padMul = Math.Clamp(Settings.Creator.PaddingScale, 0.4f, 2f);
        float spaceMul = Math.Clamp(Settings.Creator.SpacingScale, 0.4f, 2f) * Math.Clamp(ActivePanelTune.Spacing, 0.5f, 2.5f);
        F(ImGuiStyleVar.WindowRounding, 10f * ps);
        F(ImGuiStyleVar.ChildRounding, 6f * ps);
        F(ImGuiStyleVar.FrameRounding, 5f * ps);
        F(ImGuiStyleVar.PopupRounding, 8f * ps);
        F(ImGuiStyleVar.GrabRounding, 4f * ps);
        F(ImGuiStyleVar.ScrollbarRounding, 6f * ps);
        F(ImGuiStyleVar.TabRounding, 5f * ps);
        F(ImGuiStyleVar.WindowBorderSize, 0f);
        F(ImGuiStyleVar.ChildBorderSize, 0f);
        F(ImGuiStyleVar.PopupBorderSize, 1f);
        F(ImGuiStyleVar.FrameBorderSize, 1f);
        F(ImGuiStyleVar.ScrollbarSize, 8f * ps);
        F(ImGuiStyleVar.GrabMinSize, 10f * ps);
        F(ImGuiStyleVar.IndentSpacing, 14f * ps);
        V(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * ps * padMul);
        V(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * ps * spaceMul);
        V(ImGuiStyleVar.ItemInnerSpacing, new Vector2(6f, 4f) * ps);
        V(ImGuiStyleVar.FramePadding, new Vector2(8f, 3f) * ps);
    }

    /// <summary>
    /// The Atlas chrome in place of the vanilla dialog art: warm glass with a hairline border and a soft shadow, a small
    /// uppercase kicker over a gold title, and flat icon buttons (gear, fold for the sidebar, close). Returns true when
    /// close was clicked; <paramref name="fold"/> reports the sidebar's fold chevron.
    /// </summary>
    private bool DrawCreatorAtlasChrome(string title, string? tuneId, bool sidebar, out bool fold)
    {
        fold = false;
        var dl = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        float ps = CreatorPixelScale;
        float r = 10f * ps;

        dl.PushClipRect(min - new Vector2(40f, 40f) * ps, max + new Vector2(40f, 40f) * ps, false);
        for (int i = 1; i <= 5; i++)   // box-shadow: 0 10px 40px rgba(0,0,0,.45), approximated by widening rings
        {
            var grow = new Vector2(i * 4f * ps, i * 4f * ps);
            dl.AddRectFilled(min - grow + new Vector2(0f, 6f * ps), max + grow + new Vector2(0f, 6f * ps),
                AtlasU(new Vector4(0f, 0f, 0f, 0.07f)), r + grow.X);
        }
        dl.AddRectFilled(min, max, AtlasU(new Vector4(28 / 255f, 23 / 255f, 18 / 255f, AtlasPanelAlpha)), r);
        dl.AddRect(min, max, AtlasU(AtlasInk, 0.12f), r, ImDrawFlags.None, 1f);
        dl.PopClipRect();

        // Header: kicker + title on the left, icon buttons on the right.
        Vector2 keep = ImGui.GetCursorPos();
        Vector2 at = ImGui.GetCursorScreenPos();
        float font = ImGui.GetFontSize();
        // The global text dial only: a panel's own Text dial shrinks its content, never its title.
        float chromeText = Math.Clamp(Settings.Creator.TextScale, 0.6f, 2.5f);
        float kickerSize = font * chromeText * 0.78f;
        float titleSize = font * chromeText * 1.3f;
        float right = max.X - ImGui.GetStyle().WindowPadding.X;
        AtlasSmallCaps(dl, at, sidebar ? "Creator mode" : "Creator", AtlasInkFaint, kickerSize);
        var titlePos = at + new Vector2(0f, kickerSize + 2f * ps);
        dl.AddText(ImGui.GetFont(), titleSize, titlePos, AtlasU(AtlasGold), title);
        float headerH = kickerSize + 2f * ps + titleSize;

        float icon = MathF.Max(18f * ps, titleSize * 0.95f);
        float y = at.Y + headerH - icon;
        float x = right - icon;
        bool closed = AtlasIconButton(dl, $"##atlas-close-{title}", new Vector2(x, y), icon, AtlasGlyph.Close, "Close");
        if (sidebar)
        {
            x -= icon + 4f * ps;
            fold = AtlasIconButton(dl, $"##atlas-fold-{title}", new Vector2(x, y), icon, AtlasGlyph.FoldLeft,
                "Fold the sidebar away (the tab at the left edge brings it back)", observe: "Fold sidebar");
        }
        if (tuneId is not null)
        {
            x -= icon + 4f * ps;
            if (AtlasIconButton(dl, $"##atlas-gear-{title}", new Vector2(x, y), icon, AtlasGlyph.Gear, "Layout dials for this window",
                    _openPanelTuneId == tuneId))
                _openPanelTuneId = _openPanelTuneId == tuneId ? null : tuneId;
        }

        ImGui.SetCursorPos(keep);
        ImGui.Dummy(new Vector2(1f, headerH + 4f * ps));
        var lineY = ImGui.GetCursorScreenPos().Y;
        dl.AddLine(new Vector2(min.X + 1f, lineY), new Vector2(max.X - 1f, lineY), AtlasU(AtlasInk, 0.12f), 1f);
        ImGui.Dummy(new Vector2(1f, 2f * ps));
        return closed;
    }

    private enum AtlasGlyph { Close, FoldLeft, Unfold, Gear }

    /// <summary>A flat square icon button (hover: white 6% plate and gold glyph), drawn by hand so no font glyph is needed.</summary>
    private bool AtlasIconButton(ImDrawListPtr dl, string id, Vector2 pos, float size, AtlasGlyph glyph, string tip, bool on = false,
        string? observe = null)
    {
        Vector2 keep = ImGui.GetCursorPos();
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        if (observe is not null) ObserveWorldBuilderUiItem(observe);   // scripted QA can click it by name
        bool hovered = ImGui.IsItemHovered();
        ImGui.SetCursorPos(keep);
        float ps = CreatorPixelScale;
        if (hovered || on) dl.AddRectFilled(pos, pos + new Vector2(size, size), AtlasU(hovered ? AtlasInk : AtlasGold, hovered ? 0.07f : 0.16f), 5f * ps);
        uint col = AtlasU(hovered || on ? AtlasGold : AtlasInkDim);
        Vector2 c = pos + new Vector2(size, size) * 0.5f;
        float g = size * 0.22f, stroke = MathF.Max(1.4f, 1.6f * ps);
        switch (glyph)
        {
            case AtlasGlyph.Close:
                dl.AddLine(c - new Vector2(g, g), c + new Vector2(g, g), col, stroke);
                dl.AddLine(c + new Vector2(-g, g), c + new Vector2(g, -g), col, stroke);
                break;
            case AtlasGlyph.FoldLeft:
            case AtlasGlyph.Unfold:
                float dir = glyph == AtlasGlyph.FoldLeft ? 1f : -1f;
                for (int k = 0; k < 2; k++)
                {
                    float ox = (k == 0 ? -g * 0.45f : g * 0.45f) * dir;
                    dl.AddLine(c + new Vector2(ox + g * 0.5f * dir, -g), c + new Vector2(ox - g * 0.5f * dir, 0f), col, stroke);
                    dl.AddLine(c + new Vector2(ox - g * 0.5f * dir, 0f), c + new Vector2(ox + g * 0.5f * dir, g), col, stroke);
                }
                break;
            case AtlasGlyph.Gear:
                float rim = size * 0.2f;
                dl.AddCircle(c, rim, col, 12, stroke);
                dl.AddCircleFilled(c, size * 0.06f, col);
                for (int spoke = 0; spoke < 8; spoke++)
                {
                    float a = spoke * MathF.PI / 4f;
                    var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
                    dl.AddLine(c + d * rim, c + d * (rim + size * 0.1f), col, stroke);
                }
                break;
        }
        if (hovered) ImGui.SetTooltip(tip);
        return clicked;
    }

    /// <summary>Small uppercase, letter-spaced label (.qa-list-head / .qa-card-kicker).</summary>
    private static float AtlasSmallCaps(ImDrawListPtr dl, Vector2 pos, string text, Vector4 colour, float size)
    {
        var font = ImGui.GetFont();
        uint col = AtlasU(colour);
        float x = pos.X, track = size * 0.08f;
        float scale = size / ImGui.GetFontSize();
        foreach (char ch in text.ToUpperInvariant())
        {
            string s = ch.ToString();
            dl.AddText(font, size, new Vector2(x, pos.Y), col, s);
            x += ImGui.CalcTextSize(s).X * scale + track;
        }
        return x - pos.X;
    }

    /// <summary>
    /// Begin the docked sidebar window for <paramref name="panelId"/>: left edge, under the top bar, full height. Returns
    /// false when folded (a tab is drawn instead) or closed. Pair with <see cref="EndCreatorPanel"/>.
    /// </summary>
    private bool BeginCreatorSidebar(string title, string panelId, float defaultW)
    {
        var display = ImGui.GetIO().DisplaySize;
        float ps = CreatorPixelScale;
        float top = CreatorSidebarTop, margin = CreatorSidebarMargin;
        if (Settings.Creator.SidebarCollapsed)
        {
            DrawCreatorSidebarTab(title, top, margin);
            return false;
        }

        float width = CreatorSidebarWidth(panelId, defaultW);
        if (panelId == "World" && _wbUiPanelSize is { } scripted) { width = MathF.Min(scripted.X, CreatorSidebarMaxWidth); _wbUiPanelSize = null; }
        else if (_creatorSidebarPanel == panelId && _creatorSidebarResizing) width = _creatorSidebarLiveWidth;
        _creatorSidebarPanel = panelId;
        _creatorSidebarLiveWidth = width;
        _creatorSidebarDefaultWidth = defaultW;
        _creatorSidebarOverflowX = 0f;

        _activePanelTune = panelId;
        _creatorSidebarDrawing = true;
        ImGui.SetNextWindowPos(new Vector2(margin, top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, MathF.Max(display.Y - top - margin, 200f * ps)), ImGuiCond.Always);
        PushCreatorStyle();
        if (!ImGui.Begin($"###creator-sidebar-{title}", CreatorChromeFlags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            PopCreatorStyle();
            _creatorSidebarDrawing = false;
            _activePanelTune = null;
            return false;
        }
        _creatorSidebarRight = margin + width;
        if (DrawCreatorAtlasChrome(title, panelId, sidebar: true, out bool fold)) _creatorPanel = CreatorPanel.None;
        if (fold) { Settings.Creator.SidebarCollapsed = true; SettingsFile?.Save(); }
        ImGui.SetWindowFontScale(CreatorTextScale);
        return true;
    }

    private float _creatorSidebarLiveWidth;
    private float _creatorSidebarDefaultWidth;   // the open panel's own default width (unscaled)
    private float _creatorSidebarOverflowX;      // how far the content runs past the visible width this frame (px)
    private Vector2 _creatorSidebarMin, _creatorSidebarSize;

    /// <summary>
    /// The sidebar's drawer handle, just outside its right edge, in its own little window so it never sits on the
    /// content's scrollbar and a click on it never reaches the world. Drag to resize (remembered per panel on release).
    /// Double-click: widen exactly enough to show everything (the horizontal scrollbar goes away); when nothing is
    /// cut off, back to the panel's default width.
    /// </summary>
    private void DrawCreatorSidebarResizeGrip()
    {
        if (_creatorSidebarPanel is not { } panelId) return;
        float ps = CreatorPixelScale;
        Vector2 min = _creatorSidebarMin, size = _creatorSidebarSize;
        float hitW = 10f * ps;
        var origin = new Vector2(min.X + size.X, min.Y);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(hitW, size.Y), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##creator-sidebar-grip-window", flags))
        {
            ImGui.SetCursorScreenPos(origin);
            ImGui.InvisibleButton("##creator-sidebar-grip", new Vector2(hitW, size.Y));
            ObserveWorldBuilderUiItem("Sidebar edge");
            bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
            var io = ImGui.GetIO();

            // The handle: a small rounded tab on the edge, mid-height, always faintly visible so it can be found.
            var dl = ImGui.GetWindowDrawList();
            float tabW = 8f * ps, tabH = 46f * ps;
            var tabMin = new Vector2(origin.X, min.Y + (size.Y - tabH) * 0.5f);
            var tabMax = tabMin + new Vector2(tabW, tabH);
            bool lit = hovered || active;
            dl.AddRectFilled(tabMin, tabMax, AtlasU(new Vector4(28 / 255f, 23 / 255f, 18 / 255f, AtlasPanelAlpha)), 4f * ps,
                ImDrawFlags.RoundCornersRight);
            dl.AddRect(tabMin, tabMax, AtlasU(lit ? AtlasGold : AtlasInk, lit ? 0.6f : 0.18f), 4f * ps, ImDrawFlags.RoundCornersRight, 1f);
            uint ridge = AtlasU(lit ? AtlasGold : AtlasInkDim, lit ? 0.95f : 0.6f);
            for (int k = -1; k <= 1; k += 2)
            {
                float x = tabMin.X + tabW * 0.5f + k * 1.4f * ps;
                dl.AddLine(new Vector2(x, tabMin.Y + 12f * ps), new Vector2(x, tabMax.Y - 12f * ps), ridge, MathF.Max(1f, 1.2f * ps));
            }
            if (lit)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                float x = origin.X + 1f;
                dl.AddLine(new Vector2(x, min.Y + 14f * ps), new Vector2(x, min.Y + size.Y - 14f * ps),
                    AtlasU(AtlasGold, active ? 0.9f : 0.5f), 2f * ps);
            }

            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                float fit = _creatorSidebarOverflowX > 0.5f
                    ? _creatorSidebarLiveWidth + _creatorSidebarOverflowX + 6f * ps
                    : _creatorSidebarDefaultWidth * CreatorUiScale;
                _creatorSidebarLiveWidth = Math.Clamp(fit, CreatorSidebarMinWidth, CreatorSidebarMaxWidth);
                Settings.Creator.SidebarWidths[panelId] = _creatorSidebarLiveWidth / CreatorUiScale;
                SettingsFile?.Save();
            }
            else if (active)
            {
                _creatorSidebarResizing = true;
                _creatorSidebarLiveWidth = Math.Clamp(_creatorSidebarLiveWidth + io.MouseDelta.X,
                    CreatorSidebarMinWidth, CreatorSidebarMaxWidth);
            }
            else if (_creatorSidebarResizing)
            {
                _creatorSidebarResizing = false;
                Settings.Creator.SidebarWidths[panelId] = _creatorSidebarLiveWidth / CreatorUiScale;
                SettingsFile?.Save();
            }
            if (hovered && !active)
                ImGui.SetTooltip(_creatorSidebarOverflowX > 0.5f
                    ? "Drag to resize the sidebar. Double-click to widen it until nothing is cut off."
                    : "Drag to resize the sidebar. Double-click to go back to the default width.");
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>The folded sidebar: a small glass tab on the left edge; click to unfold.</summary>
    private void DrawCreatorSidebarTab(string title, float top, float margin)
    {
        float ps = CreatorPixelScale;
        var size = new Vector2(30f, 72f) * ps;
        ImGui.SetNextWindowPos(new Vector2(margin, top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##creator-sidebar-tab", flags))
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetWindowPos();
            dl.AddRectFilled(min, min + size, AtlasU(new Vector4(28 / 255f, 23 / 255f, 18 / 255f, AtlasPanelAlpha)), 8f * ps);
            dl.AddRect(min, min + size, AtlasU(AtlasInk, 0.12f), 8f * ps, ImDrawFlags.None, 1f);
            if (AtlasIconButton(dl, "##creator-sidebar-unfold", min + new Vector2(3f * ps, (size.Y - 24f * ps) * 0.5f), 24f * ps,
                    AtlasGlyph.Unfold, $"Show {title}", observe: "Unfold sidebar"))
            {
                Settings.Creator.SidebarCollapsed = false;
                SettingsFile?.Save();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
        _creatorSidebarRight = margin + size.X;
    }

    /// <summary>Atlas section header (.qa-list-head): hairline on top, chevron, small uppercase label; hover lifts it.</summary>
    private void DrawAtlasSectionHeaderVisual(ImDrawListPtr dl, Vector2 pos, float width, float h, string label, bool open, bool hovered,
        bool nested)
    {
        float ps = CreatorPixelScale;
        if (hovered) dl.AddRectFilled(pos, pos + new Vector2(width, h), AtlasU(AtlasInk, 0.06f), 5f * ps);
        if (!nested) dl.AddLine(pos, pos + new Vector2(width, 0f), AtlasU(AtlasInk, 0.12f), 1f);
        uint col = AtlasU(hovered ? AtlasGold : nested ? AtlasInkDim : AtlasInkFaint);
        float g = MathF.Min(h * 0.18f, 5f * ps);
        var c = pos + new Vector2(6f * ps + g, h * 0.5f);
        float stroke = MathF.Max(1.3f, 1.4f * ps);
        if (open) { dl.AddLine(c + new Vector2(-g, -g * 0.5f), c + new Vector2(0f, g * 0.5f), col, stroke); dl.AddLine(c + new Vector2(0f, g * 0.5f), c + new Vector2(g, -g * 0.5f), col, stroke); }
        else { dl.AddLine(c + new Vector2(-g * 0.5f, -g), c + new Vector2(g * 0.5f, 0f), col, stroke); dl.AddLine(c + new Vector2(g * 0.5f, 0f), c + new Vector2(-g * 0.5f, g), col, stroke); }
        float textX = c.X + g + 8f * ps;
        if (nested)
        {
            dl.AddText(new Vector2(textX, pos.Y + (h - ImGui.GetTextLineHeight()) * 0.5f), AtlasU(hovered ? AtlasGold : AtlasInk), label);
        }
        else
        {
            float size = ImGui.GetFontSize() * 0.9f;
            AtlasSmallCaps(dl, new Vector2(textX, pos.Y + (h - size) * 0.5f), label, hovered ? AtlasGold : AtlasInkDim, size);
        }
    }
}
