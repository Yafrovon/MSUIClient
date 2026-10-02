using System.Buffers;
using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient.Engine.UI;

/// <summary>
/// CPU mirror of the unit vertex shader for mouse picking. The input pose is the renderer's
/// last completed draw, so selection tests the same animation, world transform and geoset set
/// that produced the visible body. Pass two expands each vertex by one model-space normal,
/// matching the reference mouse-pick halo without changing spell/LoS collision.
/// </summary>
public static class TargetMeshPickLaw
{
    public const float HaloModelUnits = 1f;

    /// <summary>Reference pass-two priority: sticky previous pick, then alive, then dead.</summary>
    public static uint HaloPriority(bool previousPick, bool dead) =>
        previousPick ? uint.MaxValue : dead ? 2u : 3u;

    public static bool HaloWins(float distance, uint priority,
        float bestDistance, uint bestPriority) =>
        priority > bestPriority || priority == bestPriority && distance < bestDistance;

    /// <summary>
    /// Return the nearest two-sided triangle hit along a normalized world ray. Invalid skin
    /// influences follow the production GPU path: indices past the 160-row upload clamp to bone
    /// zero; indices past the active palette are ignored; surviving weights renormalize.
    /// </summary>
    public static bool TryPick(in SpellUnitPose pose, Vector3 origin, Vector3 direction,
        bool inflated, out float distance)
    {
        distance = float.PositiveInfinity;
        M2Model? model = pose.Model;
        if (!pose.Found || model is null || model.Vertices.Count == 0 || model.Indices.Count < 3 ||
            direction.LengthSquared() <= 1e-12f)
            return false;

        direction = Vector3.Normalize(direction);
        if (!PassesBroadPhase(pose, origin, direction, inflated)) return false;
        float nearest = float.PositiveInfinity;
        Vector3[] world = ArrayPool<Vector3>.Shared.Rent(model.Vertices.Count);
        try
        {
            for (int i = 0; i < model.Vertices.Count; i++)
            {
                M2Vertex vertex = model.Vertices[i];
                SkinVertex(vertex, pose.Skin, out Vector3 point, out Vector3 normal);
                Vector3 placed = Vector3.Transform(point, pose.UnitTransform);
                if (inflated)
                    placed += Vector3.TransformNormal(normal, pose.UnitTransform) * HaloModelUnits;
                world[i] = placed;
            }

            if (pose.VisibleGeosets is { } visible && model.Submeshes.Count > 0)
            {
                foreach (M2Submesh submesh in model.Submeshes)
                    if (visible.Contains(submesh.Id))
                        TestRange(submesh.IndexStart, submesh.IndexCount);
            }
            else
            {
                TestRange(0, model.Indices.Count);
            }

            distance = nearest;
            return float.IsFinite(nearest);

            void TestRange(int start, int count)
            {
                int end = Math.Min(model.Indices.Count, start + count);
                for (int i = Math.Max(0, start); i + 2 < end; i += 3)
                {
                    int a = model.Indices[i], b = model.Indices[i + 1], c = model.Indices[i + 2];
                    if ((uint)a >= (uint)model.Vertices.Count ||
                        (uint)b >= (uint)model.Vertices.Count ||
                        (uint)c >= (uint)model.Vertices.Count)
                        continue;
                    if (RayTriangle(origin, direction, world[a], world[b], world[c], out float hit) &&
                        hit < nearest)
                        nearest = hit;
                }
            }
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(world);
        }
    }

    private static bool PassesBroadPhase(in SpellUnitPose pose, Vector3 origin,
        Vector3 direction, bool inflated)
    {
        if (!(pose.PickBoundsRadius > 0f) || !float.IsFinite(pose.PickBoundsRadius))
            return true;
        Vector3 center = Vector3.Transform(pose.PickBoundsCenter, pose.UnitTransform);
        float scale = MathF.Max(
            Vector3.TransformNormal(Vector3.UnitX, pose.UnitTransform).Length(),
            MathF.Max(
                Vector3.TransformNormal(Vector3.UnitY, pose.UnitTransform).Length(),
                Vector3.TransformNormal(Vector3.UnitZ, pose.UnitTransform).Length()));
        float radius = pose.PickBoundsRadius * scale + (inflated ? HaloModelUnits * scale : 0f);
        Vector3 toCenter = center - origin;
        float along = MathF.Max(0f, Vector3.Dot(toCenter, direction));
        return (toCenter - direction * along).LengthSquared() <= radius * radius;
    }

    /// <summary>Shared CPU mirror of the native shader, also used by opt-in equipment geometry evidence.</summary>
    public static void SkinVertex(in M2Vertex vertex, IReadOnlyList<Matrix4x4>? skin,
        out Vector3 point, out Vector3 normal)
    {
        Vector3 basePoint = new(vertex.PosX, vertex.PosY, vertex.PosZ);
        Vector3 baseNormal = new(vertex.NormX, vertex.NormY, vertex.NormZ);
        point = basePoint;
        normal = baseNormal;
        if (skin is null || skin.Count == 0) return;

        int rawTotal = vertex.BoneWeight0 + vertex.BoneWeight1 +
            vertex.BoneWeight2 + vertex.BoneWeight3;
        if (rawTotal <= 0)
        {
            point = Vector3.Transform(basePoint, skin[0]);
            normal = Vector3.TransformNormal(baseNormal, skin[0]);
            return;
        }

        Vector3 skinnedPoint = Vector3.Zero;
        Vector3 skinnedNormal = Vector3.Zero;
        float accepted = 0f;
        Apply(vertex.BoneWeight0, vertex.BoneIndex0);
        Apply(vertex.BoneWeight1, vertex.BoneIndex1);
        Apply(vertex.BoneWeight2, vertex.BoneIndex2);
        Apply(vertex.BoneWeight3, vertex.BoneIndex3);
        if (accepted > .0001f)
        {
            point = skinnedPoint / accepted;
            normal = skinnedNormal / accepted;
        }

        void Apply(byte rawWeight, byte rawIndex)
        {
            if (rawWeight == 0) return;
            int index = rawIndex < M2Animator.MaxBones ? rawIndex : 0;
            if ((uint)index >= (uint)skin.Count) return;
            float weight = rawWeight / (float)rawTotal;
            skinnedPoint += Vector3.Transform(basePoint, skin[index]) * weight;
            skinnedNormal += Vector3.TransformNormal(baseNormal, skin[index]) * weight;
            accepted += weight;
        }
    }

    private static bool RayTriangle(Vector3 origin, Vector3 direction,
        Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-8f) return false;
        float inverse = 1f / determinant;
        Vector3 fromA = origin - a;
        float u = Vector3.Dot(fromA, p) * inverse;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(fromA, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f;
    }
}
