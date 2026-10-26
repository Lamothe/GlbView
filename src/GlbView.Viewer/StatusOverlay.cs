using System.Numerics;
using Silk.NET.OpenGL;

namespace GlbView.Viewer;

/// <summary>
/// Minimal on-screen text: a hand-drawn 5x7 bitmap font rasterised as solid quads.
/// No textures, no dependencies — one shader, one dynamic vertex buffer. Used for
/// load/close status so long operations are never a silent freeze.
/// </summary>
internal sealed class StatusOverlay : IDisposable
{
    private const int GlyphWidth = 5;
    private const int GlyphHeight = 7;
    private const int Advance = 6; // 5 px glyph + 1 px gap
    private const int VertexFloats = 6; // pos2 + colour4

    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _vbo;
    private float[] _verts = new float[VertexFloats * 6 * 1024];
    private int _vertsUsed;
    private int _width;
    private int _height;
    private bool _disposed;

    public StatusOverlay(GL gl)
    {
        _gl = gl;

        const string vs = """
            #version 330 core
            layout (location = 0) in vec2 aPos;
            layout (location = 1) in vec4 aColor;
            uniform vec2 uResolution;
            out vec4 vColor;
            void main()
            {
                vec2 ndc = (aPos / uResolution) * 2.0 - 1.0;
                gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
                vColor = aColor;
            }
            """;
        const string fs = """
            #version 330 core
            in vec4 vColor;
            out vec4 FragColor;
            void main() { FragColor = vColor; }
            """;

        _program = gl.CreateProgram();
        var v = Compile(gl, ShaderType.VertexShader, vs);
        var f = Compile(gl, ShaderType.FragmentShader, fs);
        gl.AttachShader(_program, v);
        gl.AttachShader(_program, f);
        gl.LinkProgram(_program);
        gl.GetProgram(_program, GLEnum.LinkStatus, out var linked);
        if (linked == 0)
        {
            throw new InvalidOperationException($"Status shader link failed: {gl.GetProgramInfoLog(_program)}");
        }

        gl.DeleteShader(v);
        gl.DeleteShader(f);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, VertexFloats * sizeof(float), null);
            gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, VertexFloats * sizeof(float), (void*)(2 * sizeof(float)));
        }

        gl.EnableVertexAttribArray(0);
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draws one status screen: title, detail line, and a progress bar
    /// (0..1 filled; negative draws the indeterminate sweep).
    /// </summary>
    public void DrawStatus(int width, int height, string title, string? detail, float progress, float elapsed)
    {
        _width = width;
        _height = height;
        _vertsUsed = 0;

        const int scale = 3;
        var centerX = width / 2f;

        var y = (height / 2f) - 60f;
        DrawText(title, centerX - (TextWidth(title, scale) / 2f), y, scale, new Vector4(0.92f, 0.94f, 0.98f, 1f));

        y += (GlyphHeight * scale) + 14f;
        if (detail is not null)
        {
            DrawText(detail, centerX - (TextWidth(detail, scale) / 2f), y, scale, new Vector4(0.62f, 0.68f, 0.78f, 1f));
        }

        var barW = MathF.Min(width * 0.5f, 480f);
        var barH = 10f;
        var barX = centerX - (barW / 2f);
        var barY = y + (GlyphHeight * scale) + 26f;
        PushQuad(new Vector4(0.20f, 0.23f, 0.30f, 1f), barX, barY, barW, barH);

        // Indeterminate bars sweep; determinate bars fill.
        var frac = progress >= 0f
            ? Math.Clamp(progress, 0f, 1f)
            : 0.25f + (0.5f * (0.5f + (0.5f * MathF.Sin(elapsed * 3f))));
        PushQuad(new Vector4(0.35f, 0.62f, 0.95f, 1f), barX, barY, barW * frac, barH);

        Flush();
    }

    /// <summary>Draws one line of text at pixel coordinates (also usable outside DrawStatus).</summary>
    public void DrawText(string text, float x, float y, int scale, Vector4 color)
    {
        var cursor = x;
        foreach (var raw in text)
        {
            var glyph = BitmapFont.Get(char.ToUpperInvariant(raw));
            for (var row = 0; row < GlyphHeight; row++)
            {
                var art = glyph[row];
                for (var col = 0; col < GlyphWidth; col++)
                {
                    if (art[col] == '#')
                    {
                        PushQuad(color, cursor + (col * scale), y + (row * scale), scale, scale);
                    }
                }
            }

            cursor += Advance * scale;
        }
    }

    public static float TextWidth(string text, int scale) => text.Length * Advance * scale;

    private void PushQuad(Vector4 color, float x, float y, float w, float h)
    {
        var needed = _vertsUsed + (6 * VertexFloats);
        if (needed > _verts.Length)
        {
            Array.Resize(ref _verts, Math.Max(needed, _verts.Length * 2));
        }

        Span<float> v = _verts.AsSpan(_vertsUsed);
        for (var i = 0; i < 6; i++)
        {
            // Two triangles: (tl, tr, br) and (tl, br, bl).
            var (px, py) = i switch
            {
                0 => (x, y),
                1 => (x + w, y),
                2 => (x + w, y + h),
                3 => (x, y),
                4 => (x + w, y + h),
                _ => (x, y + h),
            };
            var o = i * VertexFloats;
            v[o + 0] = px;
            v[o + 1] = py;
            v[o + 2] = color.X;
            v[o + 3] = color.Y;
            v[o + 4] = color.Z;
            v[o + 5] = color.W;
        }

        _vertsUsed += 6 * VertexFloats;
    }

    private void Flush()
    {
        if (_vertsUsed == 0)
        {
            return;
        }

        _gl.UseProgram(_program);
        _gl.Uniform2(_gl.GetUniformLocation(_program, "uResolution"), (float)_width, (float)_height);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (float* p = _verts)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertsUsed * sizeof(float)), p, BufferUsageARB.StreamDraw);
            }
        }

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.DepthTest);
        // Overlay quads are back-facing after the shader's y-flip, so any cull state
        // left behind by the model renderer would erase the text entirely.
        _gl.Disable(EnableCap.CullFace);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_vertsUsed / VertexFloats));
        _gl.BindVertexArray(0);
        _vertsUsed = 0;
    }

    private static uint Compile(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, GLEnum.CompileStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException($"{type} compile failed: {gl.GetShaderInfoLog(shader)}");
        }

        return shader;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
    }
}
