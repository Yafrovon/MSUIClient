using System.Numerics;
using System.Reflection;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Player;
using MSUIClient.World;
using MSUIClient.World.Collision;

if (args.Contains("--onyxia-knockback")) return ProbeOnyxiaKnockback();
if (args.Contains("--onyxia-wall-replay")) return ProbeOnyxiaKnockback(true);
if (args.Contains("--onyxia-impulse-replay")) return ProbeOnyxiaKnockback(false,true);

TerrainRenderer terrain = CreateTerrain(height: 100f);
CollisionWorld collision = CreateFloor(height: 10f);

VerifyTeleportLanding(terrain, collision);
VerifyFlightExitLanding(terrain, collision);
VerifyInteriorJumpDoesNotSelectMountain(CreateTerrain(height: 100f));
VerifyOverheadTerrainExpandsSupportProbe(CreateTerrain(height: 100f));
VerifyFloorEdgeKeepsFootprintSupport();
VerifySunkenSupportIsRecovered();
VerifyChestHighWallBlocksTheSweep();
VerifyAtomicLowStepClimbs();
VerifyStaircaseClimbs();
VerifyBeveledKerbAndStairsClimb();
VerifyRampStillWalkable();
VerifyWalkableAdtTerrainClimbs();
VerifySteepAdtTerrainBlocksWalking();
VerifySteepAdtTerrainDoesNotBankJumps();
VerifyWmoFloorCrossesSteepCaveLip();
VerifyDistantCollisionFloorDoesNotOpenHillside();
VerifyOverheadSteepTerrainDoesNotWallInterior();
VerifyImpassableMcnkIsOneWay();
VerifyContinuousInteriorEntryRetainsTerrainShell(CreateTerrain(height: 100f));
VerifyHoleWithNothingUnderItHoldsTheBody();
VerifyHoleOverACellarStillDropsIntoIt();
VerifyShellWithNothingBelowRecoversToTheSurface();
VerifyGlobalWmoFall(CreateEmptyTerrain(), collision);
VerifyWalkableTriangleGather();
VerifyCameraTerrainShellClassification();
VerifyFlyFloorClearanceSpareInterior(CreateTerrain(height: 100f));
VerifyFlyFloorClearanceStillLiftsOutdoors(CreateTerrain(height: 100f));
VerifySwimCannotDiveThroughTerrain();
VerifySwimCannotDiveThroughCollisionFloor();
VerifySwimCannotRiseThroughCeiling();
VerifySwimCannotCrossWall();
VerifySwimDoesNotSnapToOverheadDeck();
VerifyShallowFloorBeatsSwimRestLine();
VerifyIdleSwimmerDoesNotSeekRestLine();
VerifySwimResamplesLandingWaterline();
VerifySwimUsesDisplayCollisionHeight();
VerifySpaceAscendsWhileDeep();
VerifySpaceBreachesOnlyAtSurface();
VerifySwimHeadCannotCrossSlantedUnderside();
VerifySwimSteepFaceIsNeverAFloorLift();
VerifySwimmerUnderOpenGroundIsLifted();
VerifySwimmerKeepsRoofedInteriorBelowTerrain();

VerifySlowFrameMovement();
VerifySlowFrameWall();
VerifyKnockbackSlidesDownSteepGeometry();

Console.WriteLine("character-controller clinical checks passed");
return 0;

static void VerifySwimCannotDiveThroughTerrain()
{
    TerrainRenderer terrain = CreateTerrain(5f);
    Require(terrain.TrySampleMovementSurface(-1f, -1f, out TerrainSurfaceSample sample, out _),
        "terrain-dive setup has no ADT sample");
    Require(MathF.Abs(sample.Height - 5f) < 0.001f,
        $"terrain-dive setup sampled unexpected Z={sample.Height:F3}");
    CharacterController controller = CreateController(terrain, new CollisionWorld());
    controller.Teleport(-1f, -1f, 5.08f);
    controller.LiquidSurfaceZ = 15f;
    controller.Update(1f / 20f,
        new MovementInput { Forward = 1f, Pitch = -1.45f });

    Require(controller.Swimming, "terrain-dive setup did not enter swimming");
    Require(controller.Position.Z >= 4.999f,
        $"swim stroke crossed ADT ground to Z={controller.Position.Z:F3}");
}

static void VerifySwimCannotDiveThroughCollisionFloor()
{
    CharacterController controller = CreateController(CreateEmptyTerrain(), CreateFloor(0f));
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 0.08f);
    controller.LiquidSurfaceZ = 10f;
    controller.Update(1f / 20f,
        new MovementInput { Forward = 1f, Pitch = -1.45f });

    Require(controller.Position.Z >= -0.001f,
        $"swim stroke crossed WMO floor to Z={controller.Position.Z:F3}");
    Require(controller.Grounded && controller.GroundSource == "collision",
        $"swim floor contact was not retained ({controller.GroundSource})");
}

static void VerifySwimCannotRiseThroughCeiling()
{
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 3f);
    collision.Build();
    CharacterController controller = CreateController(CreateEmptyTerrain(), collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 0.91f); // configured 2.1-yd body is touching Z=3
    controller.LiquidSurfaceZ = 20f;
    controller.Update(1f / 20f,
        new MovementInput { Forward = 1f, Pitch = 1.45f });

    Require(controller.Position.Z <= 0.911f,
        $"swim stroke crossed WMO ceiling to Z={controller.Position.Z:F3}");
}

static void VerifySwimCannotCrossWall()
{
    var collision = new CollisionWorld();
    AddWallAtX(collision, 0f, -5f, 5f, -5f, 10f);
    collision.Build();
    CharacterController controller = CreateController(CreateEmptyTerrain(), collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(-1f, 0f, 1f);
    controller.LiquidSurfaceZ = 10f;
    var forward = new MovementInput { Forward = 1f, Yaw = 0f };

    for (int i = 0; i < 30; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X <= -0.399f,
        $"swimmer crossed WMO wall to X={controller.Position.X:F3}");
}

static void VerifySwimDoesNotSnapToOverheadDeck()
{
    CharacterController controller = CreateController(CreateEmptyTerrain(), CreateFloor(1.8f));
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 1f);
    controller.LiquidSurfaceZ = 10f;

    controller.Update(1f / 60f, new MovementInput { Forward = 1f });

    Require(controller.Position.Z < 1.2f,
        $"swimmer snapped upward onto an overhead deck at Z={controller.Position.Z:F3}");
}

static void VerifyShallowFloorBeatsSwimRestLine()
{
    var collision = new CollisionWorld();
    AddFloor(collision, -2f, 0f, -2f, 2f, 1f);
    AddFloor(collision, 0f, 2f, -2f, 2f, 1.08f);
    collision.Build();
    CharacterController controller = CreateController(CreateEmptyTerrain(), collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(-0.01f, 0f, 1f);
    controller.LiquidSurfaceZ = 2.6f;
    controller.LiquidSurfaceProbe = _ => 2.6f;

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    controller.Update(1f / 60f, forward);
    Require(controller.Position.Z >= 1.079f,
        $"rest-line correction overwrote the shallow floor at Z={controller.Position.Z:F3}");
    controller.Update(1f / 60f, forward);
    Require(!controller.Swimming,
        "shallow floor did not lift feet through the swim exit depth");
}

static void VerifyIdleSwimmerDoesNotSeekRestLine()
{
    CharacterController controller = CreateController(CreateEmptyTerrain(), new CollisionWorld());
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 1.045f);
    controller.LiquidSurfaceZ = 2.7f;
    controller.Update(1f / 60f, default);
    controller.LiquidSurfaceZ = 2.6f; // inside the 1/36-yd exit hysteresis band

    controller.Update(1f / 60f, default);

    Require(controller.Swimming, "idle-float setup left the swim hysteresis band");
    Require(MathF.Abs(controller.Position.Z - 1.045f) < 0.0001f,
        $"idle swimmer sought the rest line at Z={controller.Position.Z:F3}");
}

