using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MSUIClient.Engine;
using Silk.NET.OpenGL;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// Frame-burst recorder — visual evidence for transitions (2026-10-04).
//
// A log line saying a portal handoff "promoted" its prepared world says nothing about
// what the player SAW (owner, watching a karting run the log called seamless: "you had
// PLENTY of glitching moments"). This keeps a rolling 1.5 s of small frames and, when a
// crossing fires, records 6 s more, so the moment itself can be looked at frame by frame.
//
// Live protocol: `record-crossings <name>` arms it; every real-portal crossing and every
// world switch writes dumps/frames/<name>-<NN>-<why>/fNNN.png plus frames.csv (time, frame
// ms, map, position). Frames are 640 px wide, read back after the HUD pass, encoded off
// the render thread.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private const double FrameRecInterval = 0.1;
    private const double FrameRecPreSeconds = 1.5;
    private const double FrameRecPostSeconds = 6.0;
    private const int FrameRecWidth = 640;

    private sealed record RecordedFrame(byte[] Rgba, int Width, int Height, double At, string Meta);

    private string? _frameRecName;
    private double _frameRecNextAt;
    private double _frameRecPostUntil = double.NegativeInfinity;
    private double _frameRecLastFrameAt;
    private int _frameRecBurst;
    private int _frameRecIndex;
    private string? _frameRecDir;
    private readonly Queue<RecordedFrame> _frameRecRing = new();
    private readonly StringBuilder _frameRecCsv = new();
    private readonly ConcurrentQueue<Task> _frameRecWrites = new();
    private uint _frameRecFbo, _frameRecRbo;
    private int _frameRecFboW, _frameRecFboH;
    private bool _frameRecBlitBroken;

    private void StartFrameRecorder(string name)
    {
        _frameRecName = SafeCaptureName(name);
        _window.RestoreIfMinimized();
        _frameRecRing.Clear();
        _frameRecBurst = 0;
        Console.WriteLine($"[frame-rec] armed '{_frameRecName}': {FrameRecPreSeconds}s before + {FrameRecPostSeconds}s after each crossing");
    }

    /// <summary>A transition worth seeing happened (portal crossing, world switch): keep the pre-roll, record on.</summary>
    private void NoteFrameRecorderEvent(string why)
    {
        if (_frameRecName is null) return;
        _window.RestoreIfMinimized();
        double now = RealPortalNow();
        if (now < _frameRecPostUntil)
        {
            _frameRecPostUntil = now + FrameRecPostSeconds;          // a second event inside a burst extends it
            _frameRecCsv.Append(CultureInfo.InvariantCulture, $"# event {why} at {now:F3}\n");
            return;
        }
        _frameRecBurst++;
        _frameRecIndex = 0;
        _frameRecDir = Path.Combine(_config.RepoRoot, "dumps", "frames", $"{_frameRecName}-{_frameRecBurst:D2}-{SafeCaptureName(why)}");
        Directory.CreateDirectory(_frameRecDir);
        _frameRecCsv.Clear();
        _frameRecCsv.Append("frame,t,frameMs,meta\n");
        _frameRecCsv.Append(CultureInfo.InvariantCulture, $"# event {why} at {now:F3}\n");
        _frameRecPostUntil = now + FrameRecPostSeconds;
        while (_frameRecRing.TryDequeue(out RecordedFrame? f)) WriteRecordedFrame(f);
        Console.WriteLine($"[frame-rec] burst {_frameRecBurst} '{why}' -> {_frameRecDir}");
    }

    /// <summary>Called once per frame after the HUD pass (OverlayTop).</summary>
    private void CaptureFrameRecorder()
    {
        if (_frameRecName is null || _gl is null) return;
        _window.RestoreIfMinimized();       // a minimized window renders nothing: armed recording keeps it up
        double now = RealPortalNow();
        double frameMs = (now - _frameRecLastFrameAt) * 1000.0;
        _frameRecLastFrameAt = now;
        bool post = now < _frameRecPostUntil;
        // Inside a burst, a long frame is itself evidence: always keep the frame after a hitch.
        if (now < _frameRecNextAt && !(post && frameMs > 50)) return;
        _frameRecNextAt = now + FrameRecInterval;

        RecordedFrame? frame = ReadRecorderFrame(now, frameMs);
        if (frame is null) return;
        if (post)
        {
            WriteRecordedFrame(frame);
            return;
        }
        if (_frameRecDir is not null)
        {
            // A burst just ended: flush its log.
            File.WriteAllText(Path.Combine(_frameRecDir, "frames.csv"), _frameRecCsv.ToString());
            _frameRecDir = null;
        }
        _frameRecRing.Enqueue(frame);
        while (_frameRecRing.Count > 0 && now - _frameRecRing.Peek().At > FrameRecPreSeconds) _frameRecRing.Dequeue();
    }

    private void WriteRecordedFrame(RecordedFrame frame)
    {
        if (_frameRecDir is null) return;
        int index = _frameRecIndex++;
        string path = Path.Combine(_frameRecDir, $"f{index:D3}.png");
        _frameRecCsv.Append(CultureInfo.InvariantCulture, $"{index},{frame.At:F3},{frame.Meta}\n");
        _frameRecWrites.Enqueue(Task.Run(() => PortraitRenderTarget.SaveRgbaPng(path, frame.Width, frame.Height, frame.Rgba)));
        if (_frameRecWrites.Count > 64)
            while (_frameRecWrites.TryPeek(out Task? t) && t.IsCompleted) _frameRecWrites.TryDequeue(out _);
        if (index % 20 == 0) File.WriteAllText(Path.Combine(_frameRecDir, "frames.csv"), _frameRecCsv.ToString());
    }

    private unsafe RecordedFrame? ReadRecorderFrame(double now, double frameMs)
    {
        GL gl = _gl!;
        System.Numerics.Vector2 size = _window.FramebufferSize;
        int sw = (int)size.X, sh = (int)size.Y;
        if (sw <= 1 || sh <= 1) return null;
        int w = Math.Min(FrameRecWidth, sw), h = Math.Max(1, sh * w / sw);
        string meta = string.Create(CultureInfo.InvariantCulture,
            $"{frameMs:F1},map={(_net?.Player?.Map is uint m ? (int)m : -1)} pos={_controller.Position.X:F1}|{_controller.Position.Y:F1}|{_controller.Position.Z:F1} " +
            $"terrain={(_terrain is null ? "none" : $"{_terrain.TileCount}t {_terrain.DrawnLastFrame}d {_terrain.TrianglesLastFrame}tri fog={_terrain.FogStart:F0}-{_terrain.FogEnd:F0} tex0={_terrain.FirstTileTextureCount} {_terrain.GlProbe()}")} glerr={(int)gl.GetError():X}");
        byte[] bottomUp = new byte[w * h * 4];

        if (!_frameRecBlitBroken)
        {
            int readFb, drawFb, samples;
            gl.GetInteger(GetPName.ReadFramebufferBinding, out readFb);
            gl.GetInteger(GetPName.DrawFramebufferBinding, out drawFb);
            gl.GetInteger(GetPName.Samples, out samples);
            if (samples > 0)
            {
                // A multisampled framebuffer cannot be blitted to a different size (GL error, not a fallback).
                _frameRecBlitBroken = true;
                Console.WriteLine("[frame-rec] multisampled framebuffer; reading full frames");
                goto Full;
            }
            if (_frameRecFbo == 0 || _frameRecFboW != w || _frameRecFboH != h)
            {
                if (_frameRecFbo != 0) { gl.DeleteFramebuffer(_frameRecFbo); gl.DeleteRenderbuffer(_frameRecRbo); }
                _frameRecFbo = gl.GenFramebuffer();
                _frameRecRbo = gl.GenRenderbuffer();
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _frameRecRbo);
                gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)w, (uint)h);
                gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _frameRecFbo);
                gl.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                    RenderbufferTarget.Renderbuffer, _frameRecRbo);
                _frameRecFboW = w; _frameRecFboH = h;
            }
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _frameRecFbo);
            gl.BlitFramebuffer(0, 0, sw, sh, 0, 0, w, h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            bool ok = gl.GetError() == GLEnum.NoError;
            if (ok)
            {
                gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _frameRecFbo);
                fixed (byte* p = bottomUp) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)readFb);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)drawFb);
            if (!ok)
            {
                _frameRecBlitBroken = true;   // e.g. a multisampled default framebuffer: scaled blits are illegal
                Console.WriteLine("[frame-rec] GPU downscale unavailable; reading full frames");
            }
            else return Finish(bottomUp, w, h);
        }

        Full:
        byte[] full = new byte[sw * sh * 4];
        fixed (byte* p = full) gl.ReadPixels(0, 0, (uint)sw, (uint)sh, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        for (int y = 0; y < h; y++)
        {
            int sy = y * sh / h;
            for (int x = 0; x < w; x++)
                System.Buffer.BlockCopy(full, (sy * sw + x * sw / w) * 4, bottomUp, (y * w + x) * 4, 4);
        }
        return Finish(bottomUp, w, h);

        RecordedFrame Finish(byte[] src, int fw, int fh)
        {
            byte[] top = new byte[src.Length];
            int stride = fw * 4;
            for (int y = 0; y < fh; y++) System.Buffer.BlockCopy(src, y * stride, top, (fh - 1 - y) * stride, stride);
            for (int i = 3; i < top.Length; i += 4) top[i] = 255;
            return new RecordedFrame(top, fw, fh, now, meta);
        }
    }
}
