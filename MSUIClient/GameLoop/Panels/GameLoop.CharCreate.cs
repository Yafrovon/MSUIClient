using System;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;      // GlueBooth / BoothTune
using MSUIClient.Engine.UI;   // WowSkin
using MSUIClient.Net;         // NetworkClient / CharCreateParams
using MSUIClient.Formats;     // CharCreateCatalog / GlueStrings
using MSUIClient.World.Units; // CharacterEquipment (starting outfit)

namespace MSUIClient;

// The 1.12 "Create New Character" screen. Full-bleed skinned chrome over the per-race 3D booth,
// the same immediate-mode WowSkin approach as DrawCharacterSelect, scaled to a 1024x768 glue canvas.
//
// Grounded in benilla (the byte-faithful reference on the device at C:\Users\nico\Desktop\benilla-main)
// and SPEC_CHARACTER_CREATE.md. Every mechanism cites where it comes from:
//   * State + flow            = char_create/mod.rs (CreateSelection, reset/clamp/cycle_dial/randomize,
//                               request(), the click dispatch, name rules, result-code table).
//   * Class list per race     = CharBaseInfo.dbc via CharCreateCatalog (verified byte layout).
//   * Dial ranges per race,sex= CharSections / CharHairGeosets / CharacterFacialHairStyles via the catalog.
//   * CMSG_CHAR_CREATE wire    = benilla-protocol messages/client.rs char_create (name + 9 bytes,
//                               outfit_id=0); handled by WorldSession.CreateCharacter / NetworkClient.
//   * Preview model            = the SAME GlueBooth char-select uses, driven by the create selection.
//
// The network stays PARKED at CharacterSelect the whole time (benilla carries the create request over
// the same pick channel the select screen uses); this screen is a client-side overlay gated by
// _charCreateOpen. A success re-enums the roster and returns to select with the new row armed.
//
// IMPLEMENTED: race/class/gender icon sheets + faction banners (UI-CharacterCreate-* via GlueImageUv),
// the GlueStrings faction/race/class paragraphs + racial abilities, the class starting outfit on the
// preview (CharStartOutfit.dbc -> ItemDisplayInfo), the ornate tower/label/rotate art, drag-to-rotate.
// Glue interactions use the shared client-data SoundEntries path, which is alive before world entry.
// Layout numbers are live-tunable via CreateTune (the "tune" toggle, top-right) - dial in, Log, bake.
public sealed partial class GameLoop
{
    private bool _charCreateOpen;
    private readonly CharCreateState _cc = new();
    private CharCreateCatalog? _ccCatalog;
    private bool _ccCatalogLoaded;
    private readonly byte[] _ccNameBuf = new byte[CharCreateUiLaw.MaxNameLength + 1];
    private ImGuiInputTextCallback? _ccNameFilterCallback;
    private string _ccStatus = "";
    private string? _ccArmName;          // name of a just-created character to select on return to the roster
    private bool _ccNameSubmit;          // the name box's Enter this frame
    private float _ccBlink;
    private readonly Random _ccRng = new();
    private GlueStrings? _ccStrings;
    private readonly float[] _ccPanelScroll = new float[3];   // per info-panel wheel-scroll offset
    private bool _ccFocusName;                                // focus the name box on screen entry
    private bool _ccTuneOpen;                                 // create-screen layout tuning modal

    private const byte CharCreateSuccessCode = 0x2E;   // benilla-protocol CHAR_CREATE_SUCCESS

    /// <summary>Open the create screen from character-select. Loads the catalog + resets the selection.</summary>
    private void OpenCharCreate()
    {
        PlayUiSound(CharSelectUiLaw.CreateSound, CharSelectUiLaw.SoundCategory);
        EnsureCatalog();
        _cc.Reset(_ccCatalog);
        WriteBuf(_ccNameBuf, "");
        _ccStatus = "";
        _ccBlink = 0f;
        BoothTune.CharYawDegrees = 0f;   // benilla resets facing on entry (SetCharacterCreateFacing -15)
        _ccFocusName = true;
        _charCreateOpen = true;
    }

    private void EnsureCatalog()
    {
        if (_ccCatalogLoaded) return;
        _ccCatalogLoaded = true;
        try { _ccCatalog = CharCreateCatalog.Load(_config.ClientDataPath); }
        catch (Exception e) { Console.WriteLine($"[charcreate] catalog load failed: {e.Message}"); }
        try { _ccStrings = GlueStrings.Load(_config.ClientDataPath); }
        catch (Exception e) { Console.WriteLine($"[charcreate] GlueStrings load failed: {e.Message}"); }
    }

    /// <summary>Surface a SMSG_CHAR_CREATE result (polled by PumpNet). Success -> arm the new row + close.</summary>
    private void OnCreateResult(byte code)
    {
        _lastCreateResult = code;   // the live runner's char-create step reads it (PumpNet takes the wire result first)
        _cc.Creating = false;
        _ccStatus = CharResultText(code);
        Console.WriteLine($"[charcreate] result 0x{code:X2} - {_ccStatus}");
        if (code == CharCreateSuccessCode)
        {
            _ccArmName = _cc.Name;    // pick the new character when the refreshed roster arrives
            _charCreateOpen = false;
        }
    }

    // ── The screen ──────────────────────────────────────────────────────────────────────────────