static void VerifySwimResamplesLandingWaterline()
{
    CharacterController controller = CreateController(CreateEmptyTerrain(), new CollisionWorld());
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 1.42f); // just below the entry surface's rest line
    controller.LiquidSurfaceZ = 3f;
    controller.LiquidSurfaceProbe = _ => 2.8f;

    controller.Update(1f / 60f, new MovementInput { Forward = 1f });

    Require(MathF.Abs(controller.Position.Z - 1.225f) < 0.001f,
        $"swim used the stale entry waterline, ending at Z={controller.Position.Z:F3}");
}

static void VerifySwimUsesDisplayCollisionHeight()
{
    CharacterController controller = CreateController(CreateTerrain(5f), new CollisionWorld());
    controller.CollisionHeight = 1.15f;
    controller.Teleport(-1f, -1f, 5f);
    controller.LiquidSurfaceZ = 5.9f;
    controller.Update(1f / 60f, default);

    Require(controller.Swimming,
        "small display used the global human collision height for swim entry");
}

static void VerifySpaceAscendsWhileDeep()
{
    CharacterController controller = CreateController(CreateEmptyTerrain(), new CollisionWorld());
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 1f);
    controller.LiquidSurfaceZ = 10f;

    controller.Update(1f / 20f, new MovementInput { Up = 1f, Jump = true });

    Require(controller.Swimming, "deep Space incorrectly launched a breach jump");
    Require(controller.Position.Z > 1.2f,
        $"deep Space did not swim upward (Z={controller.Position.Z:F3})");
}

static void VerifySpaceBreachesOnlyAtSurface()
{
    CharacterController controller = CreateController(CreateEmptyTerrain(), new CollisionWorld());
    controller.TerrainAbsentByDesign = true;
    float restLine = SwimmingMovementLaw.RestLine(10f, controller.CollisionHeight);
    controller.Teleport(0f, 0f, restLine - 0.1f);
    controller.LiquidSurfaceZ = 10f;
    controller.Update(1f / 60f, default);
    Require(controller.Swimming, "surface-breach setup did not enter swimming");
    controller.Position = new Vector3(0f, 0f, restLine);

    controller.Update(1f / 60f, new MovementInput { Up = 1f, Jump = true });

    Require(!controller.Swimming && controller.Velocity.Z > 0f,
        "surface Space did not launch the swim breach");
}

// THE DUROTAR SHIPWRECK (reported 2026-09-03). The walking sweep only stops at STEEP faces and
// leaves everything flatter to the ground resolver - which a swimmer does not have. So the hull's
// slanted underside was crossed sideways at head height, and a downward footprint probe then read
// the hull's wall as a "floor" and lifted the body onto it; walking's ground snap finished the job
// by yanking the swimmer 0.6 yd up through the deck. A swimmer must collide-and-slide against
// EVERY surface, and only a walkable one may ever hold or lift the feet.
static void VerifySwimHeadCannotCrossSlantedUnderside()
{
    // Water 10 deep over a flat bottom at 0; a hull underside sloping from 2.6 at x=0 down to
    // 0.6 at x=4 - a 2.1-tall body at feet 0.4 clears it at x=0 and meets it just past.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 0f);
    AddSlopeAtX(collision, 0f, 4f, -10f, 10f, 2.6f, 0.6f);
    collision.Build();
    CharacterController controller = CreateController(CreateEmptyTerrain(), collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(-1f, 0f, 0.4f);
    controller.LiquidSurfaceZ = 10f;
    controller.LiquidSurfaceProbe = _ => 10f;

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    Vector3 previous = controller.Position;
    for (int i = 0; i < 180; i++)
    {
        controller.Update(1f / 60f, forward);
        RequireNoTunnel(collision, previous, controller.Position, 2.1f, $"underside frame {i}");
        float headZ = controller.Position.Z + 2.1f;
        float undersideZ = 2.6f - 0.5f * controller.Position.X;
        Require(controller.Position.X <= 0f || headZ <= undersideZ + 0.05f,
            $"head crossed the hull underside at frame {i}: head {headZ:F2} vs underside {undersideZ:F2} " +
            $"at X={controller.Position.X:F2}");
        previous = controller.Position;
    }
    Require(controller.Swimming, "the underside test left swimming");
    Require(controller.Position.X > 0.3f,
        $"the swimmer did not slide along the underside at all (X={controller.Position.X:F2})");
    Require(controller.Position.Z < 0.4f - 0.05f,
        $"sliding down the underside did not push the body down (Z={controller.Position.Z:F2})");
}

static void VerifySwimSteepFaceIsNeverAFloorLift()
{
    // A 63-degree hull face rising from z=1 at x=0 to z=3 at x=1: far too steep to stand on. The
    // old vertical probe accepted it as a floor within its rise band and lifted the feet onto it.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 0f, -10f, 10f, 1f);
    AddSlopeAtX(collision, 0f, 1f, -10f, 10f, 1f, 3f);
    collision.Build();
    CharacterController controller = CreateController(CreateEmptyTerrain(), collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(-1f, 0f, 1f);
    controller.LiquidSurfaceZ = 10f;
    controller.LiquidSurfaceProbe = _ => 10f;

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    Vector3 previous = controller.Position;
    float largestRise = 0f;
    for (int i = 0; i < 120; i++)
    {
        controller.Update(1f / 60f, forward);
        RequireNoTunnel(collision, previous, controller.Position, 2.1f, $"steep-face frame {i}");
        largestRise = MathF.Max(largestRise, controller.Position.Z - previous.Z);
        previous = controller.Position;
    }
    // A stroke is 0.079 yd per frame; any single-frame rise beyond that is a lift, not a slide.
    Require(largestRise <= 0.08f,
        $"a steep face lifted the swimmer {largestRise:F3} in one frame");
    // With no gravity in water the body SLIDES up the face and over its top, as the reference
    // capsule does; what it may never do is come out the far side below that top.
    Require(controller.Position.X < 1f || controller.Position.Z >= 3f - 0.05f,
        $"the swimmer passed through the steep face to X={controller.Position.X:F2} at Z={controller.Position.Z:F2}");
}

static void VerifySwimmerUnderOpenGroundIsLifted()
{
    // The seabed is at 5. A roof (a sunken hull's deck) at 4.5 covers x < 2 only; the body
    // starts under it at 2 - the 2.1-tall body fits under the roof, well below the shell - a
    // legitimate interior below the outdoor shell - and swims out.
    // Past the roof nothing is over the body but the height field: it must be lifted onto
    // the seabed, not left swimming under open ground.
    // (The synthetic height grid only covers x <= 0, y <= 0: everything here stays negative.)
    var collision = new CollisionWorld();
    AddFloor(collision, -20f, -6f, -10f, 10f, 4.5f);
    AddFloor(collision, -20f, 10f, -10f, 10f, 0f);
    collision.Build();
    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-9f, -1f, 2f);
    controller.LiquidSurfaceZ = 20f;
    controller.LiquidSurfaceProbe = _ => 20f;

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    controller.Update(1f / 60f, forward);
    Require(controller.Swimming, "under-ground setup did not enter swimming");
    Require(controller.UnderTerrainShell,
        "a roofed body below the height field was not recognised as under the shell");
    Require(MathF.Abs(controller.Position.Z - 2f) < 0.05f,
        $"the roofed interior was clamped to the seabed at Z={controller.Position.Z:F2}");
    for (int i = 0; i < 90; i++) controller.Update(1f / 60f, forward);
    Require(controller.Position.X > -5.5f,
        $"the swimmer did not leave the roof (X={controller.Position.X:F2})");
    Require(!controller.UnderTerrainShell,
        "leaving the roof did not release the under-shell fact");
    Require(controller.Position.Z >= 4.99f,
        $"a body under OPEN ground was left there at Z={controller.Position.Z:F2}; it must be lifted onto the seabed");
}

static void VerifySwimmerKeepsRoofedInteriorBelowTerrain()
{
    // The other half: a flooded cave under the mountain. Terrain shell at 100, roof at 40,
    // floor at 10, water to 30. The swimmer must stay in the cave, not be lifted to the shell.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 10f);
    AddFloor(collision, -10f, 10f, -10f, 10f, 40f);
    collision.Build();
    CharacterController controller = CreateController(CreateTerrain(height: 100f), collision);
    controller.Teleport(-3f, -1f, 20f);
    controller.LiquidSurfaceZ = 30f;
    controller.LiquidSurfaceProbe = _ => 30f;
    var forward = new MovementInput { Forward = 1f, Yaw = MathF.PI };
    for (int i = 0; i < 60; i++) controller.Update(1f / 60f, forward);
    Require(controller.TerrainGroundZ is not null, "the cave scenario walked off the synthetic height grid");
    Require(controller.Swimming && controller.Position.Z < 30f,
        $"the flooded cave swimmer was lifted to Z={controller.Position.Z:F2}");
    Require(controller.UnderTerrainShell, "the cave roof was not recognised as the shell");
}

/// <summary>The independent detector the live probe uses: did the committed displacement cross
/// any triangle at the feet, mid-body or head band?</summary>
static void RequireNoTunnel(CollisionWorld collision, Vector3 from, Vector3 to, float height, string where)
{
    Vector3 delta = to - from;
    float length = delta.Length();
    if (length < 1e-4f) return;
    foreach (float band in new[] { 0.15f, height * 0.5f, height - 0.15f })
    {
        RayHit? hit = collision.Raycast(from + new Vector3(0f, 0f, band), delta, length);
        Require(hit is null,
            $"{where}: the body crossed a surface at band {band:F2} (normal " +
            $"{hit?.Normal.X:F2},{hit?.Normal.Y:F2},{hit?.Normal.Z:F2}) moving from " +
            $"({from.X:F2},{from.Y:F2},{from.Z:F2}) to ({to.X:F2},{to.Y:F2},{to.Z:F2})");
    }
}

// IRONFORGE. The free view sets FlyFloorClearance so an RTS camera cannot sink beneath the map,
// but the clamp read the outdoor ADT height field as "the floor" - and every great interior
// (Ironforge, Undercity, Blackrock, the Deeprun Tram, any cave) sits BELOW it. Raising the free
// view inside one teleported the rig from the city floor onto the mountain surface overhead.
// Reported 2026-08-26.
static void VerifyFlyFloorClearanceSpareInterior(TerrainRenderer terrain)
{
    // A room under the mountain: floor at 10, ceiling at 40, terrain shell at 100.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 10f);
    AddFloor(collision, -10f, 10f, -10f, 10f, 40f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);

    // Walk in the ordinary way, so the shell is established exactly as it is in play: land
    // outdoors first, then cross into the interior.
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    controller.Position = new Vector3(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.GroundSource == "collision",
        $"interior setup selected {controller.GroundSource}, expected collision");

    // Ctrl+F: the free view raises the rig where it stands and clamps it off the floor.
    controller.Flying = true;
    controller.FlyFloorClearance = 2f;
    controller.Update(1f / 60f, default);

    Require(controller.Position.Z < 50f,
        $"the fly floor clamp lifted the free view onto the terrain shell at Z=" +
        $"{controller.Position.Z:F3}; it must stay in the room (was 10, shell is 100)");

    // And it must still be inert as the rig flies around inside the room.
    for (int frame = 0; frame < 30; frame++) controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 50f,
        $"the fly floor clamp lifted the free view onto the terrain shell after " +
        $"{30} frames at Z={controller.Position.Z:F3}");
}

