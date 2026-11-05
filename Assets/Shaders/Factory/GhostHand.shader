// Translucent "outline" hand for the controller-driven hand poses of the factory demo: the Meta
// runtime synthesises a hand pose around each held controller and the SDK's hand prefab renders
// it with this. A view-angle fresnel makes the silhouette bright and nearly opaque while the
// inside stays dark and faint, so the hand reads as a drawn outline over the controller model
// (the look of the Quest home menus). A depth-only pass first so the hand never blends with
// itself. Colours and strengths are material properties (ControllerHandGhost.mat).
Shader "Factory/GhostHand"
{
    Properties
    {
        _RimColor("Rim colour", Color) = (0.4, 0.73, 1, 1)
        _BaseColor("Inside colour", Color) = (0.05, 0.06, 0.08, 1)
        _RimPower("Rim power", Range(0.5, 8)) = 2.5
        _Alpha("Rim alpha", Range(0, 1)) = 0.85
        _InnerAlpha("Inside alpha (fraction of the rim alpha)", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        // Depth only: the shading pass below then draws just the nearest surface of the hand.
        Pass
        {
            Name "GhostHandDepth"
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionHCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }
            half4 frag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "GhostHand"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest LEqual
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _RimColor;
                half4 _BaseColor;
                half _RimPower;
                half _Alpha;
                half _InnerAlpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.viewDirWS = GetWorldSpaceViewDir(positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                half ndv = saturate(dot(normalize(IN.normalWS), normalize(IN.viewDirWS)));
                half rim = pow(1.0h - ndv, _RimPower);
                half3 color = lerp(_BaseColor.rgb, _RimColor.rgb, rim);
                half alpha = _Alpha * lerp(_InnerAlpha, 1.0h, rim);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
