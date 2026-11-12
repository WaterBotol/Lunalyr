// Adapted from Synesthia by Jon Jaques (MIT), via Lunalyr for Mac's VisualizerShaders.metal: the shared vertex stage.
// One triangle large enough to cover the target, generated from the vertex index (no vertex buffer). uv is 0...1
// across the visible target with y up, as on the Mac.
#include "Common.hlsli"

FSQuadOut main(uint id : SV_VertexID)
{
    float2 pos = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
    FSQuadOut o;
    o.position = float4(pos, 0.0, 1.0);
    o.uv = pos * 0.5 + 0.5;
    return o;
}
