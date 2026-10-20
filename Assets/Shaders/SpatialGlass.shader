// Glass window body (Doc/ui-design.md section 3): rounded-rect SDF, tint over the (blurred) backdrop, vertical sheen,
// rim and soft shadow. The body quad draws the glass; a larger quad behind it (_ShadowOnly = 1) draws the shadow.
// Lengths are metres in the quad's own plane. No blur yet: while _SpatialBlurOn is 0 the flat _Fallback colour is used.
Shader "SpatialLauncher/SpatialGlass"
{
    Properties
    {
        _Tint ("Tint", Color) = (0.165, 0.173, 0.2, 0.72)
        _Fallback ("Fallback (no blur)", Color) = (0.118, 0.125, 0.149, 0.92)
        _RimTop ("Rim top", Color) = (1, 1, 1, 0.28)
        _RimBottom ("Rim bottom", Color) = (1, 1, 1, 0.08)
        _RimWidth ("Rim width (0 = 1 px)", Float) = 0
        _ShadowColor ("Shadow", Color) = (0, 0, 0, 0.35)
        _ShadowBlur ("Shadow blur", Float) = 0.024
        _ShadowOffset ("Shadow offset", Vector) = (0, -0.006, 0, 0)
        _Radius ("Corner radius", Float) = 0.024
        _Size ("Rect size", Vector) = (0.6, 0.4, 0, 0)
        _Quad ("Quad size", Vector) = (0.6, 0.4, 0, 0)
        _Solid ("Solid", Float) = 0
        _ShadowOnly ("Shadow only", Float) = 0
        _ZWrite ("ZWrite", Float) = 1
        [MainTexture] _MainTex ("Texture (flat shapes)", 2D) = "white" {}
        [MainColor] _Color ("Fill (flat shapes)", Color) = (1, 1, 1, 1)
        _UseTex ("Use texture", Float) = 0
        _Flat ("Flat shape", Float) = 0
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

    CBUFFER_START(UnityPerMaterial)
        half4 _Tint;
        half4 _Fallback;
        half4 _RimTop;
        half4 _RimBottom;
        float _RimWidth;
        half4 _ShadowColor;
        float _ShadowBlur;
        float4 _ShadowOffset;
        float _Radius;
        float4 _Size;
        float4 _Quad;
        float _Solid;
        float _ShadowOnly;
        half4 _Color;
        float4 _MainTex_ST;
        float _UseTex;
        float _Flat;
    CBUFFER_END

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    // Set by the blur pass (later); until then _SpatialBlurOn stays 0.
    TEXTURE2D(_SpatialBackdrop);
    SAMPLER(sampler_SpatialBackdrop);
    float _SpatialBlurOn;

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

    float RoundedBoxSdf(float2 p, float2 halfSize, float radius)
    {
        float2 q = abs(p) - (halfSize - radius);
        return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
    }

    // Scales a linear colour down so its luminance is at most 0.25 (the backdrop clamp that keeps text readable).
    half3 ClampLuminance(half3 c)
    {
        half l = dot(c, half3(0.2126, 0.7152, 0.0722));
        return l > 0.25 ? c * (0.25 / l) : c;
    }

    half4 Frag(Varyings input) : SV_Target
    {
        float2 p = (input.uv - 0.5) * _Quad.xy;
        float2 halfSize = _Size.xy * 0.5;
        float radius = min(_Radius, min(halfSize.x, halfSize.y));
        float d = RoundedBoxSdf(p, halfSize, radius);
        // One pixel of anti-aliasing on the edge.
        float aa = max(fwidth(d), 1e-5);

        if (_ShadowOnly > 0.5)
        {
            float ds = RoundedBoxSdf(p - _ShadowOffset.xy, halfSize, radius);
            float blur = max(_ShadowBlur, 1e-5);
            float shade = 1.0 - smoothstep(0.0, blur, ds);
            // Nothing under the body itself: glass over a shadow would look muddy.
            float outside = saturate(d / aa + 0.5);
            return half4(_ShadowColor.rgb, _ShadowColor.a * shade * outside);
        }

        float cover = saturate(0.5 - d / aa);
        // The slab writes depth, so clip its transparent corners.
        clip(cover - 0.01);

        // Flat shapes (in-window controls and images): a solid fill, times the texture when _UseTex is 1, with the rounded edge.
        if (_Flat > 0.5)
        {
            half4 fill = _Color;
            if (_UseTex > 0.5) fill.rgb *= SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv * _MainTex_ST.xy + _MainTex_ST.zw).rgb;
            return half4(fill.rgb, fill.a * cover);
        }

        float t = saturate(p.y / max(_Size.y, 1e-5) + 0.5);   // 0 at the bottom, 1 at the top

        half3 rgb;
        half alpha;
        if (_Solid > 0.5)
        {
            rgb = _Fallback.rgb;
            alpha = 1;
        }
        else
        {
            if (_SpatialBlurOn > 0.5)
            {
                float2 screenUv = input.positionCS.xy / _ScaledScreenParams.xy;
                half3 backdrop = ClampLuminance(SAMPLE_TEXTURE2D(_SpatialBackdrop, sampler_SpatialBackdrop, screenUv).rgb);
                rgb = lerp(backdrop, _Tint.rgb, _Tint.a);
                alpha = 1;
            }
            else
            {
                rgb = _Fallback.rgb;
                alpha = _Fallback.a;
            }
            // The sheen is added in sRGB, where 0.04 is a subtle lift; in linear it would wash the glass out.
            #ifndef UNITY_COLORSPACE_GAMMA
            rgb = SRGBToLinear(LinearToSRGB(rgb) + 0.04 * t);
            #else
            rgb += 0.04 * t;
            #endif
        }

        // Rim: a band just inside the edge, brighter at the top.
        float rimWidth = max(_RimWidth, aa);
        half4 rim = lerp(_RimBottom, _RimTop, t);
        float band = saturate(1.0 + d / rimWidth);
        half rimA = rim.a * band;
        rgb = lerp(rgb, rim.rgb, rimA);
        alpha = alpha + (1 - alpha) * rimA;

        return half4(rgb, alpha * cover);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Glass"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}