    private void DrawCharacterCreate()
    {
        var io = ImGui.GetIO();
        Vector2 disp = io.DisplaySize;
        var host = CharCreateUiLaw.Host(disp);
        float s = MathF.Max(disp.Y / GlueCanvasH, 0.5f);
        _ccBlink += io.DeltaTime;
        EnsureCatalog();
        var cat = _ccCatalog;

        ImGui.SetNextWindowPos(host.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(host.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-charcreate", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;

        // ── Left configuration tower (benilla screen.rs left_tower, 206x600 at TOPLEFT (28,74)) ──
        float towerX = CreateTune.TowerX * s, towerTop = CreateTune.TowerTop * s, towerW = CreateTune.TowerW * s;
        var tMin = new Vector2(towerX, towerTop);
        var tMax = new Vector2(towerX + towerW, towerTop + CreateTune.TowerH * s);
        if (_skin is not null && _skin.Has("cc.bg"))
        {
            // The OG ornate frame, kept proportional to the (tunable) tower: benilla screen.rs draws the
            // Background at -3,0 218x680 and the OuterBorder as 3 stacked pieces (x-9 w224 tops 0/236/476
            // heights 236/240/210) for a 206x600 tower; here bg/border track TowerW and TowerH.
            float bf = CreateTune.TowerH / 600f;
            _skin.GlueImage(dl, "cc.bg", new Vector2(towerX - 3f * s, towerTop),
                new Vector2(towerX - 3f * s + (CreateTune.TowerW + 12f) * s, towerTop + 680f * bf * s));
            if (_skin.Has("cc.border"))
            {
                (float Top, float H, Vector2 U0, Vector2 U1)[] slices =
                {
                    (0f, 236f, new Vector2(0f, 0f), new Vector2(0.875f, 0.9375f)),
                    (236f, 240f, new Vector2(0f, 0f), new Vector2(0.875f, 0.9375f)),
                    (476f, 210f, new Vector2(0f, 0.1796875f), new Vector2(0.875f, 1f)),
                };
                foreach (var sl in slices)
                    _skin.GlueImageUv(dl, "cc.border",
                        new Vector2(towerX - 9f * s, towerTop + sl.Top * bf * s),
                        new Vector2(towerX - 9f * s + (CreateTune.TowerW + 18f) * s, towerTop + (sl.Top + sl.H) * bf * s), sl.U0, sl.U1);
            }
        }
        else if (_skin is not null)
            _skin.DrawBackdrop(dl, tMin, tMax, WowSkin.GlueEditBox, WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);
        else
            dl.AddRectFilled(tMin, tMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)));

        // The WoW logo sits ON TOP of the tower's top, blended into the frame (the OG create-screen
        // look): drawn AFTER the frame so it overlaps it, centered over the tower and overhanging up.
        {
            float logoW = CreateTune.LogoW * s, logoH = logoW * 0.5f;   // glue.logo is 2:1 (256x128)
            float logoX = towerX + (towerW - logoW) * 0.5f + CreateTune.LogoDX * s;
            float logoY = CreateTune.LogoTop * s;
            _skin?.GlueImage(dl, "glue.logo", new Vector2(logoX, logoY), new Vector2(logoX + logoW, logoY + logoH));
        }

        // Drag the center model area to rotate the preview (benilla full-frame mouse rotation).
        float paneL2 = towerX + towerW + 12f * s;
        float paneR2 = disp.X - 20f * s - 240f * s - 12f * s;
        if (paneR2 > paneL2 + 40f * s)
        {
            ImGui.SetCursorScreenPos(new Vector2(paneL2, 80f * s));
            ImGui.InvisibleButton("##ccmodelpane", new Vector2(paneR2 - paneL2, MathF.Max(40f * s, disp.Y - 210f * s)));
            if (ImGui.IsItemActive())
            {
                float dx = ImGui.GetIO().MouseDelta.X;
                if (dx != 0f) BoothTune.CharYawDegrees += dx * 0.4f;
            }
        }

        float colAx = towerX + 8f * s;   // dial-label left edge
        float y = CreateTune.ContentTop * s;   // content starts below the logo (blended over the frame top)

        // Icon-cell geometry for the race/gender/class grids (falls back to text when a sheet is absent).
        float ic = CreateTune.IconSize * s, icGap = CreateTune.IconGap * s;
        float pairGap = CreateTune.RacePairGap * s, rGap = 4f * s;
        float pairW = 2f * ic + pairGap;
        float icAx = towerX + (towerW - pairW) * 0.5f;
        float icBx = icAx + ic + pairGap;

        // Faction banners (cc.banners) behind the race grid. The sheet holds BOTH banners side by
        // side, and it used to be drawn as ONE image stretched across the whole tower - so the two
        // could only ever move and scale together, and only by resizing the tower. Each half is now
        // drawn on its OWN column (left half = Alliance uv 0..0.5, right half = Horde 0.5..1), centred
        // on that column's icons, with its own width/height/offsets. BannerH 0 = auto-fit the grid.
        float bannerTop = y + CreateTune.BannerTop * s;
        float bannerH = CreateTune.BannerH > 0.01f
            ? CreateTune.BannerH * s
            : 20f * s + 4f * (ic + icGap) + 6f * s;
        float bannerW = CreateTune.BannerW * s;
        if (_skin is not null && _skin.Has("cc.banners"))
            for (int b = 0; b < 2; b++)
            {
                float cxb = (b == 0 ? icAx : icBx) + ic * 0.5f
                          + (b == 0 ? -CreateTune.BannerSpread : CreateTune.BannerSpread) * s
                          + CreateTune.BannerDX * s;
                _skin.GlueImageUv(dl, "cc.banners",
                    new Vector2(cxb - bannerW * 0.5f, bannerTop),
                    new Vector2(cxb + bannerW * 0.5f, bannerTop + bannerH),
                    new Vector2(b * 0.5f, 0f), new Vector2((b + 1) * 0.5f, 1f));
            }
        GlueText(dl, "Alliance", icAx + ic * 0.5f, y, CreateTune.HeaderPx * s, WowSkin.GlueGold, 1);
        GlueText(dl, "Horde", icBx + ic * 0.5f, y, CreateTune.HeaderPx * s, WowSkin.GlueGold, 1);
        y += 20f * s;

        // Race grid: Alliance [1,3,4,7] col A, Horde [2,5,6,8] col B (benilla mod.rs, GetAvailableRaces
        // order, pinned by mod.rs:625). Icons from UI-CharacterCreate-Races (4x4, female = row+2).
        for (int i = 0; i < 4; i++)
        {
            byte ar = CharCreateState.Alliance[i], hr = CharCreateState.Horde[i];
            float ry = y + i * (ic + icGap);
            if (RaceIconButton(dl, ar, new Vector2(icAx, ry), ic))
            {
                PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound);
                SelectRace(ar);
            }
            if (RaceIconButton(dl, hr, new Vector2(icBx, ry), ic))
            {
                PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound);
                SelectRace(hr);
            }
        }
        y += 4 * (ic + icGap) + 8f * s;

        // Gender pair (benilla screen.rs: [0 Male] [1 Female]). It used to reuse the RACE columns, so
        // the two sat as far apart as Alliance and Horde - in 1.12 they are a tight pair centred under
        // the grid. Own geometry now: GenderSize, GenderGap between them, the pair centred on the
        // tower with GenderDX to nudge, and GenderTop for the space above.
        float gs = CreateTune.GenderSize * s;
        float gSpan = gs * 2f + CreateTune.GenderGap * s;
        float gx = towerX + (towerW - gSpan) * 0.5f + CreateTune.GenderDX * s;
        float gy = y + CreateTune.GenderTop * s;
        if (GenderIconButton(dl, 0, new Vector2(gx, gy), gs))
        {
            PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound);
            SetGender(0);
        }
        if (GenderIconButton(dl, 1, new Vector2(gx + gs + CreateTune.GenderGap * s, gy), gs))
        {
            PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound);
            SetGender(1);
        }
        y = gy + gs + 10f * s;

        // Class grid: only the race's valid classes (benilla enumerate-then-hide; CharBaseInfo order).
        // 3-wide of UI-CharacterCreate-Classes cells (class_tc).
        GlueText(dl, "Class", towerX + towerW * 0.5f, y, 12f * s, WowSkin.Muted, 1);
        y += 15f * s;
        var classes = _cc.RaceClasses(cat);
        float classW = 3f * ic + 2f * icGap;
        float clx0 = towerX + (towerW - classW) * 0.5f;
        for (int i = 0; i < classes.Count; i++)
        {
            byte cl = classes[i];
            float cx = clx0 + (i % 3) * (ic + icGap);
            float cyy = y + (i / 3) * (ic + icGap);
            if (ClassIconButton(dl, cl, new Vector2(cx, cyy), ic))
            {
                PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound);
                SetClass(cl);
            }
        }
        y += ((classes.Count + 2) / 3) * (ic + icGap) + 10f * s;

        // The five appearance dials: skin, face, hair style, hair color, facial hair (benilla dials[0..5]).
        int[] counts = _cc.DialCounts(cat);
        string[] labels = DialLabels();
        float dRowH = CreateTune.DialRowH * s;
        float dLabelPx = CreateTune.DialLabelPx * s;
        for (int d = 0; d < 5; d++)
        {
            float dy = y + d * (dRowH + rGap);
            int n = Math.Max(1, counts[d]);
            float aw = CreateTune.DialArrowW * s, ah = CreateTune.DialArrowH * s;
            // SOLVE THE ARROW BLOCK FIRST, then hang the plate off it. The two arrows used to be
            // placed from the tower's right edge with a hardcoded 6px inset and a hardcoded 2px space
            // between them, so the space could never be scaled with the arrows - resize them and the
            // pair stopped reading as a matched set (Nico: "fixed margin between them, I cannot get
            // them proportionally the same"). Now: DialArrowRight is the right arrow's inset from the
            // tower edge, DialArrowGap the space BETWEEN the two, DialArrowDY a vertical nudge off the
            // row centre. Everything is in the same 1024x768 units as the arrow size, so scaling the
            // pair proportionally is just scaling W/H/Gap together.
            float rightX = towerX + towerW - CreateTune.DialArrowRight * s;
            float rArrowX = rightX - aw;
            float lArrowX = rArrowX - aw - CreateTune.DialArrowGap * s;

            // The label plate's edges are dials too. DialPlateLeft slides the left edge off the icon
            // column; DialPlateGap is the clearance kept from the LEFT ARROW (not from a guessed arrow
            // block), so the plate now tracks the arrows automatically however they are resized.
            float lfL = colAx + CreateTune.DialPlateLeft * s;
            float lfR = lArrowX - CreateTune.DialPlateGap * s;
            float ty = dy + (dRowH - dLabelPx) * 0.5f;

            // The dial label plate (CharacterCreate-LabelFrame, 25|stretch|25 3-slice); arrows outside it.
            if (_skin is not null && _skin.Has("cc.labelframe"))
            {
                // Height = the row height plus DialPlatePadY above AND below, so the plate stays
                // centred on the row's text and arrows however tall it gets. Raise DialRowH to move
                // the rows apart as well; raise the pad to grow only the box.
                float cap = 20f * s;
                float lfT = dy - CreateTune.DialPlatePadY * s, lfB = dy + dRowH + CreateTune.DialPlatePadY * s;
                _skin.GlueImageUv(dl, "cc.labelframe", new Vector2(lfL, lfT), new Vector2(lfL + cap, lfB), new Vector2(0f, 0f), new Vector2(0.1953125f, 1f));
                _skin.GlueImageUv(dl, "cc.labelframe", new Vector2(lfL + cap, lfT), new Vector2(lfR - cap, lfB), new Vector2(0.1953125f, 0f), new Vector2(0.8046875f, 1f));
                _skin.GlueImageUv(dl, "cc.labelframe", new Vector2(lfR - cap, lfT), new Vector2(lfR, lfB), new Vector2(0.8046875f, 0f), new Vector2(1f, 1f));
            }

            // Label centred on the plate minus the value slot; the gold value right-aligned inside the
            // plate's right edge. Both insets used to be hardcoded (34 and 5), so widening the box left
            // the gold "1/10" pinned to the rounded cap with no way to pull it back in - Nico's "I can't
            // move the yellow text, so it gets stuck when I adjust". DialValueInset is that inset;
            // DialValueZone is how much room the label leaves it, so the two never collide.
            float valZone = CreateTune.DialValueZone * s;
            GlueText(dl, labels[d], (lfL + lfR - valZone) * 0.5f, ty, dLabelPx, WowSkin.Normal, 1);
            GlueText(dl, $"{_cc.Dials[d] + 1}/{n}", lfR - CreateTune.DialValueInset * s, ty,
                     dLabelPx * 0.85f, WowSkin.GlueGold, 2);
            // The spinner pair. benilla char_create/screen.rs dial_row draws these as the OG 32x32
            // Glue-{Left,Right}Arrow-Button art (glue/art.rs `fn arrow`, glue/widgets.rs `dial_arrow`)
            // - the yellow-on-stone arrows, NOT the white text "<" / ">" this used to draw. The text
            // TowerButton is kept as the no-art fallback, exactly as the reference falls back.
            float ay = dy + (dRowH - ah) * 0.5f + CreateTune.DialArrowDY * s, agly = ah * 0.55f;
            var lPos = new Vector2(lArrowX, ay);
            var rPos = new Vector2(rArrowX, ay);
            var aSize = new Vector2(aw, ah);

            if (_skin is not null && _skin.GlueArrowButton(dl, $"##dial{d}L", true, lPos, aSize, out bool lHit))
            {
                if (lHit)
                {
                    PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound);
                    _cc.CycleDial(cat, d, -1);
                }
            }
            else if (TowerButton(dl, $"##dial{d}L", "<", lPos, aSize, false, agly))
            {
                PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound);
                _cc.CycleDial(cat, d, -1);
            }

            if (_skin is not null && _skin.GlueArrowButton(dl, $"##dial{d}R", false, rPos, aSize, out bool rHit))
            {
                if (rHit)
                {
                    PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound);
                    _cc.CycleDial(cat, d, +1);
                }
            }
            else if (TowerButton(dl, $"##dial{d}R", ">", rPos, aSize, false, agly))
            {
                PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound);
                _cc.CycleDial(cat, d, +1);
            }
        }
        y += 5 * (dRowH + rGap) + 8f * s;

        // Randomize (benilla screen.rs: a 146x30 Small glue button centred under the dials). Its size
        // was hardcoded at 146x28 with no way to grow it; both axes plus the gap above are dials now.
        float rndW = CreateTune.RandomW * s, rndH = CreateTune.RandomH * s;
        ImGui.SetCursorScreenPos(new Vector2(towerX + (towerW - rndW) * 0.5f, y + CreateTune.RandomTop * s));
        if (_skin?.GlueButton("Randomize", new Vector2(rndW, rndH)) ?? false)
        {
            PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound);
            _cc.Randomize(cat, _ccRng);
            // The preview rebuilds off _cc.Dials every frame (Program.Net.cs SetCreateLook), so this is
            // all the button has to do - but log it, because "Randomize does nothing" is impossible to
            // tell apart from "it rolled the same values" without seeing the numbers.
            Console.WriteLine($"[cc] randomize -> dials {_cc.Dials[0]}/{_cc.Dials[1]}/{_cc.Dials[2]}/" +
                              $"{_cc.Dials[3]}/{_cc.Dials[4]}");
        }

        // ── Right info panels (titled + faction-tinted; paragraphs deferred, SPEC section 11) ──
        DrawCreateInfoPanels(dl, disp, s);

        // ── Name + status (bottom-center) ──
        DrawCreateNameBox(dl, disp, s);

        // ── Rotate pair (bottom of the tower) - drives the shared BoothTune.CharYawDegrees like select ──
        // The pair is CENTRED ON THE TOWER rather than pinned to its left edge, which is why it drifted
        // off-centre as soon as the tower was resized. RotDX nudges it off that centre; RotSize and
        // RotGap set the buttons and the space between them; RotBottom is the margin off the screen
        // bottom. Gap is signed - the OG pair slightly overlaps, so the baked default is negative.
        float rot = CreateTune.RotSize * s;
        float rotSpan = rot * 2f + CreateTune.RotGap * s;
        float rotX = towerX + (towerW - rotSpan) * 0.5f + CreateTune.RotDX * s;
        float rotY = disp.Y - CreateTune.RotBottom * s - rot;
        RotateButton(dl, "##ccRotL", true, new Vector2(rotX, rotY), rot);
        RotateButton(dl, "##ccRotR", false, new Vector2(rotX + rot + CreateTune.RotGap * s, rotY), rot);

        // ── Accept over Back (bottom-right; benilla screen.rs BOTTOMRIGHT (-50,20)) ──
        var actions = CharCreateUiLaw.Actions(disp, s, GlueTune.ButtonHeightMul);
        bool acceptEnabled = !_cc.Creating && _cc.Name.Length >= 2;
        bool doCreate = false, doBack = false;
        ImGui.SetCursorScreenPos(actions.Accept.Min);
        if (_skin?.GlueButton("Accept", actions.Accept.Size, acceptEnabled) ?? false) doCreate = true;
        ImGui.SetCursorScreenPos(actions.Back.Min);
        if (_skin?.GlueButton("Back", actions.Back.Size) ?? false) doBack = true;

        // Keyboard: Enter = Create, Escape = Back (benilla mod.rs name-entry keys).
        if (_ccNameSubmit) doCreate = true;
        if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) doCreate = true;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) doBack = true;

        if (doBack)
        {
            PlayCharCreateSound(CharCreateUiLaw.CancelSound);
            _charCreateOpen = false;
            _ccStatus = "";
        }
        else if (doCreate && acceptEnabled && _net is not null)
        {
            PlayCharCreateSound(CharCreateUiLaw.CreateSound);
            _cc.Creating = true;
            _ccStatus = "Creating character...";
            _net.CreateCharacter(_cc.Request());
        }

        // Dev: the layout-tuning toggle (small "tune" top-right, like the booth/login).
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 58f * s, 6f * s));
        if (ImGui.InvisibleButton("##cc-tune-toggle", new Vector2(52f * s, 18f * s)))
            _ccTuneOpen = !_ccTuneOpen;
        GlueText(dl, "tune", disp.X - 6f * s, 6f * s, 12f * s, (_ccTuneOpen || ImGui.IsItemHovered()) ? WowSkin.Highlight : WowSkin.Muted, 2);

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    private void PlayCharCreateSound(string cue) =>
        PlayUiSound(cue, CharCreateUiLaw.SoundCategory);

    /// <summary>The three right-hand info panels: faction/race/class, faction-tinted, each with the
    /// title + the GlueStrings paragraph (FACTION_INFO/RACE_INFO/CLASS) word-wrapped and clipped, plus
    /// the gold racial-ability lines under the race body. Wheel-scroll a hovered panel. Keys are the
    /// benilla refresh.rs set; race/class tokens verified against his GlueStrings.lua.</summary>
    private void DrawCreateInfoPanels(ImDrawListPtr dl, Vector2 disp, float s)
    {
        var gs = _ccStrings;
        bool alliance = Array.IndexOf(CharCreateState.Alliance, _cc.Race) >= 0;
        Vector4 tint = alliance ? new Vector4(0.09f, 0.09f, 0.19f, 0.92f) : new Vector4(0.19f, 0.05f, 0.05f, 0.92f);
        string file = RaceFileToken(_cc.Race);

        string factionBody = gs?.Text(alliance ? "FACTION_INFO_ALLIANCE" : "FACTION_INFO_HORDE", "") ?? "";
        string raceBody = gs?.Text($"RACE_INFO_{file}", "") ?? "";
        string classBody = gs?.Text($"CLASS_{ClassFileToken(_cc.Class)}", "") ?? "";
        string abilities = "";
        if (gs is not null)
        {
            var sb = new StringBuilder();
            for (int n = 1; ; n++)
            {
                string? a = gs.Get($"ABILITY_INFO_{file}{n}");
                if (a is null) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(a);
            }
            abilities = sb.ToString();
        }

        (string Title, string Body, string Gold, int Idx)[] panels =
        {
            (alliance ? "Alliance" : "Horde", factionBody, "", 0),
            (RaceName(_cc.Race), raceBody, abilities, 1),
            (ClassName(_cc.Class), classBody, "", 2),
        };

        float titlePx = CreateTune.TitlePx * s, bodyPx = CreateTune.BodyPx * s;
        float w = CreateTune.PanelW * s, x = disp.X - CreateTune.PanelRight * s - w, y = CreateTune.PanelTop * s;
        float lineRatio = MathF.Max(1f, CreateTune.PanelLineH);
        foreach (var p in panels)
        {
            // FIXED HEIGHTS, NOT AUTO-SIZE (benilla char_create/panels.rs right_stack: 240 wide at
            // TOPRIGHT (-20,-20), heights 160 / 260 / 210, 10 apart). The boxes previously grew to fit
            // their text, so raising the body size resized the layout instead of scrolling - Nico's
            // "the box sizes shouldn't increase if I increase text size, but rather the correct scroll
            // look". Each height is its own dial; PanelAutoSize brings the old behaviour back.
            float bodyW = w - CreateTune.PanelScrollLeft * s - CreateTune.PanelScrollRight * s;
            float contentH = titlePx + CreateTune.PanelBodyGap * s
                           + MeasureWrappedText(p.Body.TrimStart(), bodyW, bodyPx, lineRatio);
            if (p.Gold.Length > 0)
                contentH += CreateTune.PanelBodyGap * s + MeasureWrappedText(p.Gold, bodyW, bodyPx, lineRatio);

            float fixedH = p.Idx == 0 ? CreateTune.PanelFactionH
                         : p.Idx == 1 ? CreateTune.PanelRaceH
                         :              CreateTune.PanelClassH;
            float panelH = CreateTune.PanelAutoSize
                ? Math.Clamp(contentH + CreateTune.PanelTitleTop * s + 10f * s,
                             CreateTune.PanelMinH * s, CreateTune.PanelMaxH * s)
                : fixedH * s;

            var min = new Vector2(x, y);
            var max = new Vector2(x + w, y + panelH);
            if (_skin is not null)
                _skin.DrawBackdrop(dl, min, max, WowSkin.GlueEditBox, tint, WowSkin.GlueBoxBorder);
            else
                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(tint));

            float titleX = x + 16f * s;
            (string Key, Vector2 U0, Vector2 U1) hdr =
                p.Idx == 0 ? ("cc.factions", alliance ? new Vector2(0f, 0f) : new Vector2(0.5f, 0f), alliance ? new Vector2(0.5f, 1f) : new Vector2(1f, 1f))
              : p.Idx == 1 ? ("cc.races", RaceIconUv(_cc.Race, _cc.Sex).Uv0, RaceIconUv(_cc.Race, _cc.Sex).Uv1)
              :              ("cc.classes", ClassIconUv(_cc.Class).Uv0, ClassIconUv(_cc.Class).Uv1);
            if (_skin is not null && _skin.Has(hdr.Key))
            {
                // The header icon sits on the panel's top-left corner, overhanging it. Its offset and
                // size were hardcoded (-4, -6, 42), so moving the PANEL moved the icon with it and there
                // was no way to lift the icon on its own - Nico's "I can move the box but not the icon".
                // Now all three are live dials. The title indent FOLLOWS the icon's right edge, so
                // resizing or sliding the icon never runs the title into it.
                var ip = new Vector2(x + CreateTune.PanelIconDX * s, y + CreateTune.PanelIconTop * s);
                var iszp = new Vector2(CreateTune.PanelIconSize * s, CreateTune.PanelIconSize * s);
                dl.AddRectFilled(ip, ip + iszp, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.4f)), 3f * s);
                _skin.GlueImageUv(dl, hdr.Key, ip, ip + iszp, hdr.U0, hdr.U1);
                titleX = x + (CreateTune.PanelIconDX + CreateTune.PanelIconSize + 6f) * s;
            }
            GlueText(dl, p.Title, titleX, y + CreateTune.PanelTitleTop * s, titlePx, WowSkin.GlueGold, 0);

            // The body block starts a settable gap below the TITLE (benilla stacks title -> body ->
            // abilities in one column inside the scroll frame). PanelTitleTop moves the title,
            // PanelBodyGap the space under it, PanelScrollLeft the text's left edge - so the start
            // point of the body is fully positionable instead of a hardcoded band.
            float bodyTop = y + (CreateTune.PanelTitleTop + CreateTune.PanelBodyGap) * s + titlePx;
            float bodyBottom = max.Y - CreateTune.PanelScrollBottom * s;
            float bodyLeft = x + CreateTune.PanelScrollLeft * s;
            float visH = bodyBottom - bodyTop;
            float scrollH = contentH - titlePx - CreateTune.PanelBodyGap * s;   // the body block alone
            float maxScroll = MathF.Max(0f, scrollH - visH);
            float scroll = _ccPanelScroll[p.Idx];
            if (ImGui.IsMouseHoveringRect(new Vector2(min.X, bodyTop), new Vector2(max.X, bodyBottom)))
            {
                float wheel = ImGui.GetIO().MouseWheel;
                if (wheel != 0f) scroll -= wheel * 24f * s;
            }
            // The bar is resolved BEFORE the text so an arrow click or a knob drag lands on THIS
            // frame's draw, not the next one. It is a no-op (and invisible) when nothing scrolls.
            scroll = DrawPanelScrollBar(dl, min, max, s, Math.Clamp(scroll, 0f, maxScroll), maxScroll, visH);
            _ccPanelScroll[p.Idx] = scroll;

            dl.PushClipRect(new Vector2(min.X, bodyTop), new Vector2(max.X, bodyBottom), true);
            float cy = bodyTop - scroll;
            cy += DrawWrappedText(dl, p.Body.TrimStart(), bodyLeft, cy, bodyW, bodyPx, WowSkin.Normal, lineRatio);
            if (p.Gold.Length > 0)
            {
                cy += CreateTune.PanelBodyGap * s;
                DrawWrappedText(dl, p.Gold, bodyLeft, cy, bodyW, bodyPx, WowSkin.GlueGold, lineRatio);
            }
            dl.PopClipRect();

            y += panelH + CreateTune.PanelGap * s;
        }
    }

    /// <summary>
    /// One info panel's 1.12 scrollbar, resolved to panel coordinates from benilla
    /// char_create/panels.rs `fn scrollbar`: the decorative CharacterCreate track art behind, then a
    /// 16-wide slider column inset 10 top and bottom - up button, knob track, down button. The whole
    /// bar is HIDDEN while the panel fits its text (the ref's `scrollBarHideable`), which is why this
    /// returns early on maxScroll 0.
    ///
    /// The buttons and knob are `Interface\Buttons\UI-ScrollBar-*` 32x32 sheets whose CENTRE QUARTER
    /// is the 16x16 button (GlueScrollBarButton's texcoords, benilla SCROLL_BTN_TC = 0.25..0.75) -
    /// drawing the whole sheet instead gets you a button ringed by its own transparent margin.
    ///
    /// Returns the (possibly changed) scroll offset: the arrows step half a track, the knob drags.
    /// </summary>
    private float DrawPanelScrollBar(ImDrawListPtr dl, Vector2 min, Vector2 max, float s,
                                     float scroll, float maxScroll, float visH)
    {
        if (maxScroll <= 0f || _skin is null) return scroll;

        float panelH = max.Y - min.Y;
        float inset = CreateTune.PanelScrollBottom * s;
        float barX = max.X - CreateTune.PanelBarRight * s;
        float barTop = min.Y + inset;
        float barH = panelH - inset * 2f;
        float btn = CreateTune.PanelBarW * s;
        float trackH = barH - btn * 2f;
        if (trackH <= btn) return scroll;

        var uv0 = new Vector2(0.25f, 0.25f);
        var uv1 = new Vector2(0.75f, 0.75f);

        // Decorative track art behind the slider (panel x 204 for a 240-wide panel = 9 left of the bar).
        float trackX = barX - 9f * s;
        if (_skin.Has("cc.scrolltop"))
            _skin.GlueImage(dl, "cc.scrolltop", new Vector2(trackX, min.Y + 6f * s),
                            new Vector2(trackX + 32f * s, min.Y + 6f * s + 128f * s));
        if (_skin.Has("cc.scrollbot"))
            _skin.GlueImageUv(dl, "cc.scrollbot",
                new Vector2(trackX, max.Y - 8f * s - 123f * s), new Vector2(trackX + 30f * s, max.Y - 8f * s),
                new Vector2(0.53125f, 0.03125f), new Vector2(1f, 1f));

        float step = trackH * 0.5f;

        // Up / down buttons. InvisibleButton first so the panel's own wheel handler cannot eat them.
        var upPos = new Vector2(barX, barTop);
        var dnPos = new Vector2(barX, barTop + barH - btn);
        foreach (var (pos, key, dir) in new[] { (upPos, "scroll.up", -1f), (dnPos, "scroll.dn", +1f) })
        {
            ImGui.SetCursorScreenPos(pos);
            bool hit = ImGui.InvisibleButton($"##sb{key}{min.Y:F0}", new Vector2(btn, btn));
            bool held = ImGui.IsItemActive();
            string art = held && _skin.Has(key + ".dn") ? key + ".dn" : key;
            if (_skin.Has(art))
                _skin.GlueImageUv(dl, art, pos, pos + new Vector2(btn, btn), uv0, uv1);
            else
                dl.AddRectFilled(pos, pos + new Vector2(btn, btn),
                                 ImGui.ColorConvertFloat4ToU32(WowSkin.GoldDim), 2f * s);
            if (hit) scroll = Math.Clamp(scroll + dir * step, 0f, maxScroll);
        }

        // The knob: a 16x16 sprite riding a (trackH - 16) travel, draggable.
        float travel = trackH - btn;
        float knobY = barTop + btn + travel * (maxScroll > 0f ? scroll / maxScroll : 0f);
        var knobPos = new Vector2(barX, knobY);
        ImGui.SetCursorScreenPos(knobPos);
        ImGui.InvisibleButton($"##sbknob{min.Y:F0}", new Vector2(btn, btn));
        if (ImGui.IsItemActive() && travel > 0.5f)
            scroll = Math.Clamp(scroll + ImGui.GetIO().MouseDelta.Y / travel * maxScroll, 0f, maxScroll);
        knobY = barTop + btn + travel * (maxScroll > 0f ? scroll / maxScroll : 0f);
        knobPos = new Vector2(barX, knobY);
        if (_skin.Has("scroll.knob"))
            _skin.GlueImageUv(dl, "scroll.knob", knobPos, knobPos + new Vector2(btn, btn), uv0, uv1);
        else
            dl.AddRectFilled(knobPos, knobPos + new Vector2(btn, btn),
                             ImGui.ColorConvertFloat4ToU32(WowSkin.GlueGold), 2f * s);

        return scroll;
    }

    /// <summary>The name edit box (12-char ASCII-alpha, benilla mod.rs) with a gold "Name" label and
    /// the status line beneath (empty until a create fails - the ref's minimal error stand-in).</summary>
    private unsafe void DrawCreateNameBox(ImDrawListPtr dl, Vector2 disp, float s)
    {
        float cx = disp.X * 0.5f;
        float boxW = CreateTune.NameBoxW * s, boxH = 38f * s;
        float boxBottom = disp.Y - 74f * s;
        var min = new Vector2(cx - boxW * 0.5f, boxBottom - boxH);
        var max = new Vector2(cx + boxW * 0.5f, boxBottom);

        GlueText(dl, "Name", cx, min.Y - 24f * s, 18f * s, WowSkin.GlueGold, 1);
        if (_skin is not null)
            _skin.DrawBackdrop(dl, min, max, WowSkin.GlueEditBox, WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);
        else
        {
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)));
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(WowSkin.GoldDim));
        }

        float typedPx = 18f * s, baseFs = ImGui.GetFontSize();
        float wfScale = baseFs > 0f ? typedPx / baseFs : 1f;
        ImGui.SetWindowFontScale(wfScale);
        float inset = 15f * s, frameH = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(min.X + inset, min.Y + (boxH - frameH) * 0.5f));
        ImGui.SetNextItemWidth(boxW - inset - 8f * s);
        var clear = new Vector4(0f, 0f, 0f, 0f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, clear);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, clear);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, clear);
        ImGui.PushStyleColor(ImGuiCol.Text, WowSkin.Normal);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        if (_ccFocusName) { ImGui.SetKeyboardFocusHere(); _ccFocusName = false; }
        _ccNameFilterCallback ??= FilterCreateNameCharacter;
        _ccNameSubmit = ImGui.InputText("##ccname", _ccNameBuf, (uint)_ccNameBuf.Length,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackCharFilter, _ccNameFilterCallback);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        ImGui.SetWindowFontScale(1f);

        // Filter at insertion, including paste, so ImGui's active editor and the request
        // share one value. The native buffer capacity enforces the twelve-letter limit.
        _cc.Name = BufToString(_ccNameBuf);

        if (_ccStatus.Length > 0)
            GlueText(dl, _ccStatus, cx, boxBottom + 6f * s, 13f * s, WowSkin.Muted, 1);
    }

    private static unsafe int FilterCreateNameCharacter(ImGuiInputTextCallbackData* data) =>
        CharCreateUiLaw.AcceptsNameCharacter((char)data->EventChar) ? 0 : 1;

    // A small immediate-mode tower button: unique ImGui id, custom centered glyph/label, and a
    // selected/hover face. Used for the race/gender/class grids and the dial arrows (which repeat the
    // "<"/">" glyphs, so GlueButton's label-as-id can't be used). Same InvisibleButton + manual-draw
    // pattern DrawCharacterSelect uses for the roster rows.
    private bool TowerButton(ImDrawListPtr dl, string id, string label, Vector2 pos, Vector2 size, bool selected, float textPx)
    {
        float sc = _skin?.Scale ?? 1f;
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        var min = pos;
        var max = pos + size;
        Vector4 face = selected ? new Vector4(0.28f, 0.22f, 0.05f, 0.88f)
                     : hovered ? new Vector4(0.16f, 0.14f, 0.10f, 0.82f)
                     : new Vector4(0.06f, 0.05f, 0.04f, 0.70f);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(face), 3f * sc);
        Vector4 border = selected ? WowSkin.GlueGold : hovered ? WowSkin.Highlight : WowSkin.GoldDim;
        dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(border), 3f * sc, ImDrawFlags.None, selected ? 2f : 1f);
        Vector4 tcol = selected ? WowSkin.GlueGold : WowSkin.Normal;
        GlueText(dl, label, (min.X + max.X) * 0.5f, min.Y + (size.Y - textPx) * 0.5f, textPx, tcol, 1);
        return clicked;
    }

    private bool RaceIconButton(ImDrawListPtr dl, byte race, Vector2 pos, float sz)
    {
        var (uv0, uv1) = RaceIconUv(race, _cc.Sex);
        return IconCell(dl, $"##race{race}", "cc.races", uv0, uv1, RaceName(race), pos, sz, _cc.Race == race);
    }

    private bool GenderIconButton(ImDrawListPtr dl, byte sex, Vector2 pos, float sz)
    {
        var uv0 = sex == 0 ? new Vector2(0f, 0f) : new Vector2(0.5f, 0f);
        var uv1 = sex == 0 ? new Vector2(0.5f, 1f) : new Vector2(1f, 1f);
        return IconCell(dl, sex == 0 ? "##sexM" : "##sexF", "cc.gender", uv0, uv1, sex == 0 ? "Male" : "Female", pos, sz, _cc.Sex == sex);
    }

    private bool ClassIconButton(ImDrawListPtr dl, byte cls, Vector2 pos, float sz)
    {
        var (uv0, uv1) = ClassIconUv(cls);
        return IconCell(dl, $"##class{cls}", "cc.classes", uv0, uv1, ClassName(cls), pos, sz, _cc.Class == cls);
    }

    // One icon-sheet cell as a button: unique id, the sub-rect drawn on a dark plate, selected/hover
    // border. Falls back to the text TowerButton when the sheet didn't load (benilla no-art fallback).
    private bool IconCell(ImDrawListPtr dl, string id, string skinKey, Vector2 uv0, Vector2 uv1, string fallbackLabel, Vector2 pos, float sz, bool selected)
    {
        var size = new Vector2(sz, sz);
        float sc = _skin?.Scale ?? 1f;
        if (_skin is null || !_skin.Has(skinKey))
            return TowerButton(dl, id, fallbackLabel, pos, size, selected, 11f * sc);

        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        var min = pos;
        var max = pos + size;
        if (_skin.Has("cc.iconshadow"))
        {
            // The IconShadow ring behind the cell. It was a fixed 18% of the icon on every side, which
            // is a LOT wider than the ref's (benilla: a 64 shadow behind a 48 button = ~17% but offset
            // (-6,-6), so it hugs the top-left and barely shows) - and because the highlight square
            // only covers the ICON, that surplus ring never lights up. Result: a selected icon gained a
            // dark halo where 1.12 gains a bright one. Dial it down (or to 0) if it still reads heavy.
            var pad = new Vector2(sz * CreateTune.IconShadowPad, sz * CreateTune.IconShadowPad);
            _skin.GlueImage(dl, "cc.iconshadow", min - pad, max + pad);
        }
        else
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.35f)), 3f * sc);
        _skin.GlueImageUv(dl, skinKey, min, max, uv0, uv1);

        // 1.12 has NO border around a picked race/class/gender. benilla glue/widgets.rs icon_button:
        // the visual is `Interface\Buttons\ButtonHilight-Square` lit on hover and HELD while selected
        // (the ref's LockHighlight) - the template's CheckedTexture is commented out in the shipped
        // GlueXML, so that square is the entire selected state. The gold/white AddRect that used to
        // ring these was ours, not Blizzard's.
        if ((selected || hovered) && _skin.Has("btn.hilight.sq"))
        {
            // The sheet is rebuilt as a WHITE light mask at load (WowSkin.WhiteGlowFromLuma), so this
            // lerps the icon TOWARDS IconGlowColor and can only brighten - drawn with its own RGB it
            // was a dark blue-grey film that read as an inward shadow. Alpha clamps at 1, so redraw
            // once per whole unit of IconGlow to push the brightening past a single pass. Selected and
            // hovered deliberately share the value: in 1.12 the only difference is that the selected
            // one stays lit.
            // IconGlowBleed is the glow's SIZE relative to the icon cell, as a fraction of the icon:
            // POSITIVE spills past it (lighting the shadow ring, so a selected icon's surround reads
            // brighter than an unlit one rather than as a recess), NEGATIVE pulls it inside the cell,
            // 0 matches the cell exactly. It was a hardcoded pad before there was a dial for it.
            var glowCol = CreateTune.IconGlowColor;
            var bleed = new Vector2(sz * CreateTune.IconGlowBleed, sz * CreateTune.IconGlowBleed);
            for (float g = MathF.Max(CreateTune.IconGlow, 0.05f); g > 0.001f; g -= 1f)
                _skin.GlueImage(dl, "btn.hilight.sq", min - bleed, max + bleed,
                                new Vector4(glowCol.X, glowCol.Y, glowCol.Z,
                                            glowCol.W * MathF.Min(1f, g)));
        }

        // The NAME along the icon's bottom edge (benilla glue/widgets.rs icon_button: the ref's
        // `HighlightText`, GlueFontNormalSmall gold, anchored BOTTOM +1 so it sits OVER the edge).
        // Shown for the SELECTED icon and the hovered one - the same pair the highlight square lights,
        // so a lit box always carries its name. This is NOT a deviation: the 1.12 reference shot has
        // "Dwarf", "Female" and "Warrior" all lit at once, and the mouse can only be over one of them,
        // so the label tracks SELECTION and hover, not hover alone. (benilla names the marker
        // `HoverLabel`, which is what misled an earlier pass here - go by the screenshot.)
        // IconLabelsAlways names every icon at once instead. Drawn through GlueText so it carries the
        // same shadow + outline as every other gold string here, and AFTER the highlight square so
        // the glow cannot wash it out.
        if (selected || hovered || CreateTune.IconLabelsAlways)
            GlueText(dl, fallbackLabel, min.X + size.X * 0.5f,
                     max.Y - (CreateTune.HoverLabelPx + CreateTune.HoverLabelBottom) * sc,
                     CreateTune.HoverLabelPx * sc, WowSkin.GlueGold, 1);

        return clicked;
    }

    // Icon-sheet texcoords (benilla glue/art.rs race_tc / class_tc). Race sheet is 4x4, female = row+2.
    private static (int Col, int Row) RaceCell(byte race) => race switch
    {
        1 => (0, 0), 3 => (1, 0), 7 => (2, 0), 4 => (3, 0),
        6 => (0, 1), 5 => (1, 1), 8 => (2, 1), 2 => (3, 1), _ => (0, 0)
    };

    private static (Vector2 Uv0, Vector2 Uv1) RaceIconUv(byte race, byte sex)
    {
        var (col, row) = RaceCell(race);
        row += sex == 1 ? 2 : 0;
        return (new Vector2(col * 0.25f, row * 0.25f), new Vector2((col + 1) * 0.25f, (row + 1) * 0.25f));
    }

    private static (Vector2 Uv0, Vector2 Uv1) ClassIconUv(byte cls)
    {
        float[] tc = cls switch
        {
            1 => new[] { 0f, 0.25f, 0f, 0.25f },
            8 => new[] { 0.25f, 0.49609375f, 0f, 0.25f },
            4 => new[] { 0.49609375f, 0.7421875f, 0f, 0.25f },
            11 => new[] { 0.7421875f, 0.98828125f, 0f, 0.25f },
            3 => new[] { 0f, 0.25f, 0.25f, 0.5f },
            7 => new[] { 0.25f, 0.49609375f, 0.25f, 0.5f },
            5 => new[] { 0.49609375f, 0.7421875f, 0.25f, 0.5f },
            9 => new[] { 0.7421875f, 0.98828125f, 0.25f, 0.5f },
            2 => new[] { 0f, 0.25f, 0.5f, 0.75f },
            _ => new[] { 0f, 0.25f, 0f, 0.25f }
        };
        return (new Vector2(tc[0], tc[2]), new Vector2(tc[1], tc[3]));
    }

    // The big rotate button (UI-RotationRight-Big); the LEFT button is the same art mirrored. Held =
    // the Down art, and it rotates the preview while held (benilla ROTATE_RATE 120 deg/s).
    private void RotateButton(ImDrawListPtr dl, string id, bool left, Vector2 pos, float sz)
    {
        var size = new Vector2(sz, sz);
        ImGui.SetCursorScreenPos(pos);
        _ = ImGui.InvisibleButton(id, size);
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        var min = pos;
        var max = pos + size;
        string key = active && _skin is not null && _skin.Has("cc.rotate.down") ? "cc.rotate.down" : "cc.rotate.up";
        if (_skin is not null && _skin.Has(key))
        {
            Vector2 uv0 = left ? new Vector2(1f, 0f) : new Vector2(0f, 0f);
            Vector2 uv1 = left ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
            _skin.GlueImageUv(dl, key, min, max, uv0, uv1);
        }
        else
        {
            float sc = _skin?.Scale ?? 1f;
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.06f, 0.05f, 0.04f, 0.7f)), 3f * sc);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(hovered ? WowSkin.Highlight : WowSkin.GoldDim), 3f * sc);
            GlueText(dl, left ? "<" : ">", (min.X + max.X) * 0.5f, min.Y + (sz - 16f * sc) * 0.5f, 16f * sc, WowSkin.GlueGold, 1);
        }
        if (active)
            BoothTune.CharYawDegrees += (left ? 1f : -1f) * 120f * ImGui.GetIO().DeltaTime;
    }

    private void SelectRace(byte r)
    {
        if (_cc.Race == r) return;
        _cc.Race = r;
        var cl = _cc.RaceClasses(_ccCatalog);
        _cc.Class = cl.Count > 0 ? cl[0] : (byte)1;
        _cc.Clamp(_ccCatalog);
        BoothTune.CharYawDegrees = 0f;   // reset facing on race switch (benilla)
    }

    private void SetGender(byte g)
    {
        if (_cc.Sex == g) return;
        _cc.Sex = g;
        _cc.Clamp(_ccCatalog);
    }

    private void SetClass(byte c)
    {
        if (_cc.Class == c) return;
        _cc.Class = c;   // the class picks the starting outfit (preview re-dress = later pass)
    }

    private string[] DialLabels()
    {
        string hair = _ccCatalog?.HairCustomization(_cc.Race) ?? "NORMAL";
        string facial = _ccCatalog?.FacialHairCustomization(_cc.Race, _cc.Sex) ?? "NORMAL";
        return CharCreateUiLaw.DialLabels(_ccStrings, hair, facial);
    }

    /// <summary>Map a SMSG_CHAR_CREATE result byte to its 1.12 GlueStrings text (benilla char_create/mod.rs
    /// char_result_text, verbatim from GlueStrings.lua; codes are vmangos ResponseCodes,
    /// CHAR_CREATE_SUCCESS = 0x2E). Unknown codes fall back to the generic name error.</summary>
    private static string CharResultText(byte code) => code switch
    {
        0x2E => "Character created",
        0x2F => "Error creating character",
        0x30 => "Character creation failed",
        0x31 => "That name is unavailable",
        0x32 => "Creation of that race and/or class is currently disabled.",
        0x33 => "You cannot have both a Horde and an Alliance character on the same PvP server",
        0x34 => "You already have the maximum number of characters allowed on this realm.",
        0x35 => "You already have the maximum number of characters allowed on this account.",
        0x36 => "This server is currently queued and new character creation is temporarily disabled.",
        0x37 => "Only players who already have characters on this realm are currently allowed to create characters.",
        0x45 => "Enter a name for your character",
        0x46 => "Names must be at least 2 characters",
        0x47 => "Names must be no more than 12 characters",
        0x48 => "Names can only contain letters",
        0x49 => "Names must contain only one language",
        0x4A => "That name contains profanity",
        0x4B => "That name is unavailable",
        0x4C => "You cannot use an apostrophe as the first or last character of your name",
        0x4D => "You can only have one apostrophe",
        0x4E => "You cannot use the same letter three times consecutively",
        0x4F => "You cannot use a space as the first or last character of your name",
        0x50 => "You cannot use consecutive spaces in a name",
        _ => "Invalid character name",
    };

    // Word-wrap + draw a paragraph (with the glue drop shadow) within a width; returns the height used.
    private static float DrawWrappedText(ImDrawListPtr dl, string text, float x, float y, float width,
                                         float sizePx, Vector4 col, float lineRatio = 1.28f)
    {
        var font = ImGui.GetFont();
        float baseFs = ImGui.GetFontSize();
        float scale = baseFs > 0f ? sizePx / baseFs : 1f;
        float lineH = sizePx * lineRatio;
        float cy = y;
        foreach (string para in text.Split('\n'))
        {
            if (para.Length == 0) { cy += lineH; continue; }
            string cur = "";
            foreach (string word in para.Split(' '))
            {
                if (word.Length == 0) continue;
                string test = cur.Length == 0 ? word : cur + " " + word;
                if (ImGui.CalcTextSize(test).X * scale > width && cur.Length > 0)
                {
                    DrawTextShadow(dl, font, sizePx, new Vector2(x, cy), col, cur);
                    cy += lineH;
                    cur = word;
                }
                else cur = test;
            }
            if (cur.Length > 0) { DrawTextShadow(dl, font, sizePx, new Vector2(x, cy), col, cur); cy += lineH; }
        }
        return cy - y;
    }

    // Same wrapping as DrawWrappedText, but measures the height without drawing (for panel auto-size).
    private static float MeasureWrappedText(string text, float width, float sizePx, float lineRatio = 1.28f)
    {
        float baseFs = ImGui.GetFontSize();
        float scale = baseFs > 0f ? sizePx / baseFs : 1f;
        float lineH = sizePx * lineRatio;
        float h = 0f;
        foreach (string para in text.Split('\n'))
        {
            if (para.Length == 0) { h += lineH; continue; }
            string cur = "";
            foreach (string word in para.Split(' '))
            {
                if (word.Length == 0) continue;
                string test = cur.Length == 0 ? word : cur + " " + word;
                if (ImGui.CalcTextSize(test).X * scale > width && cur.Length > 0) { h += lineH; cur = word; }
                else cur = test;
            }
            if (cur.Length > 0) h += lineH;
        }
        return h;
    }

    private static void DrawTextShadow(ImDrawListPtr dl, ImFontPtr font, float sizePx, Vector2 pos, Vector4 col, string text)
    {
        float so = MathF.Max(1f, sizePx * 0.06f);
        dl.AddText(font, sizePx, pos + new Vector2(so, so), ImGui.ColorConvertFloat4ToU32(WowSkin.GlueShadow), text);
        WowSkin.OutlineText(dl, font, sizePx, pos, text);
        dl.AddText(font, sizePx, pos, ImGui.ColorConvertFloat4ToU32(col), text);
    }

    // ChrRaces file tokens (verified vs GlueStrings.lua RACE_INFO_* keys): Undead=SCOURGE, NightElf=NIGHTELF.
    private static string RaceFileToken(byte r) => r switch
    {
        1 => "HUMAN", 2 => "ORC", 3 => "DWARF", 4 => "NIGHTELF",
        5 => "SCOURGE", 6 => "TAUREN", 7 => "GNOME", 8 => "TROLL", _ => "HUMAN"
    };

    // ChrClasses file tokens (benilla class_file / GlueStrings CLASS_* keys).
    private static string ClassFileToken(byte c) => c switch
    {
        1 => "WARRIOR", 2 => "PALADIN", 3 => "HUNTER", 4 => "ROGUE", 5 => "PRIEST",
        7 => "SHAMAN", 8 => "MAGE", 9 => "WARLOCK", 11 => "DRUID", _ => "WARRIOR"
    };

    // Build the (race,class,sex) starting-outfit equipment from the catalog (CharStartOutfit.dbc).
    private CharacterEquipment BuildStartOutfit()
    {
        var kit = new CharacterEquipment();
        EnsureCatalog();
        if (_ccCatalog is null) return kit;
        int i = 0;
        foreach (var (disp, inv) in _ccCatalog.StartOutfit(_cc.Race, _cc.Class, _cc.Sex))
            kit.Add($"start{i++}", disp, inv);
        return kit;
    }

    /// <summary>Live layout tuning for the create screen (the "tune" toggle). Sliders write CreateTune,
    /// read next frame by the draw, so the boxes move as you drag; "Log" prints the set to bake as
    /// defaults. Same dev-scaffold pattern as the booth/login tuning modals.</summary>
    private void DrawCreateTuning()
    {
        if (!_ccTuneOpen) return;
        var tuningWindow = CharCreateUiLaw.TuningWindow;
        ImGui.SetNextWindowSize(tuningWindow.Size, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(tuningWindow.Min, ImGuiCond.FirstUseEver);
        _skin?.PushStyle();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.04f, 0.03f, 0.96f));
        bool open = _ccTuneOpen;
        if (ImGui.Begin("Create Screen Layout", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled("Drag to dial in the boxes; values apply live. Units are 1024x768 glue px.");
            ImGui.Spacing();
            ImGui.TextDisabled("LEFT TOWER");
            ImGui.SliderFloat("Tower X", ref CreateTune.TowerX, 0f, 140f);
            ImGui.SliderFloat("Tower top", ref CreateTune.TowerTop, 0f, 220f);
            ImGui.SliderFloat("Tower width", ref CreateTune.TowerW, 150f, 320f);
            ImGui.SliderFloat("Tower height", ref CreateTune.TowerH, 380f, 760f);
            ImGui.SliderFloat("Logo width", ref CreateTune.LogoW, 120f, 340f);
            ImGui.SliderFloat("Logo X nudge", ref CreateTune.LogoDX, -80f, 80f);
            ImGui.SliderFloat("Logo top", ref CreateTune.LogoTop, 0f, 120f);
            ImGui.SliderFloat("Content top", ref CreateTune.ContentTop, 90f, 240f);
            ImGui.SliderFloat("Header text px", ref CreateTune.HeaderPx, 10f, 26f);
            ImGui.Spacing();
            ImGui.TextDisabled("RIGHT INFO PANELS (auto-size to text, capped at max height)");
            ImGui.SliderFloat("Panel width", ref CreateTune.PanelW, 160f, 380f);
            ImGui.SliderFloat("Panel top", ref CreateTune.PanelTop, 0f, 140f);
            ImGui.SliderFloat("Panel right inset", ref CreateTune.PanelRight, 0f, 140f);
            ImGui.SliderFloat("Panel gap", ref CreateTune.PanelGap, 0f, 48f);
            ImGui.SliderFloat("Panel min height", ref CreateTune.PanelMinH, 40f, 200f);
            ImGui.SliderFloat("Panel max height", ref CreateTune.PanelMaxH, 120f, 560f);
            ImGui.Checkbox("Panels auto-size to text (off = fixed 1.12 heights)", ref CreateTune.PanelAutoSize);
            ImGui.SliderFloat("Panel H: faction", ref CreateTune.PanelFactionH, 80f, 420f);
            ImGui.SliderFloat("Panel H: race", ref CreateTune.PanelRaceH, 80f, 480f);
            ImGui.SliderFloat("Panel H: class", ref CreateTune.PanelClassH, 80f, 480f);
            ImGui.SliderFloat("Panel title top", ref CreateTune.PanelTitleTop, -20f, 60f);
            ImGui.SliderFloat("Panel body gap (under title)", ref CreateTune.PanelBodyGap, -20f, 60f);
            ImGui.SliderFloat("Panel text left inset", ref CreateTune.PanelScrollLeft, 0f, 80f);
            ImGui.SliderFloat("Panel text right inset", ref CreateTune.PanelScrollRight, 0f, 90f);
            ImGui.SliderFloat("Panel text bottom inset", ref CreateTune.PanelScrollBottom, 0f, 40f);
            ImGui.SliderFloat("Panel line spacing", ref CreateTune.PanelLineH, 1f, 2.2f);
            ImGui.SliderFloat("Panel scrollbar inset", ref CreateTune.PanelBarRight, 8f, 60f);
            ImGui.SliderFloat("Panel scrollbar width", ref CreateTune.PanelBarW, 8f, 32f);
            ImGui.SliderFloat("Panel icon top", ref CreateTune.PanelIconTop, -60f, 40f);
            ImGui.SliderFloat("Panel icon X", ref CreateTune.PanelIconDX, -60f, 40f);
            ImGui.SliderFloat("Panel icon size", ref CreateTune.PanelIconSize, 20f, 80f);
            ImGui.SliderFloat("Title text px", ref CreateTune.TitlePx, 10f, 28f);
            ImGui.SliderFloat("Body text px", ref CreateTune.BodyPx, 8f, 22f);
            ImGui.SliderFloat("Text outline px", ref GlueTune.OutlinePx, 0f, 0.14f);
            ImGui.SliderFloat("Button hover glow", ref GlueTune.HoverGlow, 0f, 4f);
            ImGui.Spacing();
            ImGui.TextDisabled("GRIDS / DIALS / NAME");
            ImGui.SliderFloat("Icon size", ref CreateTune.IconSize, 28f, 72f);
            ImGui.SliderFloat("Icon gap", ref CreateTune.IconGap, 0f, 30f);
            ImGui.SliderFloat("Race column gap", ref CreateTune.RacePairGap, 0f, 90f);
            ImGui.SliderFloat("Banner width", ref CreateTune.BannerW, 20f, 200f);
            ImGui.SliderFloat("Banner height (0 = auto)", ref CreateTune.BannerH, 0f, 500f);
            ImGui.SliderFloat("Banner top", ref CreateTune.BannerTop, -60f, 60f);
            ImGui.SliderFloat("Banner X nudge (both)", ref CreateTune.BannerDX, -80f, 80f);
            ImGui.SliderFloat("Banner spread (apart)", ref CreateTune.BannerSpread, -60f, 60f);
            ImGui.SliderFloat("Gender size", ref CreateTune.GenderSize, 20f, 90f);
            ImGui.SliderFloat("Gender gap", ref CreateTune.GenderGap, -20f, 90f);
            ImGui.SliderFloat("Gender X nudge", ref CreateTune.GenderDX, -120f, 120f);
            ImGui.SliderFloat("Gender top gap", ref CreateTune.GenderTop, -40f, 80f);
            ImGui.SliderFloat("Icon label px", ref CreateTune.HoverLabelPx, 8f, 24f);
            ImGui.SliderFloat("Icon label bottom", ref CreateTune.HoverLabelBottom, -20f, 30f);
            ImGui.Checkbox("Name EVERY icon (off = selected + hovered only)", ref CreateTune.IconLabelsAlways);
            ImGui.SliderFloat("Icon selected/hover glow", ref CreateTune.IconGlow, 0f, 8f);
            ImGui.ColorEdit4("Icon glow colour", ref CreateTune.IconGlowColor, ImGuiColorEditFlags.AlphaBar);
            ImGui.SliderFloat("Icon glow size (+/- x icon size)", ref CreateTune.IconGlowBleed, -0.45f, 0.45f);
            ImGui.SliderFloat("Icon shadow pad (x icon size)", ref CreateTune.IconShadowPad, 0f, 0.4f);
            ImGui.SliderFloat("Dial row height", ref CreateTune.DialRowH, 16f, 40f);
            ImGui.SliderFloat("Dial label px", ref CreateTune.DialLabelPx, 7f, 16f);
            ImGui.SliderFloat("Dial arrow width", ref CreateTune.DialArrowW, 12f, 40f);
            ImGui.SliderFloat("Dial arrow height", ref CreateTune.DialArrowH, 12f, 40f);
            ImGui.SliderFloat("Dial arrow gap (between)", ref CreateTune.DialArrowGap, -16f, 40f);
            ImGui.SliderFloat("Dial arrow right inset", ref CreateTune.DialArrowRight, -20f, 60f);
            ImGui.SliderFloat("Dial arrow Y nudge", ref CreateTune.DialArrowDY, -30f, 30f);
            ImGui.SliderFloat("Dial box pad Y (height)", ref CreateTune.DialPlatePadY, 0f, 30f);
            ImGui.SliderFloat("Dial box left edge", ref CreateTune.DialPlateLeft, -60f, 40f);
            ImGui.SliderFloat("Dial box gap to arrows", ref CreateTune.DialPlateGap, -10f, 60f);
            ImGui.SliderFloat("Dial value inset (yellow)", ref CreateTune.DialValueInset, -20f, 60f);
            ImGui.SliderFloat("Dial value zone (label clearance)", ref CreateTune.DialValueZone, 0f, 90f);
            ImGui.SliderFloat("Randomize width", ref CreateTune.RandomW, 80f, 320f);
            ImGui.SliderFloat("Randomize height", ref CreateTune.RandomH, 18f, 80f);
            ImGui.SliderFloat("Randomize top gap", ref CreateTune.RandomTop, -40f, 80f);
            ImGui.Spacing();
            ImGui.SliderFloat("Rotate size", ref CreateTune.RotSize, 20f, 90f);
            ImGui.SliderFloat("Rotate gap", ref CreateTune.RotGap, -30f, 40f);
            ImGui.SliderFloat("Rotate X nudge", ref CreateTune.RotDX, -200f, 200f);
            ImGui.SliderFloat("Rotate bottom margin", ref CreateTune.RotBottom, 0f, 160f);
            ImGui.SliderFloat("Name box width", ref CreateTune.NameBoxW, 120f, 420f);
            ImGui.Separator();
            if (ImGui.Button("Log values")) CreateTune.Log();
            ImGui.SameLine();
            if (ImGui.Button("Reset")) CreateTune.Reset();
            ImGui.SameLine();
            if (ImGui.Button("Close")) open = false;
        }
        ImGui.End();
        ImGui.PopStyleColor();
        _skin?.PopStyle();
        _ccTuneOpen = open;
    }
}