// The clamp must not be defeated wholesale - outdoors, with nothing overhead, sinking under the
// height field is still the jank FlyFloorClearance exists to prevent.
static void VerifyFlyFloorClearanceStillLiftsOutdoors(TerrainRenderer terrain)
{
    CharacterController controller = CreateController(terrain, new CollisionWorld());
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);

    controller.Flying = true;
    controller.FlyFloorClearance = 2f;
    controller.Position = new Vector3(0f, 0f, 60f);   // sunk under the map, no roof above
    controller.Update(1f / 60f, default);

    Require(MathF.Abs(controller.Position.Z - 102f) < 0.001f,
        $"the fly floor clamp stopped lifting an unroofed rig back to the surface: Z=" +
        $"{controller.Position.Z:F3}, expected 102");
}

static void VerifyTeleportLanding(TerrainRenderer terrain, CollisionWorld collision)
{
    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 20f);

    controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 20f,
        $"teleport snapped onto overhead terrain at Z={controller.Position.Z:F3}");
    Require(controller.GroundSource == "collision",
        $"teleport selected {controller.GroundSource}, expected collision");

    Land(controller);
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"teleport landed at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyFlightExitLanding(TerrainRenderer terrain, CollisionWorld collision)
{
    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "setup did not settle on outdoor terrain");

    controller.Flying = true;
    controller.Position = new Vector3(0f, 0f, 20f);
    controller.Flying = false;

    controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 20f,
        $"flight exit snapped onto overhead terrain at Z={controller.Position.Z:F3}");
    Require(controller.GroundSource == "collision",
        $"flight exit selected {controller.GroundSource}, expected collision");

    Land(controller);
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"flight exit landed at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyInteriorJumpDoesNotSelectMountain(TerrainRenderer terrain)
{
    // Model the Ironforge failure: an upper WMO support ends beneath a prop,
    // the next WMO floor is more than the normal five-yard probe below it, and
    // the outdoor mountain ADT is far overhead.  Losing the upper support while
    // airborne must produce a fall to the lower WMO floor, never a 90-yard snap
    // to the outdoor height field.
    var collision = new CollisionWorld();
    AddFloor(collision, -0.2f, 10f, -10f, 10f, 10f);
    AddFloor(collision, -20f, 10f, -10f, 10f, 0f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "collision",
        "interior-jump setup did not settle on the upper WMO floor");

    var jump = new MovementInput { Forward = -1f, Yaw = 0f, Jump = true };
    controller.Update(1f / 60f, jump);

    float highest = controller.Position.Z;
    for (int i = 0; i < 600 && !controller.Grounded; i++)
    {
        // Keep moving only long enough to clear the upper support.  Remaining
        // still afterward makes the expected lower-floor landing deterministic.
        var input = i < 30
            ? new MovementInput { Forward = -1f, Yaw = 0f }
            : default;
        controller.Update(1f / 60f, input);
        highest = MathF.Max(highest, controller.Position.Z);
    }

    Require(highest < 20f,
        $"interior jump selected overhead terrain, reaching Z={highest:F3}");
    Require(controller.Grounded,
        "interior jump did not reach the deeper WMO floor within 10 seconds");
    Require(MathF.Abs(controller.Position.Z) < 0.001f,
        $"interior jump landed at Z={controller.Position.Z:F3}, expected lower WMO floor at 0");
    Require(controller.GroundSource == "collision",
        $"interior jump selected {controller.GroundSource}, expected collision");
}

static void VerifyOverheadTerrainExpandsSupportProbe(TerrainRenderer terrain)
{
    // The centre is just outside the floor but the left-hand capsule footprint
    // still overlaps it.  Overhead terrain must not count as "nearby" merely
    // because (feet - terrain) is a large negative number and suppress the
    // eight rescue probes.
    var collision = new CollisionWorld();
    AddFloor(collision, 0.15f, 10f, -10f, 10f, 10f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    // First settle outdoors to clear Teleport's terrain-shell exception. The
    // next update must discover the interior only through the ordinary support
    // selection path, which directly exercises terrainNearby.
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "terrain",
        "footprint-probe setup did not clear teleport state outdoors");

    controller.Position = new Vector3(-0.1f, 0f, 10f);
    controller.Update(1f / 60f, default);

    Require(controller.TerrainGroundZ is float terrainZ && terrainZ > 90f,
        "overhead-terrain probe regression did not actually sample its terrain shell");
    Require(controller.Grounded,
        "overhead terrain suppressed footprint support probes");
    Require(controller.GroundSource == "collision",
        $"footprint support selected {controller.GroundSource}, expected collision");
    Require(controller.GroundProbesLastFrame == 9,
        $"footprint support used {controller.GroundProbesLastFrame} probes, expected expansion to 9");
    Require(controller.GroundProbeOffset.X > 0f,
        "footprint support did not come from the overlapping right probe");
}

