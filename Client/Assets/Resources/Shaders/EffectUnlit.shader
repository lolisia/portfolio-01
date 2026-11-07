// 무기 효과용 가산 혼합 Unlit. 정점 색 * _BaseColor
// _Ring이 1이면 Quad UV 중심 기준 원형으로 그림. 가장자리가 밝고 안쪽은 옅게 채우며 _PulseSpeed로 깜빡임
Shader "aisilol/EffectUnlit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Ring ("Ring", Float) = 0
        _RingWidth ("Ring Width", Float) = 0.08
        _PulseSpeed ("Pulse Speed", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Ring;
                float _RingWidth;
                float _PulseSpeed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 color = input.color * _BaseColor;

                if (_Ring > 0.5)
                {
                    float distance = length(input.uv - 0.5) * 2;
                    if (distance > 1)
                        discard;

                    float edge = saturate(1 - abs(1 - _RingWidth - distance) / _RingWidth);
                    float pulse = 0.75 + 0.25 * sin(_Time.y * _PulseSpeed);
                    color.a *= saturate(edge + distance * distance * 0.25) * pulse;
                }

                return color;
            }
            ENDHLSL
        }
    }
}