// The create screen's selection state - a faithful port of benilla char_create/mod.rs CreateSelection:
// race/sex/class + the five appearance dials [skin, face, hairStyle, hairColor, facialHair], the typed
// name, and the in-flight flag. reset/clamp/cycleDial/randomize/request mirror the reference exactly.
internal sealed class CharCreateState
{
    // benilla mod.rs ALLIANCE / HORDE columns (GetAvailableRaces order, ascending race id per faction).
    public static readonly byte[] Alliance = { 1, 3, 4, 7 };   // Human, Dwarf, Night Elf, Gnome
    public static readonly byte[] Horde = { 2, 5, 6, 8 };      // Orc, Scourge, Tauren, Troll

    public byte Race = 1;
    public byte Sex;                     // 0 male, 1 female
    public byte Class = 1;
    public readonly byte[] Dials = new byte[5];   // skin, face, hairStyle, hairColor, facialHair
    public string Name = "";
    public bool Creating;

    /// <summary>Reset to a valid default (Human, male, its first valid class, dials 0). benilla reset().</summary>
    public void Reset(CharCreateCatalog? cat)
    {
        Race = 1;
        Sex = 0;
        var cl = ClassesForRace(cat, 1);
        Class = cl.Count > 0 ? cl[0] : (byte)1;
        Array.Clear(Dials, 0, Dials.Length);
        Name = "";
        Creating = false;
    }

