// The pixel shader every shader-drawn line pattern shares: the pattern's coverage at this point (0...1, already
// including each line's own opacity) as premultiplied white, scaled by the pattern's strength; the layer adds it to the
// colour field. Each pattern file defines Lines() and then includes this. Windows' own.
float4 main(float4 position : SV_Position) : SV_Target
{
    float2 p = position.xy / PixelScale;
    float a = saturate(Lines(p)) * Strength;
    return float4(a, a, a, a);
}
