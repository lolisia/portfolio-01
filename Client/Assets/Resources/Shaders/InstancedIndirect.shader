// RenderMeshIndirect 전용. Instance 행렬은 _Matrices, 보이는 Instance 인덱스는 _Visible에서 읽음
// 정점 애니메이션 : 위치 기반 위상으로 Instance마다 다르게 튀어 오르기(_Hop) / 찌그러짐(_Squash) / Y축 회전(_Spin)
Shader "aisilol/InstancedIndirect"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _AnimSpeed ("Animation Speed", Float) = 0
        _Hop ("Hop Height", Float) = 0
        _Squash ("Squash", Float) = 0
        _Spin ("Spin Speed", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float _AnimSpeed;
            float _Hop;
            float _Squash;
            float _Spin;
        CBUFFER_END

        StructuredBuffer<float4x4> _Matrices;
        StructuredBuffer<uint> _Visible;
        uint _VisibleOffset;

        // Object 공간 정점 / 법선에 애니메이션을 적용하고 World 공간으로 변환
        void TransformInstance(uint instanceID, float3 positionOS, float3 normalOS, out float3 positionWS, out float3 normalWS)
        {
            float4x4 m = _Matrices[_Visible[_VisibleOffset + instanceID]];
            float3 origin = float3(m._m03, m._m13, m._m23);

            // 인덱스는 제거 시 바뀌므로 위치로 위상을 정함. 이동하면 위상도 바뀌어 걷는 것처럼 보임
            float phase = _Time.y * _AnimSpeed + dot(origin.xz, float2(1.7, 1.3));
            float wave = sin(phase);

            float squash = 1 + _Squash * wave;
            positionOS.y *= squash;
            positionOS.xz *= 1 - _Squash * wave * 0.5;

            float angle = _Time.y * _Spin;
            float s, c;
            sincos(angle, s, c);
            float2x2 spin = float2x2(c, -s, s, c);
            positionOS.xz = mul(spin, positionOS.xz);
            normalOS.xz = mul(spin, normalOS.xz);

            positionWS = mul(m, float4(positionOS, 1)).xyz;
            positionWS.y += _Hop * abs(wave);
            normalWS = normalize(mul((float3x3)m, normalOS));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                TransformInstance(input.instanceID, input.positionOS.xyz, input.normalOS, output.positionWS, output.normalWS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = mainLight.color * (NdotL * mainLight.shadowAttenuation) + SampleSH(normalWS);

                half3 color = MixFog(_BaseColor.rgb * lighting, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            float4 vert(Attributes input) : SV_POSITION
            {
                float3 positionWS;
                float3 normalWS;
                TransformInstance(input.instanceID, input.positionOS.xyz, input.normalOS, positionWS, normalWS);

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            float4 vert(Attributes input) : SV_POSITION
            {
                float3 positionWS;
                float3 normalWS;
                TransformInstance(input.instanceID, input.positionOS.xyz, input.normalOS, positionWS, normalWS);
                return TransformWorldToHClip(positionWS);
            }

            half frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
