using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// DOPE SHEET (SPELL_CREATOR_PLAN.md P3.2) - keyframes, while the clock is paused.
//
// Under the paused timeline, one LANE per animated track of the selection (an emitter's rate,
// life, speed...; a ribbon's colour, alpha, heights; a phase's bones, colours, texture motion - a
// combo picks the group). Keys are diamonds over the model's first sequence, with the playhead
// where the paused instance is:
//   click        select a key (its value edits below the lanes)
//   drag         move it in time (committed on release)
//   right-click  delete it
//   double-click add a key there, holding the value the track already has at that time
// Every change is a whole-track rewrite (M2TrackPatcher) kept in CreatorModelDoc.TrackEdits, so it
// previews at once, exports with the model, rides the design and is on the undo stack.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private readonly Dictionary<string, string> _dopeGroupOfModel = new(StringComparer.OrdinalIgnoreCase);
    private string? _dopeSelTrack;
    private int _dopeSelKey = -1;
    private string? _dopeDragTrack;
    private int _dopeDragKey = -1;
    private TrackKeys? _dopeDragKeys;

    private void DrawCreatorDopeSheet(float width)
    {
        if (_creatorSpell is not { } doc) return;
        SpellIdeSelection sel = _spellIdeSelection;
        if (sel.Kind is not (SpellIdeKind.Emitter or SpellIdeKind.Ribbon or SpellIdeKind.Phase) ||
            !doc.Models.TryGetValue(sel.Path, out CreatorModelDoc? model) || model.Working.Length == 0)
        {
            ImGui.TextDisabled("KEYS: select an emitter, a ribbon or a phase to see its keyframes.");
            return;
        }
        List<TrackInfo> tracks = M2TrackPatcher.Enumerate(model.Working);
        if (tracks.Count == 0) { ImGui.TextDisabled("KEYS: this model has no animated tracks."); return; }

        string[] groups = tracks.Select(t => t.Group).Distinct().ToArray();
        string wanted = sel.Kind switch
        {
            SpellIdeKind.Emitter => $"e{sel.Emitter}",
            SpellIdeKind.Ribbon => $"r{sel.Emitter}",
            _ => _creatorBonePick.TryGetValue(sel.Path, out int bone) ? $"b{bone}" : groups[0],
        };
        if (!_dopeGroupOfModel.TryGetValue(sel.Path, out string? group) || !groups.Contains(group) ||
            sel.Kind != SpellIdeKind.Phase) group = groups.Contains(wanted) ? wanted : groups[0];
        float cs = CreatorUiScale;
        ImGui.TextColored(SpellIdeGold, "KEYS");
        ImGui.SameLine();
        int groupIndex = Array.IndexOf(groups, group);
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.Combo("##dope-group", ref groupIndex, groups, groups.Length)) group = groups[groupIndex];
        _dopeGroupOfModel[sel.Path] = group;
        ImGui.SameLine();
        ImGui.TextDisabled($"{Path.GetFileName(model.Path)} - click a key, drag it, right-click deletes, double-click adds");

        List<(uint Start, uint End)> sequences = M2TrackPatcher.Sequences(model.Working);
        (uint start, uint end) = sequences.Count > 0 ? sequences[0] : (0u, 1000u);
        if (end <= start) end = start + 1000;
        float label = 150f * cs;
        float laneW = MathF.Max(width - label - 20f * cs, 100f * cs);
        float laneH = 18f * cs;

        // Playhead: the paused instance of this model.
        double? playMs = null;
        if (_spellEffects is not null)
            foreach (var live in _spellEffects.LiveInstances())
                if (string.Equals(live.Path, SpellVisualCatalog.ModelPath(model.Path), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(live.Path, model.Path, StringComparison.OrdinalIgnoreCase))
                {
                    double age = _creatorClockNow - (live.Missile ? live.LaunchedAt : live.Started);
                    playMs = start + (age * 1000.0 % Math.Max(end - start, 1u));
                    break;
                }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        foreach (TrackInfo track in tracks.Where(t => t.Group == group))
        {
            TrackKeys? keys = _dopeDragTrack == track.Id && _dopeDragKeys is not null ? _dopeDragKeys
                : model.TrackEdits.TryGetValue(track.Id, out TrackKeys? edited) ? edited
                : M2TrackPatcher.Read(model.Working, track);
            if (keys is null) continue;
            bool editedTrack = model.TrackEdits.ContainsKey(track.Id);
            ImGui.PushID(track.Id);
            ImGui.AlignTextToFramePadding();
            if (editedTrack) ImGui.TextColored(SpellIdeGold, track.Label); else ImGui.TextUnformatted(track.Label);
            if (keys.GlobalSequence >= 0) { ImGui.SameLine(); ImGui.TextDisabled("(loop)"); }
            ImGui.SameLine(label);
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##lane", new Vector2(laneW, laneH));
            bool hovered = ImGui.IsItemHovered();
            draw.AddRectFilled(origin, origin + new Vector2(laneW, laneH), 0x60202020u, 3f);
            if (playMs is { } p)
            {
                float px = origin.X + laneW * (float)((p - start) / (end - start));
                draw.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + laneH), 0xFF40C0FFu, 1.5f);
            }
            float X(uint ms) => origin.X + laneW * Math.Clamp((ms - (float)start) / (end - start), 0f, 1f);
            uint Ms(float x) => (uint)Math.Clamp(start + (x - origin.X) / laneW * (end - start), start, end);

            int nearest = -1;
            float nearestDistance = 7f * cs;
            Vector2 mouse = ImGui.GetIO().MousePos;
            for (int k = 0; k < keys.Times.Count; k++)
            {
                float x = X(keys.Times[k]);
                bool selected = _dopeSelTrack == track.Id && _dopeSelKey == k;
                Vector2 c = new(x, origin.Y + laneH * 0.5f);
                float r = (selected ? 6f : 4.5f) * cs;
                uint colour = keys.Times[k] < start || keys.Times[k] > end ? 0xFF606060u : selected ? 0xFF33DDEEu : 0xFFE0E0E0u;
                draw.AddQuadFilled(c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0), colour);
                float dist = MathF.Abs(mouse.X - x);
                if (hovered && dist < nearestDistance) { nearestDistance = dist; nearest = k; }
            }

            if (hovered && nearest >= 0)
                ImGui.SetTooltip($"{keys.Times[nearest] - start} ms: {M2TrackPatcher.Describe(keys.Values[nearest], track.Kind)}");
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && nearest < 0)
            {
                TrackKeys add = keys.Clone();
                uint at = Ms(mouse.X);
                add.Times.Add(at);
                add.Values.Add(M2TrackPatcher.Sample(keys, at, track.Kind));
                CommitDopeTrack(model, track, add);
                _dopeSelTrack = track.Id;
                _dopeSelKey = CommitDopeKeyIndex(model, track, at);
            }
            else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && nearest >= 0)
            {
                _dopeSelTrack = track.Id; _dopeSelKey = nearest;
                _dopeDragTrack = track.Id; _dopeDragKey = nearest; _dopeDragKeys = keys.Clone();
            }
            else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && nearest >= 0)
            {
                TrackKeys cut = keys.Clone();
                cut.Times.RemoveAt(nearest); cut.Values.RemoveAt(nearest);
                CommitDopeTrack(model, track, cut);
                _dopeSelKey = -1;
            }
            if (_dopeDragTrack == track.Id && _dopeDragKeys is { } drag)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    drag.Times[_dopeDragKey] = Ms(mouse.X);
                else
                {
                    uint movedTo = drag.Times[_dopeDragKey];
                    CommitDopeTrack(model, track, drag);
                    _dopeSelKey = CommitDopeKeyIndex(model, track, movedTo);
                    _dopeDragTrack = null; _dopeDragKeys = null; _dopeDragKey = -1;
                }
            }

            if (editedTrack) ImGui.SameLine();            // only an edited lane has a reset; others end the line
            if (editedTrack && ImGui.SmallButton("reset"))
            {
                model.TrackEdits.Remove(track.Id);
                RebuildCreatorModel(model);
                if (_dopeSelTrack == track.Id) _dopeSelKey = -1;
            }
            ImGui.PopID();
        }

        // The selected key's value and the track's interpolation.
        if (_dopeSelTrack is { } selTrackId && tracks.FirstOrDefault(t => t.Id == selTrackId && t.Group == group) is { } selTrack &&
            (model.TrackEdits.TryGetValue(selTrackId, out TrackKeys? selEdited) ? selEdited : M2TrackPatcher.Read(model.Working, selTrack)) is { } selKeys &&
            _dopeSelKey >= 0 && _dopeSelKey < selKeys.Times.Count)
        {
            float[] value = (float[])selKeys.Values[_dopeSelKey].Clone();
            ImGui.TextDisabled($"{selTrack.Label} @ {selKeys.Times[_dopeSelKey] - start} ms");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(240f * cs);
            bool changed = selTrack.Kind switch
            {
                TrackValueKind.Vec3 => ImGui.DragFloat3("##dope-v3", ref System.Runtime.CompilerServices.Unsafe.As<float, Vector3>(ref value[0]), 0.01f),
                TrackValueKind.Quat => ImGui.DragFloat4("##dope-q", ref System.Runtime.CompilerServices.Unsafe.As<float, Vector4>(ref value[0]), 0.005f, -1f, 1f),
                TrackValueKind.Fixed16 => ImGui.SliderFloat("##dope-f16", ref value[0], 0f, 1f, "%.3f"),
                _ => ImGui.DragFloat("##dope-f", ref value[0], track_step(selTrack.Kind)),
            };
            if (changed)
            {
                if (selTrack.Kind == TrackValueKind.Quat)
                {
                    var q = Quaternion.Normalize(new Quaternion(value[0], value[1], value[2], value[3]));
                    value = new[] { q.X, q.Y, q.Z, q.W };
                }
                TrackKeys next = selKeys.Clone();
                next.Values[_dopeSelKey] = value;
                CommitDopeTrack(model, selTrack, next);
            }
            ImGui.SameLine();
            bool stepped = selKeys.Interpolation == 0;
            if (ImGui.Checkbox("hold (step)##dope", ref stepped))
            {
                TrackKeys next = selKeys.Clone();
                next.Interpolation = (ushort)(stepped ? 0 : 1);
                CommitDopeTrack(model, selTrack, next);
            }
        }

        static float track_step(TrackValueKind kind) => kind is TrackValueKind.U8 or TrackValueKind.U16 ? 1f : 0.05f;
    }

    /// <summary>Store the track sorted by time (a later key at the same ms wins - what the writer
    /// does), so the indices the lanes draw are the indices the edit holds.</summary>
    private void CommitDopeTrack(CreatorModelDoc model, TrackInfo track, TrackKeys keys)
    {
        var sorted = keys.Times.Select((t, i) => (Time: t, Value: keys.Values[i]))
            .GroupBy(k => k.Time).Select(g => g.Last()).OrderBy(k => k.Time).ToList();
        TrackKeys clean = keys.Clone();
        clean.Times = sorted.Select(k => k.Time).ToList();
        clean.Values = sorted.Select(k => k.Value).ToList();
        model.TrackEdits[track.Id] = clean;
        RebuildCreatorModel(model);
    }

    /// <summary>Where a key at this time ended up after the commit sorted the track.</summary>
    private static int CommitDopeKeyIndex(CreatorModelDoc model, TrackInfo track, uint ms) =>
        model.TrackEdits.TryGetValue(track.Id, out TrackKeys? keys) ? keys.Times.IndexOf(ms) : -1;
}
