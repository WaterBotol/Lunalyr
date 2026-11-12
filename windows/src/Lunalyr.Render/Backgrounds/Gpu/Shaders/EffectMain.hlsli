// The pixel shader every GPU effect shares: the effect's light at this point, graded with the album gradient
// (Common.hlsli). Each effect file defines Effect() and then includes this.
float4 main(float4 position : SV_Position) : SV_Target
{
    float2 p = position.xy / PixelScale;
    float3 lit = Effect(p, AmbientLight(Time));
    return float4(Grade(lit, p), 1.0);
}
