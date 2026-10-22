// Shared backdrop blur (Doc/ui-design.md section 11): pass 0 downsamples the passthrough feed into the 240 x 180 target,
// pass 1 is a dual-Kawase downsample, pass 2 the dual-Kawase upsample. Used through Graphics.Blit and a CommandBuffer by BackdropBlur.
Shader "SpatialLauncher/KawaseBlur"
{
    Properties
    {
        [MainTexture] _MainTex ("Texture", 2D) = "white" {}
        _CopyStep ("Copy tap offset (uv)", Vector) = (0.00208, 0.00278, 0, 0)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);
    float4 _BlurTexel;   // xy = 1 / size of the source; BackdropBlur sets it before each pass
    float4 _CopyStep;

    struct Attributes
    {
        float4 positionOS : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
        output.uv = input.uv;
        return output;
    }

    half3 Tap(float2 uv) { return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb; }

    // The source is larger than the target: a centre tap and four corner taps a half target texel out.
    half4 FragCopy(Varyings input) : SV_Target
    {
        float2 s = _CopyStep.xy;
        half3 c = Tap(input.uv) * 4.0
            + Tap(input.uv + float2(-s.x, -s.y)) + Tap(input.uv + float2(s.x, -s.y))
            + Tap(input.uv + float2(-s.x, s.y)) + Tap(input.uv + float2(s.x, s.y));
        return half4(c * 0.125, 1);
    }

    half4 FragDown(Varyings input) : SV_Target
    {
        float2 h = _BlurTexel.xy;
        half3 c = Tap(input.uv) * 4.0
            + Tap(input.uv + float2(-h.x, -h.y)) + Tap(input.uv + float2(h.x, -h.y))
            + Tap(input.uv + float2(-h.x, h.y)) + Tap(input.uv + float2(h.x, h.y));
        return half4(c * 0.125, 1);
    }

    half4 FragUp(Varyings input) : SV_Target
    {
        float2 h = _BlurTexel.xy * 0.5;
        float2 uv = input.uv;
        half3 c = Tap(uv + float2(-h.x * 2.0, 0.0))
            + Tap(uv + float2(-h.x, h.y)) * 2.0
            + Tap(uv + float2(0.0, h.y * 2.0))
            + Tap(uv + float2(h.x, h.y)) * 2.0
            + Tap(uv + float2(h.x * 2.0, 0.0))
            + Tap(uv + float2(h.x, -h.y)) * 2.0
            + Tap(uv + float2(0.0, -h.y * 2.0))
            + Tap(uv + float2(-h.x, -h.y)) * 2.0;
        return half4(c * (1.0 / 12.0), 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Copy"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCopy
            ENDHLSL
        }
        Pass
        {
            Name "Down"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDown
            ENDHLSL
        }
        Pass
        {
            Name "Up"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUp
            ENDHLSL
        }
    }
}