    /// <summary>Re-clamp class + dials into the current (race, sex) ranges after a race/gender change.</summary>
    public void Clamp(CharCreateCatalog? cat)
    {
        if (cat is not null && !cat.Allows(Race, Class))
        {
            var cl = cat.ClassesForRace(Race);
            Class = cl.Count > 0 ? cl[0] : (byte)1;
        }
        int[] counts = DialCounts(cat);
        for (int i = 0; i < 5; i++)
        {
            int n = counts[i];
            Dials[i] = n <= 0 ? (byte)0 : (byte)Math.Min(Dials[i], n - 1);
        }
    }

    public IReadOnlyList<byte> RaceClasses(CharCreateCatalog? cat) => ClassesForRace(cat, Race);

    // Fallback only when the catalog is missing: the 1.12 creatable table (benilla fell back to
    // every class, which offers pairings the server refuses).
    private static IReadOnlyList<byte> ClassesForRace(CharCreateCatalog? cat, byte race)
    {
        var list = cat?.ClassesForRace(race);
        if (list is { Count: > 0 }) return list;
        return CharCreateCatalog.Creatable112.TryGetValue(race, out byte[]? classes)
            ? classes : new byte[] { 1 };
    }

    public int[] DialCounts(CharCreateCatalog? cat) => cat?.DialCounts(Race, Sex) ?? new[] { 1, 1, 1, 1, 1 };

    /// <summary>Cycle one dial by dir, wrapping within 0..count (benilla cycle_dial rem_euclid).</summary>
    public void CycleDial(CharCreateCatalog? cat, int dial, int dir)
    {
        int count = Math.Max(1, DialCounts(cat)[dial]);
        int v = ((Dials[dial] + dir) % count + count) % count;
        Dials[dial] = (byte)v;
    }

    /// <summary>Every dial to a random valid index (benilla randomize).</summary>
    public void Randomize(CharCreateCatalog? cat, Random rng)
    {
        int[] counts = DialCounts(cat);
        for (int i = 0; i < 5; i++)
        {
            int n = counts[i];
            Dials[i] = n <= 0 ? (byte)0 : (byte)rng.Next(n);
        }
    }

    /// <summary>The CMSG_CHAR_CREATE request for the current selection (benilla request()).</summary>
    public CharCreateParams Request() =>
        new(Name, Race, Class, Sex, Dials[0], Dials[1], Dials[2], Dials[3], Dials[4]);
}
