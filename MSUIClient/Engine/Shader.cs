using System.Numerics;
using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// A compiled GL shader program plus uniform caching.
///
/// Compile errors throw with the full driver log and the offending source, so
/// a bad shader says exactly what is wrong instead of silently rendering black.
/// </summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;
    private readonly Dictionary<string, int> _uniforms = [];

    public string Name { get; }

    private Shader(GL gl, uint handle, string name)
    {
        _gl = gl;
        _handle = handle;
        Name = name;
    }

    /// <summary>Load a vertex/fragment pair from the Shaders folder next to the exe.</summary>
    public static Shader FromFiles(GL gl, string vertPath, string fragPath)
    {
        var name = Path.GetFileNameWithoutExtension(vertPath);
        return FromSource(gl, name, File.ReadAllText(vertPath), File.ReadAllText(fragPath));
    }

    public static Shader FromSource(GL gl, string name, string vertexSource, string fragmentSource)
    {
        uint vert = Compile(gl, ShaderType.VertexShader, Sanitize(vertexSource), $"{name}.vert");
        uint frag = Compile(gl, ShaderType.FragmentShader, Sanitize(fragmentSource), $"{name}.frag");

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vert);
        gl.AttachShader(program, frag);
        gl.LinkProgram(program);

        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"shader '{name}' failed to link:\n{log}");
        }

        gl.DetachShader(program, vert);
        gl.DetachShader(program, frag);
        gl.DeleteShader(vert);
        gl.DeleteShader(frag);

        Console.WriteLine($"[shader] {name} compiled and linked");
        return new Shader(gl, program, name);
    }

    /// <summary>
    /// Strip anything a GLSL compiler can choke on before it ever sees the
    /// source: a leading UTF-8 BOM, and any non-ASCII byte.
    ///
    /// This is not paranoia. Intel's compiler aborts with a bogus
    /// "pre-mature EOF" syntax error on a single non-ASCII character even
    /// inside a comment, and a BOM ahead of #version fails on most drivers.
    /// Both are invisible when you print the source to diagnose it, which
    /// makes them expensive to find. One em-dash in a comment header cost a
    /// debugging round trip; sanitizing here means it can never happen again.
    ///
    /// Replacement is a space, so column numbers in driver errors stay honest.
    /// </summary>
    private static string Sanitize(string source)
    {
        if (source.Length > 0 && source[0] == '\uFEFF') source = source[1..];

        Span<char> buffer = source.Length <= 4096 ? stackalloc char[source.Length] : new char[source.Length];
        int replaced = 0;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c > 127) { buffer[i] = ' '; replaced++; }
            else buffer[i] = c;
        }

        if (replaced > 0)
            Console.WriteLine($"[shader] stripped {replaced} non-ASCII character(s) before compiling");

        return new string(buffer);
    }

    private static uint Compile(GL gl, ShaderType type, string source, string label)
    {
        uint handle = gl.CreateShader(type);
        gl.ShaderSource(handle, source);
        gl.CompileShader(handle);

        gl.GetShader(handle, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
        {
            string log = gl.GetShaderInfoLog(handle);
            gl.DeleteShader(handle);

            // Number the source so driver line references are usable.
            var numbered = string.Join('\n',
                source.Split('\n').Select((l, i) => $"{i + 1,4}| {l}"));

            throw new InvalidOperationException(
                $"shader '{label}' failed to compile:\n{log}\n--- source ---\n{numbered}");
        }

        return handle;
    }

    public uint Handle => _handle;

    public void Use() => _gl.UseProgram(_handle);

    private int Location(string name)
    {
        if (_uniforms.TryGetValue(name, out int cached)) return cached;

        int loc = _gl.GetUniformLocation(_handle, name);
        if (loc == -1)
        {
            // Not fatal: the compiler strips uniforms that don't affect output.
            Console.WriteLine($"[shader] {Name}: uniform '{name}' not found (optimised out?)");
        }
        _uniforms[name] = loc;
        return loc;
    }

    public void Set(string name, int value) => _gl.Uniform1(Location(name), value);
    public void Set(string name, float value) => _gl.Uniform1(Location(name), value);
    public void Set(string name, Vector2 v) => _gl.Uniform2(Location(name), v.X, v.Y);
    public void Set(string name, Vector3 v) => _gl.Uniform3(Location(name), v.X, v.Y, v.Z);
    public void Set(string name, Vector4 v) => _gl.Uniform4(Location(name), v.X, v.Y, v.Z, v.W);

    /// <summary>
    /// Upload a vec4 array uniform - the skinning path.
    ///
    /// Bone matrices go up as three vec4 per bone holding the ROWS of the
    /// transform rather than as a mat4 or mat3x4 array. That is deliberate:
    /// it sidesteps the column-order question entirely (the shader does three
    /// dot products), it is a quarter smaller than mat4, and one flat float
    /// array uploads in a single call with no per-element location lookups.
    ///
    /// <paramref name="count"/> is the number of vec4s to send, which is
    /// normally boneCount * 3 and NOT the length of the buffer - the buffer is
    /// sized for the shader's maximum and only partly filled.
    /// </summary>
    public unsafe void SetVec4Array(string name, float[] values, int count)
    {
        int loc = Location(name);
        if (loc == -1 || count <= 0) return;

        if (count * 4 > values.Length)
            throw new ArgumentException(
                $"{Name}: asked to upload {count} vec4 ({count * 4} floats) from a " +
                $"{values.Length}-float buffer", nameof(count));

        fixed (float* p = values)
        {
            _gl.Uniform4(loc, (uint)count, p);
        }
    }

    public unsafe void Set(string name, Matrix4x4 m)
    {
        int loc = Location(name);
        if (loc == -1) return;
        // transpose = false is deliberate. System.Numerics is row-major in
        // memory; GL reads those bytes as column-major, which performs the
        // row->column flip GLSL needs. See Camera.ViewProjection.
        _gl.UniformMatrix4(loc, 1, false, (float*)&m);
    }

    public void Dispose() => _gl.DeleteProgram(_handle);
}