static void VerifyFloorEdgeKeepsFootprintSupport()
{
    // Model the Stormwind auction house edge: an upper deck, and just past its
    // lip a plank a few tenths lower - inside GroundSnapDistance.
    //
    // Walk the capsule CENTRE a hand's breadth past the lip while most of the
    // footprint is still over the deck. The centre ray now reports the lower
    // plank, and the old gate read "something is nearby below, one ray is
    // enough" and suppressed the footprint fan at exactly the moment it was
    // load-bearing. Support collapsed to a single ray, adhesion pulled the
    // character down onto the plank, and repeating that a few frames walked it
    // below the deck entirely and out through the floor.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 0f, -10f, 10f, 10f);      // deck
    AddFloor(collision, 0f, 10f, -10f, 10f, 9.7f);      // plank just past the lip
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-1f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"floor-edge setup did not settle on the deck, Z={controller.Position.Z:F3}");

    // Centre 0.1 past the lip; the footprint reaches 0.34, so the deck still
    // carries the character.
    controller.Position = new Vector3(0.1f, 0f, 10f);
    controller.Update(1f / 60f, default);

    Require(controller.GroundProbesLastFrame == 9,
        $"floor edge used {controller.GroundProbesLastFrame} support probes, " +
        "expected the footprint fan to expand to 9");
    Require(controller.Grounded,
        "floor edge lost support entirely");
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"floor edge dropped to Z={controller.Position.Z:F3}; the deck under the " +
        "footprint should still hold the character at 10");
    Require(controller.GroundProbeOffset.X < 0f,
        "floor-edge support did not come from the probe still over the deck");
}

static void VerifySunkenSupportIsRecovered()
{
    // Whatever puts the feet below a floor - a step-up into a solid, a stair
    // lip miss, an adhesion frame that guessed low - must not be permanent.
    // A fixed StepHeight probe lift cannot see a surface 1.1 above the feet, so
    // the floor the character was standing on last frame becomes invisible and
    // it falls out of a floor that is still there.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 10f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "collision",
        "sunken-support setup did not settle on the WMO floor");

    // Sink 1.1 - past StepHeight - while still horizontally over the floor.
    controller.Position = new Vector3(0f, 0f, 8.9f);
    controller.Update(1f / 60f, default);

    Require(controller.Grounded,
        $"sunken character fell instead of recovering, vz={controller.Velocity.Z:F3}");
    Require(controller.GroundSource == "collision",
        $"sunken recovery selected {controller.GroundSource}, expected collision");
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"sunken recovery left the character at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyChestHighWallBlocksTheSweep()
{
    // A rim 0.9 tall at the edge of a deck, with nothing beyond it. Taller than
    // It has no walkable top beyond it, so it must stop the character - but its
    // top is below the mid-body height the sweep used to be a single ray at, so
    // that ray passed clean over it and reported open air. The character walked
    // through a face the debug view draws in wall red and fell off the far side.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 0f, -10f, 10f, 10f);
    AddWallAtX(collision, 0f, -10f, 10f, 10f, 10.9f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-2f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "chest-high wall setup did not settle on the deck");

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 120; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X < 0f,
        $"sweep walked through the rim to X={controller.Position.X:F3}; it must stop before 0");
    Require(controller.Grounded,
        $"sweep left the deck entirely, Z={controller.Position.Z:F3}");
    Require(controller.HasBlock,
        "the rim never registered as a block, so the sweep never saw it");
}

static void VerifyAtomicLowStepClimbs()
{
    // Current Benilla's maneuver looks only this frame's travel for the face,
    // then advances a fixed body-scale distance to find the tread. A 0.3-yard
    // curb therefore commits in one certified move at both 60 and 240 fps.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 0f, -4f, 4f, 10f);
    AddWallAtX(collision, 0f, -4f, 4f, 10f, 10.3f);
    AddFloor(collision, 0f, 10f, -4f, 4f, 10.3f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-2f, 0f, 10f);
    controller.Update(1f / 60f, default);
    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 60; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X > 2f,
        $"atomic step stalled at X={controller.Position.X:F3}");
    Require(controller.Grounded && MathF.Abs(controller.Position.Z - 10.3f) < 0.01f,
        $"atomic step landed at Z={controller.Position.Z:F3}, expected 10.3");

    var pinched = new CollisionWorld();
    AddFloor(pinched, -10f, 0f, -4f, 4f, 10f);
    AddWallAtX(pinched, 0f, -4f, 4f, 10f, 10.3f);
    AddFloor(pinched, 0f, 10f, -4f, 4f, 10.3f);
    AddFloor(pinched, 0f, 2f, -4f, 4f, 12.2f); // too low over the landing footprint
    pinched.Build();

    CharacterController blocked = CreateController(CreateTerrain(height: 5f), pinched);
    blocked.Teleport(-2f, 0f, 10f);
    blocked.Update(1f / 60f, default);
    for (int i = 0; i < 60; i++) blocked.Update(1f / 60f, forward);
    Require(blocked.Position.X < 0f && MathF.Abs(blocked.Position.Z - 10f) < 0.01f,
        $"atomic step committed without landing headroom at {blocked.Position}");
}

// STORMWIND STAIRS. Reported 2026-09-01: the short flights in front of the Trade District
// shops (four 0.45 yd risers on 0.6 yd treads) could not be walked; the player had to jump.
// One curb climbs (VerifyAtomicLowStepClimbs), a FLIGHT must too.
static void VerifyStaircaseClimbs()
{
    var collision = new CollisionWorld();
    const float rise = 0.45f, tread = 0.35f;
    AddFloor(collision, -10f, 0f, -4f, 4f, 10f);
    for (int i = 0; i < 4; i++)
    {
        float x = i * tread, z = 10f + i * rise;
        AddWallAtX(collision, x, -4f, 4f, z, z + rise);
        AddFloor(collision, x, i == 3 ? 40f : x + tread, -4f, 4f, z + rise);
    }
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-2f, 0f, 10f);
    controller.Update(1f / 60f, default);
    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 90; i++) controller.Update(1f / 60f, forward);
    Require(controller.Position.X > 3f,
        $"staircase stalled at X={controller.Position.X:F3} Z={controller.Position.Z:F3}");
    Require(controller.Grounded && MathF.Abs(controller.Position.Z - 11.8f) < 0.02f,
        $"staircase landed at Z={controller.Position.Z:F3}, expected 11.8");
}

