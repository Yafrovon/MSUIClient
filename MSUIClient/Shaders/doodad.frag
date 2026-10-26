#version 330 core

// MSUI Client - M2 doodad fragment shader.
//
// FORKED FROM wmo.frag ON PURPOSE - see doodad.vert. The exterior lighting and
// fog model below is character-for-character the terrain/WMO one, because a
// barrel that lights differently from the ground under it reads as wrong
// without being nameable. What this shader adds, and wmo.frag must never gain,
// is per-instance baked interior light and the M2 "unlit" material flag.
//
// ASCII ONLY - see doodad.vert.

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
in vec4 vLight;
in float vAppearStart;
in float vHighlight;

uniform sampler2D uTexture;
uniform int   uHasTexture;

// Per-object appear fade (benilla model_fade.rs). Only the OUTPUT ALPHA is
// touched - never the lighting - so this is purely a reveal, not a colour
// change. Enabled sets the whole feature; uNow is the world clock in seconds;
// uAppearFadeSecs is the ramp length (2.0 in benilla). A fragment fades in as
// alpha = t^3 over uAppearFadeSecs from vAppearStart. vAppearStart <= 0 means
// "already resident" -> fully opaque, which is every instance except one just
// streamed in while the world was on screen.
uniform int   uAppearFadeEnabled;
uniform float uNow;
uniform float uAppearFadeSecs;

// Alpha below which a fragment is thrown away. Set PER BATCH, because it must
// be zero for any texture that has no alpha channel: a BLP without alpha can
// decode with every alpha byte at zero, and a fixed cut then discards geometry
// that loaded perfectly well. Leaves and fence lattices still need a real cut,
// so this cannot simply be removed.
uniform float uAlphaCutoff;


// Command View interior cut (Engine/WorldCut.cs): camera-relative footprint (minX, minY, maxX,
// maxY) and cut height. Fragments inside the footprint and above the height are discarded.
uniform int   uCutActive;
uniform vec4  uCutRect;
uniform float uCutZ;

// Command View line-of-sight cut (Engine/WorldCut.cs): camera-relative segments from the eye
// (origin) to each party member's chest. A fragment inside a tunnel around a segment, nearer
// than the unit, is discarded; the tunnel tapers from uSightRadius.x at the eye to .y at the unit.
uniform int   uSightCount;
uniform vec3  uSightTo[8];
uniform vec2  uSightRadius;
// Command View primary slice (Engine/WorldCut.cs, WorldSlice) - see wmo.frag; props on the
// near half of a stairwell go with the treads they stand on.
uniform int   uSliceActive;
uniform vec3  uSliceDir;
uniform float uSliceDepth;
uniform float uSliceFloorZ;
uniform vec2  uSliceCentre;
uniform float uSliceRadius;
// Canopy cut: uCanopy.xy = radius / height above the feet; feet are uSightTo minus the chest lift.
uniform vec3  uCanopy;      // x = radius, y = cut height above feet, z = chest lift
uniform vec3  uCameraPos;
uniform vec3  uSunDirection;   // points TOWARD the sun, normalised
uniform vec3  uSunColor;
uniform float uSunIntensity;
uniform vec3  uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;

// The classic render path's overbright factor, shared with the walls. Vanilla
// authored vertex light in [0, 2], not [0, 1]. This MUST be the same value the
// WMO renderer uses or props stop matching the surfaces they sit on.
uniform float uVertexColorScale;

// M2 material flag 0x01. Lantern glows, fire quads and glow planes are authored
// at full brightness and are supposed to ignore lighting completely - without
// this a lantern inside a dark room goes out, which is the one thing a lantern
// must not do.
uniform int   uUnlit;
// M2 batch colour x texture weight at the world clock (DoodadRenderer.BatchTintFor); off = untinted batch.
uniform int   uBatchTintOn;
uniform vec4  uBatchTint;

// Beyond-portal fill light (see wmo.frag). Weighted by (1 - vLight.a) so it
// lands on interior-baked props and fades out on daylight-dominant ones;
// radius 0 disables it whenever no instance portal is near.
uniform vec3  uPortalLightPos;    // camera-relative, same space as vWorldPos
uniform vec3  uPortalLightColor;  // colour premultiplied by intensity
uniform float uPortalLightRadius; // yards; 0 = off

