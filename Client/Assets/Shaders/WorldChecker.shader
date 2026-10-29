// 월드 좌표 기준 체커. 오브젝트 스케일/UV와 무관하게 _CellSize(기본 1 unit) 단위로 두 색이 번갈아 나온다.
// 면의 방향(노멀)에 따라 바닥은 XZ, 벽은 YZ 또는 XY 평면으로 투영한다.
Shader "aisilol/WorldChecker"
{
    Properties
    {
        _ColorA ("Color A", Color) = (0.8, 0.8, 0.8, 1)
        _ColorB ("Color B", Color) = (0.45, 0.45, 0.45, 1)
        _CellSize ("Cell Size", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _ColorA;
            half4 _ColorB;
            float _CellSize;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
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
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);

                Varyings output;
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float3 axis = abs(normalWS);
                float3 position = input.positionWS / _CellSize;

                float2 uv;
                if (axis.y >= axis.x && axis.y >= axis.z)
                    uv = position.xz;
                else if (axis.x >= axis.z)
                    uv = position.yz;
                else
                    uv = position.xy;

                float2 cell = floor(uv);
                half checker = frac((cell.x + cell.y) * 0.5) * 2.0; // 음수 좌표에서도 0/1 이 번갈아 나오도록 frac 사용
                half3 albedo = lerp(_ColorA.rgb, _ColorB.rgb, checker);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = mainLight.color * (NdotL * mainLight.shadowAttenuation) + SampleSH(normalWS);

                half3 color = MixFog(albedo * lighting, input.fogFactor);
                return half4(color, 1);
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
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
