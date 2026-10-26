using System.Text.Json;
using System.Text.Json.Nodes;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH JSON — the `sketches` block of a session entry (shared_docs/SPELL_SKETCH.md
// §5, slice S7; SPELL_CREATOR_PLAN.md P0.2).
//
// The pieces ARE the source of a sketched model; the compiled M2 is output. A
// spell reopens editable only if the pieces travel, so this is the format they
// travel in. Shape: { "version": 1, "stage": "cast", "pieces": [ … ] }, every
// field of SketchModel by name (CreatorJson's rules: enums by name, vectors as
// arrays, computed properties not written, absent members keep their defaults).
//
// LAW: compile(parse(serialize(doc))) is byte-identical to compile(doc). The
// emitter lab asserts it on its fixtures, so a field added to the model without
// being serializable fails there and not in front of the owner.
// ═══════════════════════════════════════════════════════════════════════════
public static class SketchJson
{
    public const int FormatVersion = 1;

    public static JsonObject ToJson(SketchDoc doc)
    {
        var node = new JsonObject
        {
            ["version"] = FormatVersion,
            ["stage"] = doc.Stage,
            ["pieces"] = JsonSerializer.SerializeToNode(doc.Pieces, CreatorJson.Options),
        };
        return node;
    }

    /// <summary>Null when the node is not a sketch this build can read (a newer major
    /// version, or no pieces array at all).</summary>
    public static SketchDoc? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        int version = obj["version"]?.GetValue<int>() ?? 0;
        if (version is < 1 or > FormatVersion) return null;
        if (obj["pieces"] is not JsonArray pieces) return null;
        var doc = new SketchDoc { Stage = obj["stage"]?.GetValue<string>() ?? "cast" };
        foreach (JsonNode? pieceNode in pieces)
        {
            if (pieceNode is null) continue;
            SketchPiece? piece = pieceNode.Deserialize<SketchPiece>(CreatorJson.Options);
            if (piece is null) continue;
            // Members a file left out keep their defaults; members it set to null on a
            // non-nullable reference must not leave the compiler holding a null.
            piece.Shape ??= new SketchShape();
            piece.Shape.Strokes ??= new List<List<System.Numerics.Vector2>>();
            piece.Look ??= new SketchLook();
            piece.Place ??= new SketchPlace();
            piece.Motion ??= new SketchMotion();
            piece.Motion.Path ??= new List<System.Numerics.Vector3>();
            piece.Extras ??= new SketchExtras();
            piece.Extras.Trail ??= new SketchTrail();
            piece.Extras.Sparks ??= new SketchSparks();
            piece.Extras.Glow ??= new SketchGlow();
            piece.Name ??= "piece";
            doc.Pieces.Add(piece);
        }
        return doc;
    }

    public static string Serialize(SketchDoc doc) => ToJson(doc).ToJsonString(CreatorJson.Options);

    public static SketchDoc? Deserialize(string json) => FromJson(JsonNode.Parse(json));
}
