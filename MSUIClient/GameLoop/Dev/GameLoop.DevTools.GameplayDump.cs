using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using Silk.NET.OpenGL;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct GameplayLayoutRow(
        string Id, float[] Authored, float[] Screen);

    private bool _gameplayDumpRequested;
    private bool _gameplayDumpArmed;
    private string? _gameplayDumpDirectoryOverride;
    private readonly List<GameplayLayoutRow> _gameplayDumpLayout = [];
    private readonly List<ActionButtonVerdict> _gameplayDumpVisibleActions = [];

    private void ArmGameplayDump()
    {
        if (_config.DevTools || _liveRunOptions is not null) _gameplayDumpRequested = true;
    }

    private void BeginGameplayDumpFrame()
    {
        if (!_gameplayDumpRequested || (!_config.DevTools && _liveRunOptions is null)) return;
        _gameplayDumpRequested = false;
        _gameplayDumpArmed = true;
        _gameplayDumpLayout.Clear();
        _gameplayDumpVisibleActions.Clear();
    }

    private void CollectGameplayLayout(string id, float x, float y, float width, float height,
        Vector2 screenMin, Vector2 screenSize)
    {
        if (!_gameplayDumpArmed) return;
        _gameplayDumpLayout.Add(new GameplayLayoutRow(id,
            [x, y, width, height],
            [screenMin.X, screenMin.Y, screenSize.X, screenSize.Y]));
    }

    private void CollectGameplayAction(in ActionButtonVerdict verdict)
    {
        if (_gameplayDumpArmed) _gameplayDumpVisibleActions.Add(verdict);
    }

    private void FinishGameplayDump()
    {
        if (!_gameplayDumpArmed) return;
        _gameplayDumpArmed = false;

        string name = _currentVantage ?? "unsaved-view";
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string fileName = $"gameplay-{SafeCaptureName(name)}-{stamp}";
        string relativeJson = Path.Combine("dumps", fileName + ".json").Replace('\\', '/');
        string relativePng = Path.Combine("dumps", fileName + ".png").Replace('\\', '/');
        string jsonPath = _gameplayDumpDirectoryOverride is null ? Path.Combine(_config.RepoRoot, relativeJson) :
            Path.Combine(_gameplayDumpDirectoryOverride, fileName + ".json");
        string pngPath = _gameplayDumpDirectoryOverride is null ? Path.Combine(_config.RepoRoot, relativePng) :
            Path.Combine(_gameplayDumpDirectoryOverride, fileName + ".png");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            object dump = BuildGameplayDump(name);
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(dump, DumpJson));

            bool png = false;
            try
            {
                png = TrySaveGameplayScreenshot(pngPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gdump] screenshot unavailable - {ex.Message}");
            }
            Console.WriteLine($"[gdump] wrote {relativeJson}{(png ? " (+ .png)" : "")}");
            PruneAutomaticDumps(Path.GetDirectoryName(jsonPath)!, fileName);
            if (_liveRunOptions?.Background != true) ImGui.SetClipboardText(relativeJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gdump] failed - {ex.Message}");
        }
        finally
        {
            _gameplayDumpDirectoryOverride = null;
            _gameplayDumpLayout.Clear();
            _gameplayDumpVisibleActions.Clear();
        }
    }

    /// <summary>Machine-generated dump families and how many files (png + json) each keeps.</summary>
    private static readonly (string Prefix, int Keep)[] AutomaticDumpFamilies =
    {
        ("gameplay-wb-", 60),      // World Builder surveys / script shots (reviewed as contact sheets)
        ("gameplay-fight-", 60),   // live-run fight captures (start + outcome per fight)
    };

    /// <summary>
    /// Owner rule (2026-09-26): don't collect tens of thousands of screenshots. After writing a dump of an
    /// AUTOMATIC family, delete that family's oldest files beyond its cap. Hand-named evidence dumps
    /// (any other prefix) are never touched.
    /// </summary>
    private static void PruneAutomaticDumps(string directory, string justWritten)
    {
        foreach (var (prefix, keep) in AutomaticDumpFamilies)
        {
            if (!justWritten.StartsWith(prefix, StringComparison.Ordinal)) continue;
            try
            {
                var old = new DirectoryInfo(directory).EnumerateFiles(prefix + "*")
                    .Where(f => f.Extension is ".png" or ".json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep).ToList();
                foreach (var f in old) f.Delete();
                if (old.Count > 0) Console.WriteLine($"[gdump] pruned {old.Count} old {prefix}* file(s) (keeping {keep})");
            }
            catch (Exception ex) { Console.WriteLine($"[gdump] prune failed - {ex.Message}"); }
        }
    }

    private object BuildGameplayDump(string name)
    {
        double now = NowSeconds();
        WorldEntity? player = _net is not null &&
            _entities.TryGet(_net.PlayerGuid, out WorldEntity playerEntity)
                ? playerEntity : null;
        WorldEntity? selection = _selectionGuid != 0 &&
            _entities.TryGet(_selectionGuid, out WorldEntity selectionEntity)
                ? selectionEntity : null;

        object? selectionFraming = selection is { IsCreature: true } creature &&
            _creatures?.TryGetPortraitFraming(creature, out CreatureRenderer.PortraitFraming framing) == true
                ? framing : null;
        string? playerOverrideKey = _character is null ? null : PlayerPortraitKey(_character);
        if (playerOverrideKey is not null && _portraitOverrides?.Find(playerOverrideKey) is null)
            playerOverrideKey = null;
        string? targetOverrideKey = selection is { IsCreature: true }
            ? CreaturePortraitKey(selection.DisplayId) : null;
        if (targetOverrideKey is not null && _portraitOverrides?.Find(targetOverrideKey) is null)
            targetOverrideKey = null;

        IReadOnlyList<IVerdict> verdictSnapshot = _verdicts.SnapshotAll();
        PortraitVerdict[] playerPortraits = verdictSnapshot.OfType<PortraitVerdict>()
            .Where(verdict => verdict.Subject == PortraitSubject.Player).ToArray();
        PortraitVerdict? playerPortrait = playerPortraits.Length == 0 ? null : playerPortraits[^1];
        PortraitVerdict[] targetPortraits = verdictSnapshot.OfType<PortraitVerdict>()
            .Where(verdict => verdict.Subject == PortraitSubject.Target).ToArray();
        PortraitVerdict? targetPortrait = targetPortraits.Length == 0 ? null : targetPortraits[^1];

        object[] AnimatorTracks(string unit) => Enumerable.Range(0, 3).Select(track =>
        {
            bool found = _lastAnimChoices.TryGetValue((unit, track), out var state);
            AnimChoice[] choices = verdictSnapshot.OfType<AnimChoice>()
                .Where(choice => choice.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase) &&
                                 choice.Track == track).ToArray();
            AnimChoice? last = choices.Length == 0 ? null : choices[^1];
            return (object)new
            {
                track,
                requestedId = found ? state.Requested : -1,
                playedId = found ? state.Played : -1,
                last,
            };
        }).ToArray();

        string selectionUnit = selection is { IsCreature: true }
            ? $"creature:{selection.DisplayId}" : "selection";
        Vector2 framebuffer = _window.FramebufferSize;
        Vector2 display = ImGui.GetIO().DisplaySize;
        object[] equipment = _character?.Equipment.Pieces.Select(piece => (object)new
        {
            slot = piece.EquipmentSlot,
            displayId = piece.DisplayId,
            inventoryType = piece.InventoryType,
            name = piece.Name,
        }).ToArray() ?? Array.Empty<object>();

        return new
        {
            name,
            takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            map = _config.Start.MapName,
            render = new
            {
                framebuffer = new[] { (int)framebuffer.X, (int)framebuffer.Y },
                msaaSamples = _window.FramebufferSamples,
                multisamplingEnabled = _window.MultisamplingEnabled,
                anisotropy = Settings.Display.Anisotropy,
                fieldOfView = _window.Camera.FieldOfViewDegrees,
                terrainShadowStrength = _terrain?.AuthoredShadowStrength,
                unitShadowOpacity = _unitShadows?.Opacity,
                glow = _glow is null ? null : new
                {
                    enabled = _glow.Enabled,
                    gain = _glow.Gain,
                },
                painterly = _painterly is null ? null : new
                {
                    enabled = _painterly.Enabled,
                    hud = Settings.Display.PainterlyUi,
                    bands = _painterly.Bands,
                    detail = _painterly.Detail,
                    ink = _painterly.Ink,
                    inkThreshold = _painterly.InkThreshold,
                    silhouette = _painterly.Silhouette,
                    distanceCalm = _painterly.DepthFade,
                    calmStart = _painterly.CalmStart,
                    calmEnd = _painterly.CalmEnd,
                    saturation = _painterly.Saturation,
                    contrast = _painterly.Contrast,
                    lift = _painterly.Lift,
                    warmth = _painterly.Warmth,
                    grain = _painterly.Grain,
                    dither = _painterly.Dither,
                    canvasHeight = _painterly.CanvasHeight,
                    depthAvailable = _painterly.DepthAvailable,
                },
            },
            scenario = new
            {
                player = new
                {
                    race = _character?.Race ?? "",
                    gender = _character?.Gender ?? "",
                    level = player?.Level ?? _net?.Player?.Level ?? 0,
                    guid = $"0x{_net?.PlayerGuid ?? 0:X16}",
                    position = player is null ? Array.Empty<float>() : V(player.Position),
                    health = player?.Fields.Health ?? 0,
                    maxHealth = player?.Fields.MaxHealth ?? 0,
                    powerType = player?.Fields.PowerType ?? 0,
                    power = player?.Fields.ActivePower ?? 0,
                    maxPower = player?.Fields.ActiveMaxPower ?? 0,
                    mounted = (player?.Fields.MountDisplayId ?? 0) != 0,
                    dead = player?.IsDead ?? false,
                },
                equipment,
                selection = new
                {
                    guid = $"0x{selection?.Guid ?? 0:X16}",
                    displayId = selection?.DisplayId ?? 0,
                    scale = selection?.Scale ?? 0f,
                    reaction = selection is null ? FactionReaction.Neutral : ReactionTargetTowardPlayer(selection),
                    dead = selection?.IsDead ?? false,
                    lootable = selection?.Fields.Lootable ?? false,
                    distanceToPlayer = selection is null || player is null
                        ? -1f : Vector3.Distance(player.Position, selection.Position),
                    portraitFraming = selectionFraming,
                },
                pendingCast = new
                {
                    spellId = _pendingCastSpell,
                    autoRepeatSpellId = _autoRepeatSpell,
                    queuedMeleeSpellId = _queuedMeleeSpell,
                    castBarSpellId = _castBarSpell,
                    stage = _castBarPhase,
                    remainingSeconds = Math.Max(0.0, _castBarEnds - now),
                },
                panelsOpen = new
                {
                    character = _characterOpen,
                    spellbook = _spellbookOpen,
                    backpack = _backpackOpen,
                    equippedBags = (bool[])_equippedBagOpen.Clone(),
                    loot = _loot.IsOpen,
                    settings = _settingsOpen,
                    portraitLab = _labPanelOpen,
                },
                uiScale = new
                {
                    effective = GameplayUiScale(),
                    configuredPreference = _skin?.Scale ?? _config.Window.UiScale,
                    framebuffer = new[] { framebuffer.X, framebuffer.Y },
                    displaySize = new[] { display.X, display.Y },
                },
            },
            portraits = new
            {
                player = new
                {
                    latest = playerPortrait,
                    usable = _playerPortraitUsable,
                    dirty = _playerPortraitDirty,
                    retryAt = _playerPortraitRetryAt,
                    activeOverrideKey = playerOverrideKey,
                },
                target = new
                {
                    latest = targetPortrait,
                    usable = _targetPortraitUsable,
                    retryAt = _targetPortraitRetryAt,
                    activeOverrideKey = targetOverrideKey,
                },
            },
            actionBar = new
            {
                page = _actionPage,
                packedSlots = Enumerable.Range(0, 120)
                    .Select(slot => _actions[slot]?.Packed ?? 0).ToArray(),
                visible = _gameplayDumpVisibleActions.ToArray(),
                recent = verdictSnapshot.OfType<ActionButtonVerdict>().TakeLast(20).ToArray(),
            },
            animator = new
            {
                player = AnimatorTracks("player"),
                selection = AnimatorTracks(selectionUnit),
                recent = verdictSnapshot.OfType<AnimChoice>().TakeLast(20).ToArray(),
            },
            combat = new
            {
                intentOn = _attackTargetGuid != 0,
                targetGuid = $"0x{_attackTargetGuid:X16}",
                serverEngaged = _net is not null && _combat.IsEngaged(_net.PlayerGuid),
                swingTimerOwner = "server",
                clientRangeEligibility = "unchecked",
                clientArcEligibility = "unchecked",
                traceActive = _combatTraceWriter is not null,
                tracePath = _combatTracePath,
                recent = verdictSnapshot.OfType<CombatVerdict>().TakeLast(50).ToArray(),
            },
            verdicts = verdictSnapshot.Select(verdict => new
            {
                channel = verdict.Channel,
                time = verdict.Time,
                line = verdict.ToLine(),
                data = (object)verdict,
            }).ToArray(),
            wire = _wire.Snapshot().TakeLast(100).ToArray(),
            layout = _gameplayDumpLayout.ToArray(),
        };
    }

    private static string SafeCaptureName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(c => invalid.Contains(c) ? '-' : c))
            .Trim().Replace(' ', '-');
    }

    private unsafe bool TrySaveGameplayScreenshot(string path)
    {
        if (_gl is null) return false;
        Vector2 size = _window.FramebufferSize;
        if (size.X <= 1 || size.Y <= 1)
        {
            Console.WriteLine($"[capture] refused unavailable framebuffer {size.X}x{size.Y}: {path}");
            return false;
        }
        int width = Math.Max(1, (int)size.X);
        int height = Math.Max(1, (int)size.Y);
        byte[] bottomUp = new byte[checked(width * height * 4)];
        fixed (byte* pixels = bottomUp)
            _gl.ReadPixels(0, 0, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        // Scene alpha carries painterly category importance for opaque world
        // geometry. A desktop screenshot is still an opaque image; exposing the
        // internal channel here would make terrain look translucent in editors.
        for (int i = 3; i < bottomUp.Length; i += 4) bottomUp[i] = 255;
        int stride = width * 4;
        byte[] topDown = new byte[bottomUp.Length];
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(bottomUp, y * stride, topDown, (height - 1 - y) * stride, stride);
        PortraitRenderTarget.SaveRgbaPng(path, width, height, topDown);
        return true;
    }

    /// <summary>Evidence-only framebuffer capture for high-volume spell sequences.
    /// The acting renderer is sampled unchanged; only the stored evidence image is
    /// reduced to a bounded 480px width so a full class matrix remains reviewable.</summary>
    private unsafe bool TrySaveAnimationSequenceFrame(string path)
    {
        if (_gl is null) return false;
        Vector2 size = _window.FramebufferSize;
        if (size.X <= 1 || size.Y <= 1)
        {
            Console.WriteLine($"[animation-frame] refused unavailable framebuffer {size.X}x{size.Y}: {path}");
            return false;
        }
        int sourceWidth = Math.Max(1, (int)size.X);
        int sourceHeight = Math.Max(1, (int)size.Y);
        byte[] bottomUp = new byte[checked(sourceWidth * sourceHeight * 4)];
        fixed (byte* pixels = bottomUp)
            _gl.ReadPixels(0, 0, (uint)sourceWidth, (uint)sourceHeight,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        for (int i = 3; i < bottomUp.Length; i += 4) bottomUp[i] = 255;

        int width = Math.Min(480, sourceWidth);
        int height = Math.Max(1, sourceHeight * width / sourceWidth);
        byte[] reduced = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int sourceY = sourceHeight - 1 - y * sourceHeight / height;
            for (int x = 0; x < width; x++)
            {
                int sourceX = x * sourceWidth / width;
                int source = (sourceY * sourceWidth + sourceX) * 4;
                int target = (y * width + x) * 4;
                System.Buffer.BlockCopy(bottomUp, source, reduced, target, 4);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PortraitRenderTarget.SaveRgbaPng(path, width, height, reduced);
        return true;
    }
}
