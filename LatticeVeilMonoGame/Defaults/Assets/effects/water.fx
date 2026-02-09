#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0
    #define PS_SHADERMODEL ps_4_0
#endif

float4x4 World;
float4x4 View;
float4x4 Projection;
float Time;
float WaveStrength = 0.015f;
float UvScrollSpeed = 0.015f;
float3 TintColor = float3(0.56f, 0.78f, 1.0f);
float WaterAlpha = 0.72f;

texture Texture0;
sampler TextureSampler = sampler_state
{
    Texture = <Texture0>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

struct VSInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float3 WorldPos : TEXCOORD1;
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    float4 worldPos = mul(input.Position, World);
    output.Position = mul(mul(worldPos, View), Projection);
    output.TexCoord = input.TexCoord;
    output.WorldPos = worldPos.xyz;
    return output;
}

float4 MainPS(VSOutput input) : COLOR0
{
    float2 uvA = input.TexCoord + float2(Time * UvScrollSpeed, Time * UvScrollSpeed * 0.65f);
    float2 uvB = input.TexCoord + float2(-Time * UvScrollSpeed * 0.55f, Time * UvScrollSpeed * 0.4f);

    float wave = sin((input.WorldPos.x + Time * 0.9f) * 0.2f) * 0.5f
        + cos((input.WorldPos.z + Time * 1.15f) * 0.22f) * 0.5f;
    float2 ripple = float2(wave, -wave) * (WaveStrength * 5.0f);

    float4 cA = tex2D(TextureSampler, uvA + ripple);
    float4 cB = tex2D(TextureSampler, uvB - ripple * 0.65f);
    float4 baseColor = lerp(cA, cB, 0.5f);
    baseColor.rgb *= TintColor;
    baseColor.a = saturate(baseColor.a * WaterAlpha);
    return baseColor;
}

technique WaterTech
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