// The kerb benilla profiled in the Trade District (decision 1121): a 0.28 yd sidewalk whose
// riser is a ~61 degree BEVEL (face normal z=+0.49), steeper than the 50 degree walk limit, so
// it reads as a wall and must be stepped; then a flight of the same bevelled risers.
static void VerifyBeveledKerbAndStairsClimb()
{
    var kerb = new CollisionWorld();
    AddFloor(kerb, -10f, 0.29f, -4f, 4f, 10f);
    AddSlopeAtX(kerb, 0.29f, 0.446f, -4f, 4f, 10f, 10.28f);
    AddFloor(kerb, 0.446f, 40f, -4f, 4f, 10.28f);
    kerb.Build();
    CharacterController c = CreateController(CreateTerrain(height: 5f), kerb);
    c.Teleport(-1f, 0f, 10f);
    c.Update(1f / 60f, default);
    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 60; i++) c.Update(1f / 60f, forward);
    Require(c.Position.X > 2f && MathF.Abs(c.Position.Z - 10.28f) < 0.02f,
        $"bevelled kerb stalled at {c.Position}");

    var stairs = new CollisionWorld();
    const float rise = 0.45f, tread = 0.35f, bevel = 0.15f;
    AddFloor(stairs, -10f, 0f, -4f, 4f, 10f);
    for (int i = 0; i < 4; i++)
    {
        float x = i * tread, z = 10f + i * rise;
        AddSlopeAtX(stairs, x, x + bevel, -4f, 4f, z, z + rise);
        AddFloor(stairs, x + bevel, i == 3 ? 40f : x + tread, -4f, 4f, z + rise);
    }
    stairs.Build();
    CharacterController d = CreateController(CreateTerrain(height: 5f), stairs);
    d.Teleport(-1f, 0f, 10f);
    d.Update(1f / 60f, default);
    for (int i = 0; i < 120; i++) d.Update(1f / 60f, forward);
    Require(d.Position.X > 3f && MathF.Abs(d.Position.Z - 11.8f) < 0.02f,
        $"bevelled staircase stalled at {d.Position}");
}
static void VerifyRampStillWalkable()
{
    // The counterpart guard: the extra sample heights must not turn a walkable
    // slope into a wall. A 30 degree ramp is inside the 50 degree limit, so the
    // character has to climb it, not stall against it.
    var collision = new CollisionWorld();
    AddFloor(collision, -12f, -6f, -6f, 6f, 10f);
    // Ramp rising from Z=10 at X=-6 to Z=12.31 at X=-2 (about 30 degrees).
    collision.AddTriangle(new Vector3(-6f, -6f, 10f),
        new Vector3(-2f, -6f, 12.31f), new Vector3(-2f, 6f, 12.31f));
    collision.AddTriangle(new Vector3(-6f, -6f, 10f),
        new Vector3(-2f, 6f, 12.31f), new Vector3(-6f, 6f, 10f));
    AddFloor(collision, -2f, 6f, -6f, 6f, 12.31f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-9f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "ramp setup did not settle on the lower floor");

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 90; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X > -2f,
        $"ramp stalled the sweep at X={controller.Position.X:F3}; expected to climb past -2");
    Require(controller.Position.Z > 12.2f,
        $"ramp climb ended at Z={controller.Position.Z:F3}, expected the upper floor at 12.31");
}

static void VerifyWalkableAdtTerrainClimbs()
{
    // ADT used to be sampled as a height only, so MaxSlopeDegrees was never
    // involved. A genuine 40-degree fan must remain ordinary walkable ground.
    TerrainRenderer terrain = CreateTerrainGrid((worldX, _) =>
        10f + MathF.Max(0f, worldX + 25f) * MathF.Tan(40f * MathF.PI / 180f));
    CharacterController controller = CreateController(terrain, new CollisionWorld());
    controller.Teleport(-30f, -10f, 10f);
    controller.Update(1f / 60f, default);

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 150; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X > -16f,
        $"walkable ADT slope stalled at X={controller.Position.X:F3}");
    Require(controller.Position.Z > 17f,
        $"walkable ADT slope did not climb, ending at Z={controller.Position.Z:F3}");
    Require(controller.Grounded && !controller.TerrainGroundSteep,
        "40-degree ADT face was classified as unwalkable");
}

static void VerifySteepAdtTerrainBlocksWalking()
{
    TerrainRenderer terrain = CreateTerrainGrid((worldX, _) =>
        10f + MathF.Max(0f, worldX + 25f) * MathF.Tan(55f * MathF.PI / 180f));
    CharacterController controller = CreateController(terrain, new CollisionWorld());
    controller.Teleport(-30f, -10f, 10f);
    controller.Update(1f / 60f, default);

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 180; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X < -23f,
        $"55-degree ADT face was walked up to X={controller.Position.X:F3}");
    Require(controller.Position.Z < 10.1f,
        $"55-degree ADT face manufactured elevation Z={controller.Position.Z:F3}");
    Require(controller.TerrainGroundSteep || controller.HasBlock,
        "55-degree ADT face never registered as a steep contact");
}

static void VerifySteepAdtTerrainDoesNotBankJumps()
{
    // This is the Elwynn regression in synthetic form. Repeated forward jumps
    // into a 55-degree terrain face must not turn horizontal plane projection
    // into cumulative vertical lift.
    TerrainRenderer terrain = CreateTerrainGrid((worldX, _) =>
        10f + MathF.Max(0f, worldX + 25f) * MathF.Tan(55f * MathF.PI / 180f));
    CharacterController controller = CreateController(terrain, new CollisionWorld());
    controller.Teleport(-27f, -10f, 10f);
    controller.Update(1f / 60f, default);

    float highestLanding = controller.Position.Z;
    for (int jump = 0; jump < 6; jump++)
    {
        controller.Update(1f / 60f,
            new MovementInput { Forward = 1f, Yaw = 0f, Jump = true });
        for (int frame = 0; frame < 240 && !controller.Grounded; frame++)
            controller.Update(1f / 60f,
                new MovementInput { Forward = 1f, Yaw = 0f });

        Require(controller.Grounded,
            $"steep-terrain jump {jump + 1} did not return to ground");
        highestLanding = MathF.Max(highestLanding, controller.Position.Z);
    }

    Require(highestLanding < 10.1f,
        $"repeated steep-terrain jumps banked landing height {highestLanding:F3}");
    Require(controller.Position.X < -23f,
        $"repeated steep-terrain jumps climbed through the face to X={controller.Position.X:F3}");
}

static void VerifyWmoFloorCrossesSteepCaveLip()
{
    // Real mountain-cave entrances cut the ADT away a few yards after the WMO
    // floor begins.  Before the controller has accumulated a full yard of
    // under-terrain separation, the last steep terrain fan at the lip must not
    // become an invisible wall across that continuing collision floor.
    TerrainRenderer terrain = CreateTerrainGrid((worldX, _) =>
        10f + MathF.Max(0f, worldX + 25f) * MathF.Tan(55f * MathF.PI / 180f));
    var collision = new CollisionWorld();
    AddFloor(collision, -40f, 0f, -20f, 0f, 10f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(-30f, -10f, 10f);
    controller.Update(1f / 60f, default);

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 150; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X > -16f,
        $"WMO-supported cave lip became an invisible terrain wall at X={controller.Position.X:F3}");
    Require(controller.Grounded && controller.GroundSource == "collision" &&
            MathF.Abs(controller.Position.Z - 10f) < 0.01f,
        $"cave-lip traversal left its WMO floor at {controller.Position} " +
        $"from {controller.GroundSource}");
}

static void VerifyDistantCollisionFloorDoesNotOpenHillside()
{
    TerrainRenderer terrain = CreateTerrainGrid((worldX, _) =>
        10f + MathF.Max(0f, worldX + 25f) * MathF.Tan(55f * MathF.PI / 180f));
    var collision = new CollisionWorld();
    AddFloor(collision, -40f, 0f, -20f, 0f, 0f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(-30f, -10f, 10f);
    controller.Update(1f / 60f, default);
    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 180; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X < -23f,
        $"distant collision floor incorrectly opened a steep hillside at X={controller.Position.X:F3}");
}

static void VerifyOverheadSteepTerrainDoesNotWallInterior()
{
    // Ironforge sits beneath the outdoor Dun Morogh terrain. Its mountain
    // contours are legitimately too steep to climb outdoors, but down here they
    // are a roof/shell tens of yards above the WMO floor, not circular walls.
    TerrainRenderer terrain = CreateTerrainGrid((worldX, _) =>
        100f + MathF.Max(0f, worldX + 25f) * MathF.Tan(55f * MathF.PI / 180f));
    var collision = new CollisionWorld();
    AddFloor(collision, -40f, 0f, -20f, 0f, 0f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(-30f, -10f, 0f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.UnderTerrainShell &&
            controller.GroundSource == "collision",
        "overhead-steep setup did not establish an interior WMO floor");

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 150; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X > -16f,
        $"overhead mountain contour became an invisible interior wall at " +
        $"X={controller.Position.X:F3}");
    Require(controller.Grounded && MathF.Abs(controller.Position.Z) < 0.01f,
        $"interior traversal left its WMO floor at {controller.Position}");
}

