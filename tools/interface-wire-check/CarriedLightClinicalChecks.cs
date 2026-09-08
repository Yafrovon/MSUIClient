using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

internal static class CarriedLightClinicalChecks
{
    public static void Run()
    {
        Check(MathF.Abs(CarriedLightFrame.Attenuation(1f) - 1f / .73f) < .0001f &&
              MathF.Abs(CarriedLightFrame.Attenuation(10f) - .1f) < .0001f,
            "fixed 0/.7/.03 carried-light attenuation drift");

        CarriedLightPlacement[] candidates = Enumerable.Range(0, 10)
            .Select(i => new CarriedLightPlacement($"light-{i}", new Vector3(i, 0, 0),
                Vector3.One)).Append(new CarriedLightPlacement("light-0", Vector3.Zero,
                new Vector3(2))).ToArray();
        CarriedLightFrame.Commit(candidates, Vector3.Zero);
        Check(CarriedLightFrame.Current.Count == CarriedLightFrame.MaxCandidates &&
              CarriedLightFrame.Current.Count(light => light.Key == "light-0") == 1 &&
              CarriedLightFrame.Current[0].Position == Vector3.Zero,
            "carried-light dedupe/camera-nearest candidate cap drift");
        CarriedLightFrame.Commit([], Vector3.Zero);

        string root = ClientConfig.FindRepoRoot();
        string attached = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "AttachedItemRenderer.cs"));
        string parser = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats", "M2Reader.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(parser.Contains("LIGHT_STRIDE_VANILLA = 0xD4", StringComparison.Ordinal) &&
              parser.Contains("ReadTrackFirstByte(data, record + 0xB8) == 0", StringComparison.Ordinal) &&
              attached.Contains("Vector3.Transform(light.Position, itemRoot)", StringComparison.Ordinal) &&
              program.Contains("CarriedLightFrame.Commit", StringComparison.Ordinal),
            "M2 carried-light parse/animated item-root publication drift");

        string[] shaders = ["terrain.frag", "character.frag", "doodad.frag", "wmo.frag"];
        foreach (string shaderName in shaders)
        {
            string shader = SourceText.Read(Path.Combine(root, "MSUIClient", "Shaders", shaderName));
            Check(shader.Contains("uPointLightCount", StringComparison.Ordinal) &&
                  shader.Contains("carriedPointLight", StringComparison.Ordinal) &&
                  shader.Contains("0.7*d + 0.03*d*d", StringComparison.Ordinal),
                $"{shaderName} lost nearest-three carried-light contribution");
        }

        CheckActualTorch(root);
    }

    private static void CheckActualTorch(string root)
    {
        string data = ClientDataRoot.Path;
        if (!Directory.Exists(data)) return;
        using var mpq = new MpqMount(data);
        byte[] bytes = mpq.ReadFile(@"Item\ObjectComponents\Weapon\Club_1H_Torch_A_01.m2") ??
            throw new InvalidDataException("actual torch item model unavailable");
        M2Model model = M2Reader.Parse(bytes) ??
            throw new InvalidDataException("actual torch item model did not parse");
        M2Light[] casting = model.Lights.Where(light => light.Casts).ToArray();
        Check(casting.Length == 1 && casting[0].Type == 1 &&
              MathF.Abs(casting[0].DiffuseIntensity - 3f) < .001f &&
              Vector3.DistanceSquared(casting[0].DiffuseColor,
                  new Vector3(.4666667f, .2901961f, .13333334f)) < .00001f,
            "actual torch carried-light count/color/intensity drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
