
Texture2D<float> KiwiY : register(t0);
Texture2D<float2> KiwiUV : register(t1);
RWTexture2D<float4> KiwiOut : register(u0);
cbuffer KiwiColor : register(b0)
{
    float4 KiwiRange;  // yOffset, yScale, cOffset, cScale
    float4 KiwiMatrix; // rCr, gCb, gCr, bCb
};
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    KiwiOut.GetDimensions(width, height);
    if (id.x >= width || id.y >= height) return;
    float ySample = KiwiY.Load(int3(id.xy, 0));
    float2 uvSample = KiwiUV.Load(int3(id.xy / 2, 0));
    float y = (ySample - KiwiRange.x) * KiwiRange.y;
    float cb = (uvSample.x - KiwiRange.z) * KiwiRange.w;
    float cr = (uvSample.y - KiwiRange.z) * KiwiRange.w;
    float3 rgb;
    rgb.r = y + KiwiMatrix.x * cr;
    rgb.g = y - KiwiMatrix.y * cb - KiwiMatrix.z * cr;
    rgb.b = y + KiwiMatrix.w * cb;
    KiwiOut[id.xy] = float4(saturate(rgb), 1.0);
}
