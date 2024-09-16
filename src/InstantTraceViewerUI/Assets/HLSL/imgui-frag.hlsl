struct PS_INPUT
{
    float4 pos : SV_POSITION;
    float4 col : COLOR0;
    float2 uv  : TEXCOORD0;
};

Texture2D Texture : register(t0);
sampler Sampler : register(s0);

float4 main(PS_INPUT input) : SV_Target
{
    float4 out_col = input.col * Texture.Sample(Sampler, input.uv);
    return out_col;
}