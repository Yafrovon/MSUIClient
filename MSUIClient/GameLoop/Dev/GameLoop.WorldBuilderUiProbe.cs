using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using Silk.NET.Input;

namespace MSUIClient;

/// <summary>Opt-in human-path QA driven by MSUI_WB_SCRIPT. Injects pointer/keys at the existing
/// input seams; it never calls a brush, save, cancel or tool activation method directly.</summary>
public sealed partial class GameLoop
{
    private readonly Dictionary<string, (Vector2 Min, Vector2 Max, int Frame)> _wbUiItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Vector2 Point, float Height, float SavedDelta, int Map)> _wbUiMarks = new();
    private int _wbUiClickPhase;
    private double _wbUiNextPhaseAt;
    private bool _wbUiVisitedPublish;
    private int _wbUiFailures;
    private int _wbUiTextPhase;
    private string _wbUiReplacementText = "";
    private uint _wbUiNpcExpectedEntry;
    private readonly HashSet<ImGuiKey> _wbUiGuiKeys = new();
    private readonly Dictionary<string, MSUIClient.Net.WorldPackClient.Placement> _wbUiPlacementMarks = new();
    private uint _wbUiQuestExpectedEntry;

    /// <summary>Read-only observation after a real widget; normal sessions do no work.</summary>
    private void ObserveWorldBuilderUiItem(string label)
    {
        if (WbScriptPath is null || !ImGui.IsItemVisible()) return;
        var rect = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetFrameCount());
        _wbUiItems[label] = rect;
        string visible = label.Split("##", 2, StringSplitOptions.None)[0];
        if (visible.Length > 0) _wbUiItems[visible] = rect;
    }

    private bool PumpWorldBuilderUiProbe(double now)
    {
        _wbUiVisitedPublish |= _wbPage == "publish";
        if (_wbUiTextPhase != 0)
        {
            if (now < _wbUiNextPhaseAt) return true;
            switch (_wbUiTextPhase++)
            {
                case 1: WbUiKey("Ctrl", true); break;
                case 2: WbUiKey("A", true); break;
                case 3: WbUiKey("A", false); break;
                case 4: WbUiKey("Ctrl", false); break;
                case 5:
                    if (_wbUiReplacementText.Length == 0) WbUiKey("Backspace", true);
                    else ImGui.GetIO().AddInputCharactersUTF8(_wbUiReplacementText);
                    break;
                case 6: if (_wbUiReplacementText.Length == 0) WbUiKey("Backspace", false); break;
                default: _wbUiTextPhase = 0; _wbUiReplacementText = ""; return false;
            }
            _wbUiNextPhaseAt = now + 0.15;
            return true;
        }
        if (_wbUiClickPhase == 0) return false;
        if (now < _wbUiNextPhaseAt) return true;
        switch (_wbUiClickPhase++)
        {
            case 1: _liveGuiDown = true; break;
            case 2: _liveGuiDown = false; break;
            default: _wbUiClickPhase = 0; return false;
        }
        _wbUiNextPhaseAt = now + 0.15;
        return true;
    }

    private void WbUiKey(string name, bool down)
    {
        string alias = name.Replace("-", "").Replace("_", "").ToLowerInvariant();
        (string silk, string gui, ImGuiKey modifier) = alias switch
        {
            "ctrl" or "control" or "leftctrl" or "leftcontrol" or "controlleft" => ("ControlLeft", "LeftCtrl", ImGuiKey.ModCtrl),
            "rightctrl" or "rightcontrol" or "controlright" => ("ControlRight", "RightCtrl", ImGuiKey.ModCtrl),
            "shift" or "leftshift" or "shiftleft" => ("ShiftLeft", "LeftShift", ImGuiKey.ModShift),
            "rightshift" or "shiftright" => ("ShiftRight", "RightShift", ImGuiKey.ModShift),
            "alt" or "leftalt" or "altleft" => ("AltLeft", "LeftAlt", ImGuiKey.ModAlt),
            "rightalt" or "altright" => ("AltRight", "RightAlt", ImGuiKey.ModAlt),
            "super" or "win" or "leftsuper" or "superleft" => ("SuperLeft", "LeftSuper", ImGuiKey.ModSuper),
            "rightsuper" or "superright" => ("SuperRight", "RightSuper", ImGuiKey.ModSuper),
            "esc" => ("Escape", "Escape", ImGuiKey.None),
            "return" => ("Enter", "Enter", ImGuiKey.None),
            "left" or "leftarrow" => ("Left", "LeftArrow", ImGuiKey.None),
            "right" or "rightarrow" => ("Right", "RightArrow", ImGuiKey.None),
            "up" or "uparrow" => ("Up", "UpArrow", ImGuiKey.None),
            "down" or "downarrow" => ("Down", "DownArrow", ImGuiKey.None),
            _ => (name, name, ImGuiKey.None),
        };
        if (!Enum.TryParse<Key>(silk, true, out var key) || !Enum.TryParse<ImGuiKey>(gui, true, out var guiKey))
            throw new ArgumentException($"Unknown input key '{name}'.");
        if (down) { _liveInputHeld.Add(key); _wbUiGuiKeys.Add(guiKey); }
        else { _liveInputHeld.Remove(key); _wbUiGuiKeys.Remove(guiKey); }
        var io = ImGui.GetIO(); io.AddKeyEvent(guiKey, down);
        if (modifier != ImGuiKey.None)
        {
            bool held = modifier switch
            {
                ImGuiKey.ModCtrl => _wbUiGuiKeys.Contains(ImGuiKey.LeftCtrl) || _wbUiGuiKeys.Contains(ImGuiKey.RightCtrl),
                ImGuiKey.ModShift => _wbUiGuiKeys.Contains(ImGuiKey.LeftShift) || _wbUiGuiKeys.Contains(ImGuiKey.RightShift),
                ImGuiKey.ModAlt => _wbUiGuiKeys.Contains(ImGuiKey.LeftAlt) || _wbUiGuiKeys.Contains(ImGuiKey.RightAlt),
                _ => _wbUiGuiKeys.Contains(ImGuiKey.LeftSuper) || _wbUiGuiKeys.Contains(ImGuiKey.RightSuper),
            };
            io.AddKeyEvent(modifier, held);
        }
    }

    private void WbUiPointNpc(string name, double now)
    {
        var candidates = _wbNpcPreviewNames.Where(p => p.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (guid, _) in candidates)
        {
            if (!_entities.TryGet(guid, out var unit) || _creatures?.TryGetSpellPose(guid, out _) != true) continue;
            for (float height = 1.2f; height <= 4.2f; height += 0.3f)
            {
                if (!_window.Camera.TryProjectToScreen(unit.Position + new Vector3(0, 0, height * unit.Scale), ImGui.GetIO().DisplaySize, out var pixel, out _) ||
                    pixel.X < 0 || pixel.Y < 0 || pixel.X >= ImGui.GetIO().DisplaySize.X || pixel.Y >= ImGui.GetIO().DisplaySize.Y) continue;
                // The ordinary posed-mesh picker must hit this actual rendered unit; a projected centre alone is not proof.
                if (PickUnit(pixel) != guid) continue;
                WbUiPointer(pixel); _wbScriptWaitUntil = now + 0.2;
                Console.WriteLine(FormattableString.Invariant($"[wb-ui] NPC '{name}' guid=0x{guid:X} entry={unit.Entry} actual rendered pick at ({pixel.X:F1},{pixel.Y:F1})"));
                return;
            }
        }
        throw new InvalidOperationException($"No visible, pickable NPC named '{name}'. Move the camera until its model is in view.");
    }

    private void WbUiPointer(Vector2 position)
    {
        _liveGuiPointer = position;
        _window.PointerInputSource = () => new PointerInputState(
            _liveGuiPointer ?? new Vector2(-1000, -1000), _liveGuiDown, _liveGuiRightDown);
    }

    private void WbUiClick(string label, double now)
    {
        if (!_wbUiItems.TryGetValue(label, out var item) || ImGui.GetFrameCount() - item.Frame > 2)
            throw new InvalidOperationException($"No visible current button '{label}'. Run ui-inspect to list visible buttons.");
        _liveGuiDown = false;
        WbUiPointer((item.Min + item.Max) * 0.5f);
        _wbUiClickPhase = 1;
        _wbUiNextPhaseAt = now + 0.15;
        Console.WriteLine($"[wb-ui] click '{label}' at {_liveGuiPointer} through real pointer frames");
    }

    private float WbUiSavedDelta(Vector2 point)
    {
        var tile = WorldBuilderLaw.TileOf(point.X, point.Y);
        return _wbState?.Sculpt.Where(t => t.Col == tile.col && t.Row == tile.row)
            .Sum(t => t.Deltas.Values.Sum()) ?? 0f;
    }

    private void WbUiAssert(bool ok, string detail)
    {
        if (!ok) _wbUiFailures++;
        Console.WriteLine($"[wb-ui] {(ok ? "PASS" : "FAIL")} {detail}");
        if (!ok) throw new InvalidOperationException("World Builder UI assertion failed: " + detail);
    }

    /// <summary>ui-page/click; ui-point-ground x y; ui-pointer x y; ui-down/up;
    /// ui-key-down/up Escape; ui-mark name x y; ui-assert-height name changed|same [epsilon];
    /// ui-assert-saved name changed|same; ui-assert-tool/ghost/menu/ready; ui-inspect; ui-reset.</summary>
    private void RunWorldBuilderUiProbe(string[] a, double now)
    {
        float F(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
        bool on = a.Length > 1 && a[1].Equals("on", StringComparison.OrdinalIgnoreCase);
        switch (a[0].ToLowerInvariant())
        {
            case "ui-page":
                WbUiClick(a[1].ToLowerInvariant() switch
                {
                    "terrain" => "Terrain", "buildings" => "Buildings", "npcs" => "NPCs",
                    "quests" => "Quests", "publish" => "Publish", "advanced" => "More", "packs" => "Packs...",
                    _ => throw new ArgumentException("Unknown World Builder page."),
                }, now);
                break;
            case "ui-click": WbUiClick(string.Join(' ', a[1..]), now); break;
            case "ui-text":
                ImGui.GetIO().AddInputCharactersUTF8(string.Join(' ', a[1..]));
                _wbScriptWaitUntil = now + 0.2;
                break;
            case "ui-replace-text":
                if (!ImGui.GetIO().WantTextInput) throw new InvalidOperationException("Click an editable text field before ui-replace-text.");
                _wbUiReplacementText = string.Join(' ', a[1..]); _wbUiTextPhase = 1; _wbUiNextPhaseAt = now + 0.15;
                Console.WriteLine("[wb-ui] replace focused text through Ctrl+A and UTF-8 input frames");
                break;
            case "ui-point-npc": WbUiPointNpc(string.Join(' ', a[1..]), now); break;
            case "ui-wheel":
                ImGui.GetIO().AddMouseWheelEvent(0f, F(1));
                _wbScriptWaitUntil = now + 0.2;
                break;
            case "ui-pointer": WbUiPointer(new Vector2(F(1), F(2))); _wbScriptWaitUntil = now + 0.2; break;
            case "ui-point-ground":
            {
                float x = F(1), y = F(2);
                float z = _terrain?.SampleHeight(x, y) ?? throw new InvalidOperationException("The requested ground is not resident.");
                if (!_window.Camera.TryProjectToScreen(new Vector3(x, y, z), ImGui.GetIO().DisplaySize, out var pixel, out _) ||
                    pixel.X < 0 || pixel.Y < 0 || pixel.X >= ImGui.GetIO().DisplaySize.X || pixel.Y >= ImGui.GetIO().DisplaySize.Y)
                    throw new InvalidOperationException("The requested ground is outside the current view. Adjust cam first.");
                WbUiPointer(pixel);
                _wbScriptWaitUntil = now + 0.2;
                Console.WriteLine(FormattableString.Invariant($"[wb-ui] ground ({x:F3},{y:F3},{z:F3}) projects to ({pixel.X:F1},{pixel.Y:F1})"));
                break;
            }
            case "ui-down": _liveGuiDown = true; _wbScriptWaitUntil = now + 0.15; break;
            case "ui-up": _liveGuiDown = false; _wbScriptWaitUntil = now + 0.15; break;
            case "ui-key-down":
            case "ui-key-up":
            {
                bool down = a[0].Equals("ui-key-down", StringComparison.OrdinalIgnoreCase);
                WbUiKey(a[1], down);
                _wbScriptWaitUntil = now + 0.15;
                break;
            }
            case "ui-mark":
            {
                var point = new Vector2(F(2), F(3));
                float height = _terrain?.SampleHeight(point.X, point.Y) ?? throw new InvalidOperationException("Mark terrain is not resident.");
                _wbUiMarks[a[1]] = (point, height, WbUiSavedDelta(point), _config.Start.Map);
                Console.WriteLine(FormattableString.Invariant($"[wb-ui] mark {a[1]} height={height:F5};savedDelta={WbUiSavedDelta(point):F5}"));
                break;
            }
            case "ui-assert-height":
            {
                var mark = _wbUiMarks[a[1]];
                float height = _terrain?.SampleHeight(mark.Point.X, mark.Point.Y) ?? float.NaN;
                float epsilon = a.Length > 3 ? F(3) : 0.02f;
                float delta = height - mark.Height;
                bool same = a[2] == "same";
                WbUiAssert(mark.Map == _config.Start.Map && float.IsFinite(delta) && (same ? MathF.Abs(delta) <= epsilon : MathF.Abs(delta) > epsilon),
                    FormattableString.Invariant($"height {a[1]} {a[2]};before={mark.Height:F5};after={height:F5};delta={delta:F5}"));
                break;
            }
            case "ui-assert-saved":
            {
                var mark = _wbUiMarks[a[1]];
                float delta = WbUiSavedDelta(mark.Point) - mark.SavedDelta;
                bool same = a[2] == "same";
                WbUiAssert(mark.Map == _wbStateMap && _wbOps.Count == 0 && _wbStateTask is null && !_wbSculptAwaitingState &&
                    (same ? MathF.Abs(delta) < 0.001f : MathF.Abs(delta) > 0.001f),
                    FormattableString.Invariant($"saved {a[1]} {a[2]};serverDeltaChange={delta:F5};message={_wbMessage}"));
                break;
            }
            case "ui-assert-tool": WbUiAssert(_wbTool.ToString().Equals(a[1], StringComparison.OrdinalIgnoreCase), $"tool={_wbTool};expected={a[1]}"); break;
            case "ui-assert-stroking": WbUiAssert(_wbStroking == on, $"stroking={_wbStroking};expected={on}"); break;
            case "ui-assert-cursor":
                WbUiAssert(_wbCursor is { } cursor && Vector2.Distance(new Vector2(cursor.X, cursor.Y), new Vector2(F(1), F(2))) < F(3),
                    $"cursor={_wbCursor};expected near {a[1]},{a[2]} within {a[3]} yd");
                break;
            case "ui-assert-ghost": WbUiAssert((_wbCursorGhostSig is not null) == on, $"cursor ghost={_wbCursorGhostSig is not null};expected={on}"); break;
            case "ui-assert-menu": WbUiAssert(_settingsOpen == (a[1] == "open"), $"menu={_settingsOpen};expected={a[1]}"); break;
            case "ui-assert-ready": WbUiAssert(WbSculptProblem().Length == 0, "sculpt readiness: " + WbSculptProblem()); break;
            case "ui-assert-no-publish": WbUiAssert(!_wbUiVisitedPublish, $"Publish never visited;mountedBuild={_wbMountedBuild}"); break;
            case "ui-assert-placement-count":
            {
                int expected = int.Parse(a[1], CultureInfo.InvariantCulture);
                int count = _wbState?.Placements.Count(p => p.PackId == _wbPackId && !p.Deleted) ?? -1;
                WbUiAssert(count == expected && _wbOps.Count == 0 && _wbStateTask is null,
                    $"active placements in pack {_wbPackId}={count};expected={expected}");
                break;
            }
            case "ui-mark-placement":
            {
                var selected = _wbState?.Placements.FirstOrDefault(p => p.Id == _wbSelected && p.PackId == _wbPackId && !p.Deleted)
                    ?? throw new InvalidOperationException("Select a saved object first.");
                _wbUiPlacementMarks[a[1]] = WbCopyPlacement(selected);
                Console.WriteLine($"[wb-ui] mark placement {a[1]} id={selected.Id};pose={selected.PosX},{selected.PosY},{selected.PosZ}");
                break;
            }
            case "ui-assert-placement":
            {
                var before = _wbUiPlacementMarks[a[1]];
                var current = _wbState?.Placements.FirstOrDefault(p => p.Id == before.Id && !p.Deleted);
                bool matches = a[2] switch
                {
                    "same" => current is not null && WbSignature(before) == WbSignature(current),
                    "changed" => current is not null && WbSignature(before) != WbSignature(current),
                    "deleted" => current is null,
                    _ => throw new ArgumentException("Expected same, changed or deleted."),
                };
                WbUiAssert(matches && _wbOps.Count == 0 && _wbStateTask is null,
                    $"placement {before.Id} {a[2]};current={(current is null ? "deleted" : WbSignature(current))}");
                break;
            }
            case "ui-assert-quest-saved":
            {
                string expected = string.Join(' ', a[1..]);
                var row = WbDocBodies("dbrow:quest_template").FirstOrDefault(q => WbNum(q["entry"]) == _wbQuestEditing);
                bool giver = WbDocBodies("dbrow:creature_questrelation").Any(r => WbNum(r["quest"]) == _wbQuestEditing && WbNum(r["id"]) == _wbQuest.Giver);
                bool objective = WbDocBodies("dbrow:areatrigger_involvedrelation").Any(r => WbNum(r["quest"]) == _wbQuestEditing) ||
                    Enumerable.Range(1, 4).Any(i => WbNum(row?[$"ReqCreatureOrGOId{i}"]) != 0 || WbNum(row?[$"ReqItemId{i}"]) != 0);
                WbUiAssert(_wbQuestEditing >= WbTemplateBase && (_wbUiQuestExpectedEntry == 0 || _wbUiQuestExpectedEntry == _wbQuestEditing) &&
                    row?["Title"]?.ToString() == expected && giver && objective && _wbOps.Count == 0 && _wbDocsTask is null,
                    $"quest {_wbQuestEditing};title={row?["Title"]};expected={expected};giver={giver};objective={objective};message={_wbQuestSaveStatus}");
                _wbUiQuestExpectedEntry = _wbQuestEditing;
                break;
            }
            case "ui-assert-npc-preview":
            {
                int drawn = _wbSpawnPreview.Keys.Count(g => _entities.TryGet(g, out _) && _creatures?.TryGetSpellPose(g, out _) == true);
                int minimum = int.Parse(a[1], CultureInfo.InvariantCulture);
                WbUiAssert(drawn >= minimum, $"NPC previews drawn={drawn};loaded={_wbSpawnPreview.Count};minimum={minimum}");
                break;
            }
            case "ui-assert-npc-selected":
            {
                string expected = string.Join(' ', a[1..]);
                bool matches = uint.TryParse(expected, out uint entry) ? _wbNpcEditEntry == entry : _wbNpcTemplate?["name"]?.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase) == true;
                WbUiAssert(_wbNpcEditEntry != 0 && _wbNpcLoadTask is null && !_wbNpcLoadFailed && matches,
                    $"NPC selected entry={_wbNpcEditEntry};spawn={_wbNpcEditSpawn};name={_wbNpcTemplate?["name"]};expected={expected}");
                _wbUiNpcExpectedEntry = _wbNpcEditEntry >= WbTemplateBase ? _wbNpcEditEntry : 0;
                break;
            }
            case "ui-assert-npc-name":
            {
                string expected = string.Join(' ', a[1..]);
                WbUiAssert(WbText(_wbNpcName) == expected, $"NPC form name={WbText(_wbNpcName)};expected={expected}");
                break;
            }
            case "ui-assert-npc-saved":
            {
                string expected = string.Join(' ', a[1..]);
                var authoritative = WbNpcPackDocs("dbrow:creature_template", _wbPackId).FirstOrDefault(d => WbNpcUInt(d["body"]?["entry"]) == _wbNpcEditEntry)?["body"];
                bool sameId = _wbUiNpcExpectedEntry == 0 || _wbUiNpcExpectedEntry == _wbNpcEditEntry;
                WbUiAssert(_wbNpcEditEntry >= WbTemplateBase && _wbNpcStockSourceEntry == 0 && sameId &&
                    !_wbNpcSaving && _wbOps.Count == 0 && _wbDocsTask is null && authoritative?["name"]?.ToString() == expected,
                    $"NPC saved entry={_wbNpcEditEntry};priorEntry={_wbUiNpcExpectedEntry};authoritativeName={authoritative?["name"]};expected={expected};message={_wbMessage}");
                _wbUiNpcExpectedEntry = _wbNpcEditEntry;
                break;
            }
            case "ui-inspect":
                string pick = _liveGuiPointer is { } pointer && TryPickGround(pointer, out var hit)
                    ? hit.ToString() : "none";
                Console.WriteLine($"[wb-ui] page={_wbPage};tool={_wbTool};stroking={_wbStroking};cursor={_wbCursor};" +
                    $"ghost={_wbCursorGhostSig is not null};menu={_settingsOpen};uiCapture={ImGui.GetIO().WantCaptureMouse};" +
                    $"mounted={_wbMountedBuild};pack={_wbPackId};pointer={_liveGuiPointer};pickedGround={pick};" +
                    $"message={_wbMessage};hint={_wbToolHint};failures={_wbUiFailures}");
                foreach (var (label, item) in _wbUiItems.Where(p => ImGui.GetFrameCount() - p.Value.Frame <= 2))
                    Console.WriteLine($"[wb-ui] button '{label}' rect={item.Min}..{item.Max}");
                break;
            case "ui-reset":
                _liveGuiDown = _liveGuiRightDown = false;
                _liveGuiPointer = null;
                _liveInputHeld.Clear();
                foreach (var guiKey in _wbUiGuiKeys) ImGui.GetIO().AddKeyEvent(guiKey, false);
                _wbUiGuiKeys.Clear();
                foreach (var modifier in new[] { ImGuiKey.ModCtrl, ImGuiKey.ModShift, ImGuiKey.ModAlt, ImGuiKey.ModSuper }) ImGui.GetIO().AddKeyEvent(modifier, false);
                ImGui.GetIO().AddKeyEvent(ImGuiKey.Escape, false);
                _window.PointerInputSource = null;
                _window.ClearWorldClicks();
                _wbUiClickPhase = 0;
                _wbUiTextPhase = 0; _wbUiReplacementText = "";
                Console.WriteLine($"[wb-ui] input released; failures={_wbUiFailures}");
                break;
            default: throw new ArgumentException($"Unknown UI probe command '{a[0]}'.");
        }
    }
}
