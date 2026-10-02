using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>Stroke persistence and the input/initialization seams that made interactive tools appear broken.</summary>
internal static class WorldBuilderInteractionChecks
{
    public static void Run()
    {
        float[] saved = [3f, -4f, 0f, 7f, 9f];
        float[] stroke = [0.75f, -0.25f, 0.0005f, float.NaN, float.PositiveInfinity];
        WorldBuilderLaw.ApplyStroke(saved, stroke);
        Check(saved.SequenceEqual(new[] { 3.75f, -4.25f, 0f, 7f, 9f }),
            "Preview must apply only finite deltas that the sculpt request actually saves.");
        saved[1] += 2f; // A different accepted edit must survive rollback of this rejected request.
        WorldBuilderLaw.ApplyStroke(saved, stroke, undo: true);
        Check(saved.SequenceEqual(new[] { 3f, -2f, 0f, 7f, 9f }),
            "Rejected stroke rollback must remove only that stroke, retaining other edits.");
        Check(!WorldBuilderLaw.SavesDelta(0.001f) && WorldBuilderLaw.SavesDelta(-0.0011f),
            "Preview and request must agree at the persistence threshold for both directions.");
        bool badGridRejected = false;
        try { WorldBuilderLaw.ApplyStroke(new float[2], new float[3]); }
        catch (ArgumentException) { badGridRejected = true; }
        Check(badGridRejected, "Mismatched terrain grids cannot be partially applied.");

        // A planar slope has zero curvature, including the shared edge/corner between ADTs.
        // Clamping neighbours within a tile raised a ridge here instead of preserving the plane.
        static float Plane(int col, int row, int gr, int gc) => 2 * (row * 128 + gr) + 3 * (col * 128 + gc);
        foreach (var (col, row, gr, gc) in new[] { (27, 36, 64, 128), (28, 36, 64, 0), (27, 35, 128, 128), (28, 36, 0, 0) })
        {
            var neighbours = WorldBuilderLaw.NeighbourVertices(col, row, gr, gc);
            float mean = neighbours.Sum(v => Plane(v.col, v.row, v.gridRow, v.gridCol)) / 4;
            Check(WorldBuilderLaw.Dab(WorldBuilderLaw.BrushMode.Smooth, 1, .5f, Plane(col, row, gr, gc), mean, 0) == 0,
                "Smooth must preserve a planar slope across shared tile edges and corners.");
        }
        Check(WorldBuilderLaw.NeighbourVertices(0, 0, 0, 0).All(v => v.col >= 0 && v.row >= 0 && v.gridRow >= 0 && v.gridCol >= 0),
            "Smoothing at the map boundary must not address negative tiles or vertices.");

        string root = ClientConfig.FindRepoRoot();
        string panel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode", "GameLoop.Creator.WorldBuilder.cs"));
        string frame = Between(panel, "private void UpdateWorldBuilder()", "private void WbSetTool(");
        Check(frame.IndexOf("WbReadMountedBuild();", StringComparison.Ordinal) >= 0 &&
              frame.IndexOf("WbReadMountedBuild();", StringComparison.Ordinal) < frame.IndexOf("WbKeepPreviewApplied();", StringComparison.Ordinal),
            "Sculpt preview must initialize the mounted build without opening Publish.");
        Check(frame.IndexOf("ImGuiKey.Z", StringComparison.Ordinal) >= 0 &&
              frame.IndexOf("ImGuiKey.Z", StringComparison.Ordinal) < frame.IndexOf("if (_wbTool == WorldBuilderTool.None)", StringComparison.Ordinal),
            "Ctrl+Z must work on the World Builder page even when no placement or brush is armed.");
        Check(panel.Contains("if (tile is not null) _wbStock[key] = tile;", StringComparison.Ordinal),
            "Temporarily unavailable sculpt bases must not be cached as permanent failures.");
        string client = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldPackClient.cs"));
        Check(client.Contains("surface = true", StringComparison.Ordinal) &&
              panel.Contains("if (!_wbState.SurfaceSculptSupported)", StringComparison.Ordinal) &&
              Between(panel, "private void WbFinishStroke(", "// ── terrain preview").Contains("SurfaceSculptSupported != true", StringComparison.Ordinal),
            "New strokes must select final-surface storage and refuse old servers that silently ignore that flag.");
        Check(panel.Contains("mountedIsLatest ? _mpq.ReadFile(path)", StringComparison.Ordinal),
            "All published terrain, including path-only tiles, must preview from its finished surface.");
        Check(Between(panel, "private void WbDab(", "private float WbHeight(").Contains("WorldBuilderLaw.NeighbourVertices(", StringComparison.Ordinal),
            "The interactive Smooth brush must use neighbouring tiles at shared edges.");
        string sculptPick = Between(panel, "private bool WbTryPickSculptGround(", "private void WbSculptFrame(");
        Check(sculptPick.Contains("TryPickTerrainSurface(", StringComparison.Ordinal) &&
              !sculptPick.Contains("TryPickGround(", StringComparison.Ordinal) &&
              frame.Contains("WbTryPickSculptGround(io.MousePos, out hit)", StringComparison.Ordinal),
            "The sculpt brush must pick terrain, never use a building roof as its flatten target.");
        string escape = Between(panel, "private bool ConsumeWorldBuilderEscape()", "private string WbSculptProblem()");
        Check(escape.Contains("WbCancelStroke();", StringComparison.Ordinal) &&
              escape.Contains("WbSetTool(WorldBuilderTool.None)", StringComparison.Ordinal) &&
              !escape.Contains("WantTextInput", StringComparison.Ordinal),
            "Escape must discard the unfinished stroke and disarm the editor.");
        Check(panel.Contains("else if (_wbStateRequestedPackId != _wbPackId) WbRequestState();", StringComparison.Ordinal) &&
              frame.Contains("_wbStateIncludePackId != _wbPackId", StringComparison.Ordinal),
            "A state response for the previously selected pack must be discarded and refreshed.");
        string select = Between(panel, "private void WbSelectFrame(", "private static string WbSignature(");
        Check(select.Contains("!p.Deleted && p.PackId == _wbPackId && p.MapId == _config.Start.Map", StringComparison.Ordinal),
            "An armed pointer move must never move a deleted object or one owned by another pack/map.");
        string cancel = Between(panel, "private void WbCancelStroke()", "private bool HandleWorldBuilderClick(");
        Check(cancel.Contains("_wbStroke.Clear();", StringComparison.Ordinal) &&
              cancel.Contains("WbApplyTilePreview(key);", StringComparison.Ordinal) &&
              !cancel.Contains("SculptAsync", StringComparison.Ordinal),
            "Cancelling must restore the preview without saving an operation.");
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels", "GameLoop.Settings.cs"));
        Check(settings.IndexOf("ConsumeWorldBuilderEscape()", StringComparison.Ordinal) >= 0 &&
              settings.IndexOf("ConsumeWorldBuilderEscape()", StringComparison.Ordinal) < settings.IndexOf("GameMenuUiLaw.ResolveEscape", StringComparison.Ordinal),
            "An active World Builder action must consume Escape before the game menu.");
        string save = Between(panel, "private void WbResolveSculptSave()", "private void PumpWbTasks()");
        Check(save.Contains("undo: true", StringComparison.Ordinal) && save.Contains("_wbDirtyTiles.Add(key)", StringComparison.Ordinal),
            "A failed save must restore visible terrain even if the state refresh also fails.");
    }

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        int b = a < 0 ? -1 : text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Check(a >= 0 && b > a, "Expected interaction method boundaries must remain discoverable.");
        return text[a..b];
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("WorldBuilder interaction: " + message);
    }
}