uniform int uPointLightCount;
uniform vec3 uPointLightPos[8];
uniform vec3 uPointLightColor[8];

vec3 carriedPointLight(vec3 normal, vec3 worldPos)
{
    float d0 = 1e30, d1 = 1e30, d2 = 1e30;
    vec3 v0 = vec3(0.0), v1 = vec3(0.0), v2 = vec3(0.0);
    vec3 c0 = vec3(0.0), c1 = vec3(0.0), c2 = vec3(0.0);
    for (int i = 0; i < 8; i++)
    {
        if (i >= uPointLightCount) break;
        vec3 delta = uPointLightPos[i] - worldPos;
        float ds = dot(delta, delta);
        if (ds < d0) { d2=d1; v2=v1; c2=c1; d1=d0; v1=v0; c1=c0; d0=ds; v0=delta; c0=uPointLightColor[i]; }
        else if (ds < d1) { d2=d1; v2=v1; c2=c1; d1=ds; v1=delta; c1=uPointLightColor[i]; }
        else if (ds < d2) { d2=ds; v2=delta; c2=uPointLightColor[i]; }
    }
    vec3 sum = vec3(0.0);
    if (d0 < 1e29) { float d=sqrt(d0); sum += c0 * max(dot(normal, v0/max(d,0.001)),0.0) / max(0.7*d + 0.03*d*d, 0.001); }
    if (d1 < 1e29) { float d=sqrt(d1); sum += c1 * max(dot(normal, v1/max(d,0.001)),0.0) / max(0.7*d + 0.03*d*d, 0.001); }
    if (d2 < 1e29) { float d=sqrt(d2); sum += c2 * max(dot(normal, v2/max(d,0.001)),0.0) / max(0.7*d + 0.03*d*d, 0.001); }
    return sum;
}

// Steady opaque/cutout doodads carry their painterly importance in alpha.
// uPreserveAlpha is enabled for a draw containing an active appear fade so the
// same output continues to drive straight-alpha blending correctly.
uniform float uStyleWeight;
uniform int   uPreserveAlpha;

// 1 while the renderer's blended pass (M2 blend modes 2-6) is drawing. The
// glBlendFunc for those modes reads source alpha, so the output alpha must stay
// the TEXTURE alpha - a lamp halo authored as alpha 0..0.6 additive must not be
// overwritten with the style weight or the appear fade. During an appear fade
// the texture alpha is MULTIPLIED by the fade instead of replaced, so a fading
// lantern's glow eases in without ever blowing out to full strength.
uniform int   uBlendedBatch;

// How this batch meets fog, per M2 render-flag 0x02 and blend mode:
//   0 - normal: mix toward uFogColor (opaque geometry).
//   1 - unfogged (render flag 0x02): fog never touches it. Lamp glows carry
//       this flag because a halo tinted toward daylight fog reads as a dirty
//       decal, not a light.
//   2 - additive (blend 3/4): fade toward BLACK, because black is the additive
//       identity. Mixing toward the fog colour would make distant glows ADD the
//       fog colour to the framebuffer - fog would emit light.
//   3 - modulate (blend 5): fade toward WHITE, the multiplicative identity.
//   4 - modulate2x (blend 6): fade toward 0.5 grey, the 2x-modulate identity.
uniform int   uFogMode;

// Party sight (World/PartySight.cs): the picture is the camera's own view plus the primary's,
// nothing else. uPartySightCube: distance from the primary's eye to the nearest solid in every
// direction. uPartySeenDepth: distance to the nearest surface the primary sees under this pixel
// (0 = none). uPartyPlainDepth: distance to the nearest solid the camera would see uncut.
// A fragment the primary sees stays. One it cannot see is CUT when nearer than the seen surface
// (it hides the primary's view), kept when it is what the camera sees anyway, and FOGGED when
// it is only visible because a cut opened onto it. All positions camera-relative, like vWorldPos.
uniform int         uPartySightActive;
uniform samplerCube uPartySightCube;    // unit 6
uniform sampler2D   uPartySeenDepth;    // unit 7: exact
uniform sampler2D   uPartyPlainDepth;   // unit 8
uniform sampler2D   uPartySeenDilated;  // unit 9: seen, grown a few pixels (the rim)
uniform vec3        uPartySightEye;
uniform float       uPartySightBias;

