using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Formats;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// "+ COPY FROM ANOTHER SPELL'S MODEL" (SPELL_CREATOR_PLAN.md P3.3 / P3.4).
//
// Any particle emitter or ribbon of any effect model in the game, grafted into the phase model
// being tuned (M2RecordTransplant). A graft is kept as (donor path, index, kind) and re-applied in
// the compose pipeline right after the model's own clones, so the emitter inspector's dials (which
// address emitters by index) work on it like on any other: tint it, move it, change its rate.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private sealed record CreatorTransplant(string Donor, int Index, bool Ribbon);

    private const int CreatorTransplantPickerSlot = -6;
    private string? _transplantTargetModel;
    private string? _transplantDonor;

    /// <summary>The row under a phase's EMITTERS list. True when the model changed.</summary>
    private bool DrawCreatorTransplantRow(CreatorModelDoc model)
    {
        bool dirty = false;
        if (ImGui.SmallButton("+ copy from another spell's model"))
        {
            _transplantTargetModel = model.Path;
            _transplantDonor = null;
            OpenCreatorModelPicker(null, CreatorTransplantPickerSlot);
        }
        CreatorHelp("Graft any particle emitter or trail out of any effect model in the game into this one - " +
                    "Arcane Missiles' sparkle, a fire spell's embers, a melee trail. It rides this model's root " +
                    "and appears in the list below; select it to tint, move or retime it like the others.");
        foreach (CreatorTransplant graft in model.Transplants.ToList())
        {
            ImGui.TextDisabled($"  grafted {(graft.Ribbon ? "trail" : "emitter")} {Path.GetFileName(graft.Donor)} {(graft.Ribbon ? "r" : "e")}{graft.Index}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"remove##graft-{graft.Donor}-{graft.Index}-{graft.Ribbon}"))
            {
                model.Transplants.Remove(graft);
                dirty = true;
            }
        }

        if (_transplantTargetModel == model.Path && _transplantDonor is { } donorPath)
        {
            byte[]? donor = _mpq.ReadFile(donorPath);
            M2Model? parsed = donor is null ? null : M2Reader.Parse(donor);
            ImGui.TextColored(SpellIdeGold, $"From {Path.GetFileName(donorPath)}:");
            ImGui.SameLine();
            if (ImGui.SmallButton("done##graft")) _transplantDonor = null;
            if (parsed is null) ImGui.TextDisabled("  (could not read it)");
            else
            {
                for (int i = 0; i < parsed.ParticleEmitters.Count; i++)
                {
                    M2ParticleEmitter e = parsed.ParticleEmitters[i];
                    string tex = e.Texture < parsed.Textures.Count ? Path.GetFileNameWithoutExtension(parsed.Textures[e.Texture].Filename) : "?";
                    if (ImGui.SmallButton($"+ e{i}: {tex} ({e.EmissionRate:0.#}/s, {e.Lifespan:0.##} s)##graft-e{i}"))
                    {
                        model.Transplants.Add(new CreatorTransplant(donorPath, i, false));
                        dirty = true;
                    }
                }
                for (int i = 0; i < parsed.RibbonEmitters.Count; i++)
                {
                    M2RibbonEmitter r = parsed.RibbonEmitters[i];
                    string tex = r.Texture < parsed.Textures.Count ? Path.GetFileNameWithoutExtension(parsed.Textures[r.Texture].Filename) : "?";
                    if (ImGui.SmallButton($"+ trail r{i}: {tex}##graft-r{i}"))
                    {
                        model.Transplants.Add(new CreatorTransplant(donorPath, i, true));
                        dirty = true;
                    }
                }
                if (parsed.ParticleEmitters.Count == 0 && parsed.RibbonEmitters.Count == 0)
                    ImGui.TextDisabled("  it has no emitters or trails - pick another");
            }
        }
        return dirty;
    }

    /// <summary>Apply the grafts to a model's bytes (the compose pipeline, after the clones).</summary>
    private byte[] ApplyCreatorTransplants(CreatorModelDoc model, byte[] working)
    {
        foreach (CreatorTransplant graft in model.Transplants)
        {
            byte[]? donor = _mpq.ReadFile(graft.Donor);
            M2RecordTransplant.Result? result = donor is null ? null : graft.Ribbon
                ? M2RecordTransplant.AddRibbon(working, donor, graft.Index, 0, System.Numerics.Vector3.Zero, out string ribbonNote)
                : M2RecordTransplant.AddParticle(working, donor, graft.Index, 0, System.Numerics.Vector3.Zero, out string particleNote);
            if (result is not null) working = result.Model;
            else Console.WriteLine($"[creator] {Path.GetFileName(model.Path)}: graft of {Path.GetFileName(graft.Donor)} {graft.Index} failed");
        }
        return working;
    }
}
