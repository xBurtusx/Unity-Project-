// A photographic moon with a soft halo, drawn on a quad that SkyMoon keeps fixed in the sky.
//
// Additive and unlit: black in the photo adds nothing, so only the lit disc shows, and it ignores
// fog and scene lighting (the moon is far beyond both). The disc is rotated 180 degrees because
// the source image is a northern-hemisphere view; from New Zealand the Moon is the other way up.
Shader "HorrorHouse/MoonDisc"
{
    Properties
    {
        _MainTex ("Moon photo", 2D) = "black" {}
        _Tint ("Tint", Color) = (0.92, 0.95, 1.0, 1)
        _Intensity ("Intensity", Float) = 1.6
        _DiscRadius ("Disc radius (fraction of quad)", Range(0.1, 0.5)) = 0.3
        _Halo ("Halo strength", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "MoonDisc"
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Tint;
                float _Intensity;
                float _DiscRadius;
                float _Halo;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 centred = i.uv - 0.5;
                float r = length(centred);                     // 0 at the middle, 0.5 at the quad edge

                // Photo: fit the disc to _DiscRadius, then turn it 180 degrees.
                float2 muv = centred / (_DiscRadius * 2.0) + 0.5;
                muv = 1.0 - muv;
                half3 photo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, muv).rgb;
                float disc = smoothstep(_DiscRadius, _DiscRadius - 0.012, r);     // soft anti-aliased limb
                half3 moon = photo * disc;

                // Halo: a cool glow that falls off smoothly to nothing at the quad edge.
                float h = saturate((0.5 - r) / (0.5 - _DiscRadius));
                half3 halo = _Halo * h * h * h * half3(0.55, 0.65, 0.9) * (1.0 - disc);

                return half4((moon + halo) * _Tint.rgb * _Intensity, 1);
            }
            ENDHLSL
        }
    }
}
