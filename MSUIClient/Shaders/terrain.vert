#version 330 core

// MSUI Client - terrain vertex shader.
// Positions arrive in WoW world space (X north, Y west, Z up). There is no
// coordinate conversion in this client; the camera view matrix uses +Z as up,
// so world space goes to the GPU untouched.
//
// ASCII ONLY. Some GLSL compilers (Intel notably) abort with a bogus
// "pre-mature EOF" on any non-ASCII byte, even inside a comment.
//
// THE UV IS PER CHUNK, NOT PER TILE, AND THAT IS DELIBERATE.
// It used to run 0..1 across the whole ADT, which forced the fragment shader to
// wrap it with fract() to get a per-chunk tiling coordinate - and a fract()
// inside a triangle is a derivative cliff that made every chunk edge sample the
// deepest mip, drawing a dark grid over the world. A per-chunk 0..1 coordinate
// needs no wrap at all: the tiling factor is a plain multiply, the wrap happens
// in the sampler's address unit, and the discontinuity between chunks lands on
// a vertex boundary between two separate triangles where derivatives stay
// correct on both sides.
//
// It is also exactly what the alpha array wants, since each chunk's mask is its
// own layer addressed 0..1.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aChunkUV;     // 0..1 across this vertex's own MCNK
layout (location = 3) in vec4 aLayers;      // tileset array indices, -1 = unused
layout (location = 4) in float aAlphaLayer; // this chunk's layer in the alpha array

uniform mat4 uViewProjection;
uniform vec3 uCameraOrigin;
// Camera-relative equation of an optional world cut. It is ignored unless the
// caller enables GL_CLIP_DISTANCE0.
uniform vec4 uWorldClipPlane;
// Quest Tabletop view transform (World/TabletopViewTransform.cs). Only the tabletop's own renderer
// instances set these; the game's programs keep the defaults (relief off, clip distances 1-3 disabled).
uniform int uReliefOn;
uniform float uReliefK;
uniform float uReliefBase;
uniform vec4 uBoardClip1;
uniform vec4 uBoardClip2;
uniform vec4 uBoardClip3;

out vec3 vWorldPos;
// Quest Tabletop only: the SHOWN (exaggerated) absolute position, for side-projected texturing on steep faces.
out vec3 vReliefPos;
out vec3 vNormal;
out vec2 vChunkUV;
flat out vec4 vLayers;
flat out float vAlphaLayer;

void main()
{
    vec3 relativePosition = aPosition - uCameraOrigin;
    vec3 normal = aNormal;
    if (uReliefOn == 1)
    {
        // z' = z + (k - 1)(z - base); normals take the inverse-transpose of that z scale.
        relativePosition.z += (uReliefK - 1.0) * (aPosition.z - uReliefBase);
        normal = vec3(aNormal.xy, aNormal.z / uReliefK);
    }

    vWorldPos   = relativePosition;
    vReliefPos  = vec3(aPosition.xy, aPosition.z + (uReliefOn == 1 ? (uReliefK - 1.0) * (aPosition.z - uReliefBase) : 0.0));
    vNormal     = normalize(normal);
    vChunkUV    = aChunkUV;
    vLayers     = aLayers;
    vAlphaLayer = aAlphaLayer;

    gl_ClipDistance[0] = dot(uWorldClipPlane, vec4(relativePosition, 1.0));
    gl_ClipDistance[1] = dot(uBoardClip1, vec4(relativePosition, 1.0));
    gl_ClipDistance[2] = dot(uBoardClip2, vec4(relativePosition, 1.0));
    gl_ClipDistance[3] = dot(uBoardClip3, vec4(relativePosition, 1.0));
    gl_Position = uViewProjection * vec4(relativePosition, 1.0);
}