static void VerifyImpassableMcnkIsOneWay()
{
    Require(new MSUIClient.Formats.AdtTerrainReader.McnkChunk { Flags = 0x2 }.Impassable,
        "MCNK header bit 1 was not decoded as impassable");

    // Tile (32,32), chunk row 1 spans X -33.33 to -66.66. Vanilla's authored
    // fence rejects entry but intentionally lets a character already inside
    // escape, avoiding permanent traps after teleports or old saves.
    TerrainRenderer terrain = CreateTerrainGrid((_, _) => 10f, (0, 1));
    CharacterController outside = CreateController(terrain, new CollisionWorld());
    outside.Teleport(-30f, -10f, 10f);
    outside.Update(1f / 60f, default);

    var towardChunk = new MovementInput { Forward = 1f, Yaw = MathF.PI };
    for (int i = 0; i < 120; i++) outside.Update(1f / 60f, towardChunk);
    Require(outside.Position.X > -33.34f,
        $"entered authored impassable MCNK at X={outside.Position.X:F3}");
    Require(outside.HasBlock,
        "impassable MCNK boundary did not register as a block");

    // The literal fence rises from the chunk's lowest terrain vertex, not from
    // negative infinity. A WMO tunnel fully below that base must cross freely.
    var tunnelFloor = new CollisionWorld();
    AddFloor(tunnelFloor, -80f, 0f, -20f, 0f, 0f);
    tunnelFloor.Build();
    CharacterController below = CreateController(terrain, tunnelFloor);
    below.Teleport(-30f, -10f, 0f);
    below.Update(1f / 60f, default);
    for (int i = 0; i < 120; i++) below.Update(1f / 60f, towardChunk);
    Require(below.Position.X < -35f,
        $"impassable fence extended below its terrain base at X={below.Position.X:F3}");
    CharacterController inside = CreateController(terrain, new CollisionWorld());
    inside.Teleport(-40f, -10f, 10f);
    inside.Update(1f / 60f, default);
    var leaveChunk = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 120; i++) inside.Update(1f / 60f, leaveChunk);
    Require(inside.Position.X > -33f,
        $"one-way MCNK fence trapped an inside mover at X={inside.Position.X:F3}");
}
static void VerifyContinuousInteriorEntryRetainsTerrainShell(TerrainRenderer terrain)
{
    // Simulate already being inside without Teleport's discontinuity marker.
    // Flight keeps the authored Z but clears no grounding state; exiting flight
    // normally sets the same landing marker, so instead establish an outdoor
    // landing first, then move the controller into the interior to exercise the
    // regular under-terrain closer-surface clause.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 10f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "terrain",
        "continuous-entry setup did not clear teleport state on outdoor terrain");

    // Direct placement stands in for ordinary horizontal tunnel entry after
    // crossing the ADT shell. ResolveGround must establish WMO precedence from
    // the current heights alone, with no teleport exception remaining.
    controller.Position = new Vector3(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "collision",
        $"continuous interior entry selected {controller.GroundSource}, expected collision");

    // Remove support for one frame. The already-proven terrain shell must stay
    // excluded instead of lifting the controller onto it.
    controller.Collision = new CollisionWorld();
    controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 20f,
        $"continuous interior entry forgot its terrain shell and snapped to Z={controller.Position.Z:F3}");
    Require(!controller.Grounded && controller.Velocity.Z < 0f,
        "continuous interior entry froze instead of falling through a support gap");
}

