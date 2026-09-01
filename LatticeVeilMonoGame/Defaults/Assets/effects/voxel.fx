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

// Per-frame time-of-day parameters
// .r = sky brightness factor (0.0 = night, 1.0 = full daylight)
// .g = block light intensity multiplier (1.0 = default)
// .b = ambient occlusion strength factor (1.0 = default, lower = darker AO)
float4 SkyLightParams;

// Block light tint — warm torch-light color
float3 BlockLightTint;

texture BaseTexture;
sampler BaseSampler = sampler_state
{
    Texture = <BaseTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU  = Wrap;
    AddressV  = Wrap;
};

struct VSInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
    float4 Color : COLOR0; // R=sky(0-255), G=block(0-255), B=AO(0-255), A=alpha
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
    float4 Color : COLOR0;
};

VSOutput MainVS(VSInput input)
{
    VSOutput output;
    float4 worldPos = mul(input.Position, World);
    output.Position = mul(mul(worldPos, View), Projection);
    output.TexCoord = input.TexCoord;
    output.Color = input.Color;
    return output;
}

float4 MainPS(VSOutput input) : COLOR0
{
    // Sample the base color from the texture atlas
    float4 baseTex = tex2D(BaseSampler, input.TexCoord);

    // Decode per-vertex light/ao channels (0-255 packed in Color.rgb)
    // R = sky light (0=no sky, 255=full daylight level 15)
    // G = block/torch light (0=no block light, 255=max level 14)
    // B = ambient occlusion (255=no AO, lower=darker corner)
    // A = alpha
    float skyRaw   = input.Color.r / 255.0;   // [0..1]
    float blockRaw = input.Color.g / 255.0;   // [0..1]
    float aoRaw    = input.Color.b / 255.0;   // [0.251..1], 255=no AO
    float alphaRaw = input.Color.a / 255.0;   // [0..1]

    // ── Per-frame sky brightness ──────────────────────────────────────
    // Allows time-of-day changes to affect lighting without rebuilding chunk meshes.
    // SkyLightParams.r: 0.0 = no sky light (night), 1.0 = full daylight.
    skyRaw *= SkyLightParams.r;

    // Block light intensity multiplier
    blockRaw *= SkyLightParams.g; // 1.0 by default

    // Warm torch-light tint for block light
    float3 warmTint = float3(1.0f, 0.65f, 0.35f); // orange-yellow warmth

    // Sample texture color
    float3 texColor = baseTex.rgb;

    // Apply ambient occlusion — preserves the baked AO darkening in corners.
    // aoRaw is in [0.251..1] where 1.0 = no occlusion, lower = darker.
    float3 color = texColor * aoRaw;

    // Add block light with warm torch tint — makes torches visibly warm.
    color += warmTint * blockRaw;

    // Add subtle sky light contribution (already skyRaw-adjusted above).
    // A small ambient term keeps the shade from going completely black.
    float3 skyContrib = float3(skyRaw, skyRaw, skyRaw) * 0.15f;
    color += skyContrib;

    // Final output — clamp and modulate alpha
    color = saturate(color);

    return float4(color, alphaRaw * baseTex.a);
}

technique VoxelTech
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader    = compile PS_SHADERMODEL MainPS();
    }
}