// Unblocked from the primary's eye, AND the side the camera looks at is the side the eye is on
// (a thin roof's top is not seen from below; its underside is). n: this face's normal from
// screen derivatives, oriented toward the camera by the caller.
bool PartySightSees(vec3 p, vec3 n)
{
    vec3 d = p - uPartySightEye;
    if (length(d) > texture(uPartySightCube, d).r + uPartySightBias) return false;
    return dot(n, -d) > 0.0;
}

out vec4 FragColor;

void main()
{
    // Derivatives first, before any discard, so the face normal is always defined.
    vec3 partyN = normalize(vNormal);   // vertex normal: derivatives hatch at grazing angles
    if (dot(partyN, -vWorldPos) < 0.0) partyN = -partyN;
    float partyFog = 0.0;   // 1 = painted flat fog-of-war colour (unseen, behind a cut)
    if (uPartySightActive == 1 && !PartySightSees(vWorldPos, partyN))
    {
        float partyDist = length(vWorldPos);
        ivec2 partyPx = ivec2(gl_FragCoord.xy);
        float partySeen = texelFetch(uPartySeenDepth, partyPx, 0).r;
        float partyRim  = texelFetch(uPartySeenDilated, partyPx, 0).r;
        float partyPlain = texelFetch(uPartyPlainDepth, partyPx, 0).r;
        // In front of the primary's view: cut. On the rim of the opening (a sliver the exact
        // test misses, or the far map through the hole): painted fog, never sky.
        if (partySeen > 0.0 && partyDist < partySeen - 0.3) discard;
        else if (partyRim > 0.0 && partyDist < partyRim - 2.0) partyFog = 0.92;
        else if (partyPlain > 0.0 && partyDist > partyPlain + 2.0) partyFog = 0.92;
    }
    if (uCutActive == 1 &&
        vWorldPos.x > uCutRect.x && vWorldPos.x < uCutRect.z &&
        vWorldPos.y > uCutRect.y && vWorldPos.y < uCutRect.w)
    {
        if (vWorldPos.z > uCutZ) discard;
        // Floor-like faces (treads, ramps, a sloping cave floor) are never sliced: the slice
        // exists to remove the near WALLS of a shaft, and a floor rising toward the camera
        // vanished with them (owner, 2026-09-03).
        if (uSliceActive == 1 && abs(vNormal.z) < 0.6 && vWorldPos.z > uSliceFloorZ &&
            dot(vWorldPos, uSliceDir) < uSliceDepth)
        {
            vec2 sliceFlat = vWorldPos.xy - uSliceCentre;
            if (dot(sliceFlat, sliceFlat) < uSliceRadius * uSliceRadius) discard;
        }
    }
    for (int i = 0; i < uSightCount; i++)
    {
        vec3 b = uSightTo[i];
        float len2 = max(dot(b, b), 1e-4);
        float t = clamp(dot(vWorldPos, b) / len2, 0.0, 1.0);
        if (t >= 0.985) continue;
        float d = length(vWorldPos - b * t);
        if (d < mix(uSightRadius.x, uSightRadius.y, t)) discard;
        // Canopy: anything of this doodad above the cut height and within the radius of the unit.
        vec3 feet = b - vec3(0.0, 0.0, uCanopy.z);
        if (vWorldPos.z > feet.z + uCanopy.y &&
            length(vWorldPos.xy - feet.xy) < uCanopy.x) discard;
    }
    vec4 albedo = uHasTexture == 1
        ? texture(uTexture, vUV)
        : vec4(0.62, 0.60, 0.56, 1.0);
    if (uBatchTintOn == 1)
    {
        // A batch authored invisible right now draws nothing and writes no depth (vanilla skips its submission).
        if (uBatchTint.a < 1.0 / 255.0) discard;
        albedo.rgb *= uBatchTint.rgb;
        // Only a blending batch fades: on an opaque or alpha-key batch a partial weight would just eat into the
        // 0.5 cutout and delete foliage, and the opaque pass has no blend func to consume it.
        if (uBlendedBatch == 1) albedo.a *= uBatchTint.a;
    }

    if (uAlphaCutoff > 0.0 && albedo.a < uAlphaCutoff) discard;

    vec3 normal = normalize(vNormal);

    // Two-sided materials need the geometric side currently being rasterized,
    // not a normal forced toward the camera. Camera-facing normal flips made a
    // fixed sun appear to move when the camera crossed a tangent plane.
    if (!gl_FrontFacing) normal = -normal;

    float lambert = max(dot(normal, uSunDirection), 0.0);
    vec3 light = uAmbientColor * uAmbientIntensity
        + uSunColor * lambert * uSunIntensity;

    // Vanilla never lit WMO interiors at runtime. Walls take their light from
    // MOCV; the props inside take theirs from MODD.color, which the artist
    // baked per PLACEMENT - the same barrel model is a different colour in a
    // lit corner than in a dark one. Feeding a prop the outdoor sun is why
    // barrels used to glow inside rooms that are correctly dark.
    //
    // No MOHD ambient is added here, deliberately: the wall path does not add
    // it either, and in classic-era data it is already baked into the authored
    // values. Adding it on one side and not the other is what makes props
    // detach from the floor.
    vec3 baked = vLight.rgb * uVertexColorScale;
    vec3 lighting = mix(baked, light, vLight.a);
    lighting += carriedPointLight(normal, vWorldPos);

    // Beyond-portal fill (see the uniform block). Scaled by (1 - vLight.a) so
    // it favours interior-baked props and leaves daylight-lit ones untouched.
    if (uPortalLightRadius > 0.0)
    {
        float pd = distance(uPortalLightPos, vWorldPos);
        float atten = clamp(1.0 - pd / uPortalLightRadius, 0.0, 1.0);
        lighting += uPortalLightColor * (atten * atten) * (1.0 - vLight.a);
    }

    if (uUnlit == 1) lighting = vec3(1.0);

    // Mouse-over highlight for the hovered server gameobject: the same
    // additive brighten the creature/player shaders apply (light + 64/255),
    // added AFTER the unlit clamp so even a glow batch brightens on hover.
    lighting += vec3(vHighlight);

    vec3 lit = albedo.rgb * lighting;

    float dist = distance(uCameraPos, vWorldPos);
    float fog = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    // See uFogMode. The identity colour per blend equation, or no fog at all.
    vec3 fogTarget = uFogColor;
    if      (uFogMode == 1) fog = 0.0;
    else if (uFogMode == 2) fogTarget = vec3(0.0);
    else if (uFogMode == 3) fogTarget = vec3(1.0);
    else if (uFogMode == 4) fogTarget = vec3(0.5);

    // Appear fade: cutout stays keyed on the texture alpha (the discard above),
    // so the silhouette is stable; the surviving fragments fade together as
    // alpha = t^3. Non-fading instances (vAppearStart <= 0) resolve to fade 1.
    float fade = 1.0;
    if (uAppearFadeEnabled == 1 && vAppearStart > 0.0)
    {
        float t = clamp((uNow - vAppearStart) / max(uAppearFadeSecs, 0.0001), 0.0, 1.0);
        fade = t * t * t;
        if (fade <= 0.0) discard;   // invisible AND writes no depth
    }

    float outAlpha;
    if (uBlendedBatch == 1)
    {
        // The blend func consumes this alpha (see uBlendedBatch). Multiplying
        // by the appear fade eases the batch in without conflicting blend
        // state - the func stays the batch's own.
        outAlpha = albedo.a * fade;
    }
    else
    {
        // Exactly the original opaque-pass output: fade while easing in,
        // texture alpha otherwise, style weight whenever nothing is blending.
        outAlpha = uAppearFadeEnabled == 1 ? fade : albedo.a;
        if (uPreserveAlpha == 0) outAlpha = uStyleWeight;
    }

    FragColor = vec4(mix(mix(lit, fogTarget, fog), vec3(0.04, 0.035, 0.05), partyFog), outAlpha);
}