// Hole void law (Gilneas 2026-09-27, proven live: stock mine/crypt/cave mouths leave a corner of their
// terrain-hole square with NOTHING under it; ten walk-offs fell out of the world). Quads 2..5 x 2..5 of
// tile (32,32) are holes = world x,y in (-25, -8.3]; a far-away floor keeps the collision world loaded.
static TerrainRenderer CreateTerrainWithHole(float height)
{
    TerrainRenderer terrain = CreateTerrain(height);
    FieldInfo holeField = typeof(TerrainRenderer).GetField("_holes", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TerrainRenderer._holes not found");
    var holes = (Dictionary<(int col, int row), byte[]>)holeField.GetValue(terrain)!;
    var grid = new byte[TerrainRenderer.QuadGridSide * TerrainRenderer.QuadGridSide];
    for (int r = 2; r <= 5; r++)
    for (int c = 2; c <= 5; c++)
        grid[r * TerrainRenderer.QuadGridSide + c] = 1;
    holes[(32, 32)] = grid;
    return terrain;
}

static CollisionWorld CreateFarFloor(float cellarHeight = float.NaN)
{
    var collision = new CollisionWorld();
    AddFloor(collision, -300f, -290f, -300f, -290f, 100f);                       // elsewhere: the world is loaded
    if (!float.IsNaN(cellarHeight)) AddFloor(collision, -30f, -4f, -30f, -4f, cellarHeight);
    collision.Build();
    return collision;
}

static void VerifyHoleWithNothingUnderItHoldsTheBody()
{
    CharacterController controller = CreateController(CreateTerrainWithHole(100f), CreateFarFloor());
    controller.Teleport(-16f, -16f, 106f);                                        // dropped onto the open hole
    for (int i = 0; i < 180; i++) controller.Update(1f / 60f, default);
    Require(controller.Position.Z > 99f && controller.GroundSource == "hole-void-guard",
        $"a terrain hole with nothing under it dropped the body to Z={controller.Position.Z:F1} ({controller.GroundSource})");

    controller.Teleport(-4f, -16f, 100f);                                         // on terrain, walk into it
    for (int i = 0; i < 30; i++) controller.Update(1f / 60f, default);
    var walk = new MovementInput { Forward = 1f, Yaw = MathF.PI };               // toward -x, into the hole
    for (int i = 0; i < 60; i++) controller.Update(1f / 60f, walk);
    for (int i = 0; i < 120; i++) controller.Update(1f / 60f, default);
    Require(controller.Position.X < -8.4f && controller.Position.Z > 99f,
        $"walking into a void hole corner ended at ({controller.Position.X:F1}, Z={controller.Position.Z:F1}) ({controller.GroundSource})");
}

static void VerifyHoleOverACellarStillDropsIntoIt()
{
    CharacterController controller = CreateController(CreateTerrainWithHole(100f), CreateFarFloor(cellarHeight: 80f));
    controller.Teleport(-16f, -16f, 100.5f);
    for (int i = 0; i < 240; i++) controller.Update(1f / 60f, default);
    Require(MathF.Abs(controller.Position.Z - 80f) < 0.5f && controller.Grounded,
        $"a hole over a cellar must drop the body onto the cellar floor, got Z={controller.Position.Z:F1} ({controller.GroundSource})");
}

static void VerifyShellWithNothingBelowRecoversToTheSurface()
{
    CharacterController controller = CreateController(CreateTerrain(100f), CreateFarFloor());
    controller.Teleport(-16f, -16f, 97f);                                         // under the height field: a "tunnel"
    for (int i = 0; i < 240; i++) controller.Update(1f / 60f, default);
    Require(MathF.Abs(controller.Position.Z - 100f) < 0.5f,
        $"under the terrain shell with nothing below, the body must return to the surface, got Z={controller.Position.Z:F1} ({controller.GroundSource})");
}

static void VerifyGlobalWmoFall(TerrainRenderer terrain, CollisionWorld collision)
{
    CharacterController controller = CreateController(terrain, collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "global-WMO setup did not settle on its WMO floor");

    // Simulate walking off an upper ledge after the teleport recovery flag has
    // already cleared. Missing terrain must permit a real fall toward the next
    // WMO floor rather than freezing the controller in empty height data.
    controller.Position = new Vector3(0f, 0f, 20f);
    controller.Update(1f / 60f, default);
    Require(!controller.Grounded && controller.Velocity.Z < 0f,
        "global-WMO map froze instead of falling without terrain");

    Land(controller);
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"global-WMO fall landed at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyWalkableTriangleGather()
{
    var collision = new CollisionWorld { Offset = new Vector3(3f, -2f, 1f) };
    // A sloped floor, a vertical wall in the same XY box, and a floor outside
    // the requested Z slab. Only the nearby floor may receive a ground decal.
    collision.AddTriangle(new Vector3(-2f, -2f, 0f),
        new Vector3(2f, -2f, 0f), new Vector3(2f, 2f, 1f));
    collision.AddTriangle(new Vector3(-2f, -2f, 0f),
        new Vector3(2f, 2f, 1f), new Vector3(-2f, 2f, 1f));
    collision.AddTriangle(new Vector3(0f, 0f, 0f),
        new Vector3(0f, 2f, 0f), new Vector3(0f, 2f, 3f));
    collision.AddTriangle(new Vector3(-2f, -2f, 20f),
        new Vector3(2f, -2f, 20f), new Vector3(2f, 2f, 20f));
    collision.Build();

    var gathered = new List<(Vector3 A, Vector3 B, Vector3 C)>();
    collision.GatherWalkableTriangles(0f, -5f, 0f, 6f, 1f, 4f, gathered);
    Require(gathered.Count == 2,
        $"walkable gather returned {gathered.Count} triangles instead of the two floor faces");
    Require(gathered.SelectMany(t => new[] { t.A, t.B, t.C }).All(v => v.Z is >= 1f and <= 2f),
        "walkable gather lost collision Offset or admitted the high floor");
}

static void VerifyCameraTerrainShellClassification()
{
    // Values captured from load-azeroth-301: the server-authored BRM interior
    // position is Z=166.09 and the outdoor terrain at that XY is Z=274.77.
    // EyeTarget adds 2.2 yards to the character position.
    Require(Camera.TerrainIsOverhead(168.29f, 274.77f, 1f),
        "Blackrock Mountain's outdoor terrain shell was admitted as camera ground");

    Require(!Camera.TerrainIsOverhead(168.29f, 166.09f, 1f),
        "ordinary outdoor ground was mistaken for an overhead terrain shell");
    Require(!Camera.TerrainIsOverhead(168.29f, null, 1f),
        "missing terrain was mistaken for an overhead terrain shell");
}

static CharacterController CreateController(TerrainRenderer terrain, CollisionWorld collision)
    => new(terrain, new ClientConfig.MovementConfig()) { Collision = collision };

static TerrainRenderer CreateTerrain(float height)
    => CreateTerrainGrid((_, _) => height);

static TerrainRenderer CreateTerrainGrid(
    Func<float, float, float> heightAt,
    params (int ChunkX, int ChunkY)[] impassableChunks)
{
    var terrain = new TerrainRenderer(null!, new ClientConfig(), null!, null!);

    FieldInfo heightField = typeof(TerrainRenderer).GetField("_heights",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TerrainRenderer._heights not found");
    var heights = (Dictionary<(int col, int row), float[]>)heightField.GetValue(terrain)!;
    var outer = new float[TerrainRenderer.HeightGridSide * TerrainRenderer.HeightGridSide];
    float cell = TerrainRenderer.GridSize / TerrainRenderer.QuadGridSide;
    for (int row = 0; row < TerrainRenderer.HeightGridSide; row++)
    for (int col = 0; col < TerrainRenderer.HeightGridSide; col++)
        outer[row * TerrainRenderer.HeightGridSide + col] =
            heightAt(-row * cell, -col * cell);
    heights[(32, 32)] = outer;

    FieldInfo innerField = typeof(TerrainRenderer).GetField("_innerHeights",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TerrainRenderer._innerHeights not found");
    var innerHeights =
        (Dictionary<(int col, int row), float[]>)innerField.GetValue(terrain)!;
    var inner = new float[TerrainRenderer.QuadGridSide * TerrainRenderer.QuadGridSide];
    for (int row = 0; row < TerrainRenderer.QuadGridSide; row++)
    for (int col = 0; col < TerrainRenderer.QuadGridSide; col++)
        inner[row * TerrainRenderer.QuadGridSide + col] =
            heightAt(-(row + 0.5f) * cell, -(col + 0.5f) * cell);
    innerHeights[(32, 32)] = inner;

    FieldInfo impassableField = typeof(TerrainRenderer).GetField("_impassableChunks",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TerrainRenderer._impassableChunks not found");
    var impassable =
        (Dictionary<(int col, int row), byte[]>)impassableField.GetValue(terrain)!;
    var chunkFlags = new byte[16 * 16];
    foreach ((int chunkX, int chunkY) in impassableChunks)
        chunkFlags[chunkY * 16 + chunkX] = 1;
    impassable[(32, 32)] = chunkFlags;

    return terrain;
}
static TerrainRenderer CreateEmptyTerrain()
    => new(null!, new ClientConfig(), null!, null!);

static CollisionWorld CreateFloor(float height)
{
    var collision = new CollisionWorld();
    collision.AddTriangle(new Vector3(-10f, -10f, height),
        new Vector3(10f, -10f, height), new Vector3(10f, 10f, height));
    collision.AddTriangle(new Vector3(-10f, -10f, height),
        new Vector3(10f, 10f, height), new Vector3(-10f, 10f, height));
    collision.Build();
    return collision;
}

static void AddFloor(CollisionWorld collision, float minX, float maxX,
    float minY, float maxY, float height)
{
    collision.AddTriangle(new Vector3(minX, minY, height),
        new Vector3(maxX, minY, height), new Vector3(maxX, maxY, height));
    collision.AddTriangle(new Vector3(minX, minY, height),
        new Vector3(maxX, maxY, height), new Vector3(minX, maxY, height));
}


static void AddSlopeAtX(CollisionWorld collision, float x0, float x1,
    float minY, float maxY, float z0, float z1)
{
    collision.AddTriangle(new Vector3(x0, minY, z0),
        new Vector3(x0, maxY, z0), new Vector3(x1, maxY, z1));
    collision.AddTriangle(new Vector3(x0, minY, z0),
        new Vector3(x1, maxY, z1), new Vector3(x1, minY, z1));
}
static void AddWallAtX(CollisionWorld collision, float x,
    float minY, float maxY, float minZ, float maxZ)
{
    collision.AddTriangle(new Vector3(x, minY, minZ),
        new Vector3(x, maxY, minZ), new Vector3(x, maxY, maxZ));
    collision.AddTriangle(new Vector3(x, minY, minZ),
        new Vector3(x, maxY, maxZ), new Vector3(x, minY, maxZ));
}

static void Land(CharacterController controller)
{
    for (int i = 0; i < 600 && !controller.Grounded; i++)
        controller.Update(1f / 60f, default);
    Require(controller.Grounded, "controller did not land within 10 seconds");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void VerifySlowFrameMovement()
{
    CharacterController Make() { var c = CreateController(CreateEmptyTerrain(), CreateFloor(10f)); c.TerrainAbsentByDesign = true; c.Teleport(-4f, 0f, 10f); c.Update(.05f, default); return c; }
    foreach (bool jump in new[] { false, true })
    {
        var slow = Make(); var regular = Make();
        var input = new MovementInput { Forward = 1f, Jump = jump };
        slow.Update(.3f, input);
        for (int i = 0; i < 6; i++) regular.Update(.05f, input);
        Require(Vector3.Distance(slow.Position, regular.Position) < .001f &&
            Vector3.Distance(slow.Velocity, regular.Velocity) < .001f,
            $"slow-frame {(jump ? "jump" : "run")} discarded elapsed time: {slow.Position} vs {regular.Position}");
        Require(slow.Position.X > -2f, "slow-frame run did not advance at ordinary speed");
    }
    var paused = Make(); var bounded = Make();
    paused.Update(10f, new MovementInput { Forward = 1f });
    bounded.Update(.5f, new MovementInput { Forward = 1f });
    Require(Vector3.Distance(paused.Position, bounded.Position) < .001f, "long pause exceeded the ten-step movement bound");
    Vector3 before = paused.Position;
    paused.Update(float.NaN, new MovementInput { Forward = 1f });
    paused.Update(-1f, new MovementInput { Forward = 1f });
    Require(paused.Position == before, "invalid frame time moved the character");
    var swimmer = Make(); var smoothSwimmer = Make();
    swimmer.LiquidSurfaceZ = smoothSwimmer.LiquidSurfaceZ = 20f;
    var stroke = new MovementInput { Forward = 1f, Up = 1f, Jump = true };
    swimmer.Update(.3f, stroke);
    for (int i = 0; i < 6; i++) smoothSwimmer.Update(.05f, stroke);
    Require(Vector3.Distance(swimmer.Position, smoothSwimmer.Position) < .001f, "slow-frame swim differs from ordinary substeps");
}

static void VerifySlowFrameWall()
{
    var world = new CollisionWorld();
    AddFloor(world, -10f, 10f, -4f, 4f, 10f);
    AddWallAtX(world, 0f, -4f, 4f, 10f, 14f); world.Build();
    var body = CreateController(CreateEmptyTerrain(), world);
    body.TerrainAbsentByDesign = true; body.Teleport(-1f, 0f, 10f); body.Update(.05f, default);
    body.Update(.5f, new MovementInput { Forward = 1f });
    Require(body.Position.X < 0f && MathF.Abs(body.Position.Z - 10f) < .01f,
        $"slow frame crossed a collision wall: {body.Position}");
}

static int ProbeOnyxiaKnockback(bool wallReplay = false, bool impulseReplay = false)
{
    using var mpq = new MSUIClient.Formats.MpqMount("GameData/Data");
    MSUIClient.Formats.AdtTerrainReader.StormLibExtractor = mpq.ReadFile;
    var wdt = MSUIClient.Formats.WdtFile.Read("GameData/Data", "OnyxiaLairInstance")!;
    var placement = wdt.GlobalWmo!;
    var transform = (Matrix4x4)typeof(MSUIClient.World.Wmo.WmoRenderer).GetMethod("BuildGlobalPlacement", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [placement])!;
    var root = MSUIClient.Formats.WmoReader.ParseRoot(mpq.ReadFile(placement.ModelPath)!)!;
    var world = new CollisionWorld();
    for (int gi = 0; gi < root.NGroups; gi++)
    {
        var bytes = mpq.ReadFile($"{placement.ModelPath[..^4]}_{gi:D3}.wmo");
        if (bytes is null) continue;
        var group = MSUIClient.Formats.WmoReader.ParseGroup(bytes, root.Flags);
        if (group is null) continue;
        var triangles = new List<Vector3>();
        object?[] call = [group, triangles, new List<Vector3>(), new List<Vector3>(), 0, null];
        typeof(MSUIClient.World.Wmo.WmoRenderer).GetMethod("CollectCollision", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, call);
        for (int i = 0; i < triangles.Count; i += 3)
            world.AddTriangle(Vector3.Transform(triangles[i], transform), Vector3.Transform(triangles[i+1], transform), Vector3.Transform(triangles[i+2], transform));
    }
    world.Build();
    Console.WriteLine($"Onyxia geometry {world.TriangleCount} bounds {world.BoundsMin}..{world.BoundsMax}");
    if (impulseReplay)
    {
        int impulseFailures=0;
        foreach(float dt in new[]{1f/120,1f/60,.025f,.05f,.1f,.25f,.5f})
        {
            var body=CreateController(CreateEmptyTerrain(),world);body.TerrainAbsentByDesign=true;
            body.Teleport(17.225657f,-244.19337f,-85.5775f);body.Update(.05f,default);
            body.ApplyKnockback(new MSUIClient.Net.JumpInfo(-15f,.993302f,.115554f,25f));
            float minZ=body.Position.Z;
            for(int i=0;i<(int)(8/dt);i++){body.Update(dt,default);minZ=Math.Min(minZ,body.Position.Z);if(body.Grounded)break;}
            bool failed=!body.Grounded||minZ<-95;
            if(failed)impulseFailures++;
            Console.WriteLine($"IMPULSE dt={dt} end={body.Position} grounded={body.Grounded} minZ={minZ} {(failed?"FAIL":"PASS")}");
        }
        return impulseFailures==0?0:1;
    }
    if (wallReplay)
    {
        int failed = 0;
        foreach (var start in new[] { new Vector3(34.19002f,-232.32033f,-82.763435f), new Vector3(34.644863f,-228.26108f,-82.0984f), new Vector3(35.798035f,-223.24376f,-82.577126f) })
        foreach (float dt in new[] { 1f/60, .05f, .25f, .5f })
        foreach (float yaw in new[] { 0f, .25f, 1f, 1.4f, 1.57f, 3f, 4.7f, 6f })
        {
            var body = CreateController(CreateEmptyTerrain(),world);body.TerrainAbsentByDesign=true;
            body.Teleport(start.X,start.Y,start.Z);body.Update(.05f,default);
            for(int i=0;i<(int)(8/dt);++i)
            {
                body.Update(dt,new MovementInput{Forward=1,Yaw=yaw});
            }
            if(body.Position.Z < -100 || !float.IsFinite(body.Position.Z))
            { ++failed;Console.WriteLine($"WALL REPLAY FAIL start={start} dt={dt} yaw={yaw} end={body.Position}"); }
        }
        Console.WriteLine($"Real wall movement replay: {failed}/96 fell below the floor");return failed==0?0:1;
    }
    int failures = 0;
    foreach (float dt in new[] { 1f/60f, .05f, .1f, .25f, .5f })
    foreach (float zSpeed in new[] { -7.5f, -10f, -15f, -20f, 0f })
    {
        var body = CreateController(CreateEmptyTerrain(), world);
        body.TerrainAbsentByDesign = true;
        body.Teleport(30.907f, -217.251f, -83.959f);
        body.Update(.05f, default);
        body.ApplyKnockback(new MSUIClient.Net.JumpInfo(zSpeed, .93886f, .34430f, 25f));
        float minZ = body.Position.Z;
        for (int step = 0; step < (int)(8 / dt); step++)
        {
            body.Update(dt, default);
            minZ = MathF.Min(minZ, body.Position.Z);
            if (body.Grounded) break;
        }
        bool failed = !body.Grounded || minZ < -100f;
        if (failed) failures++;
        Console.WriteLine($"knockback dt={dt} zSpeed={zSpeed} end={body.Position} grounded={body.Grounded} minZ={minZ} {(failed ? "FAIL" : "PASS")}");
    }
    return failures > 0 ? 1 : 0;
}

static void VerifyKnockbackSlidesDownSteepGeometry()
{
    var world = new CollisionWorld();
    AddFloor(world, -10f, 0f, -10f, 10f, 0f);
    AddSlopeAtX(world, 0f, 2f, -10f, 10f, 0f, 5f);
    AddFloor(world, 2f, 10f, -10f, 10f, 5f);
    world.Build();
    foreach (float dt in new[] { 1f/60f, .05f, .25f })
    {
        var body = CreateController(CreateEmptyTerrain(), world);
        body.TerrainAbsentByDesign = true;
        body.Teleport(-2f, 0f, 0f);
        body.Update(.05f, default);
        body.ApplyKnockback(new MSUIClient.Net.JumpInfo(-8f, 1f, 0f, 10f));
        for (int step = 0; step < (int)(5 / dt); step++)
        {
            body.Update(dt, default);
            Require(body.Position.Z >= -0.01f, $"knockback crossed steep geometry at dt={dt}: {body.Position}");
            if (body.Grounded) break;
        }
        Require(body.Grounded && body.ForcedJump is null, $"knockback failed to land at dt={dt}: {body.Position}");
        Require(MathF.Abs(body.Position.Z) < .01f || MathF.Abs(body.Position.Z - 5f) < .01f,
            $"steep face incorrectly became standing ground: {body.Position}");
    }
}
