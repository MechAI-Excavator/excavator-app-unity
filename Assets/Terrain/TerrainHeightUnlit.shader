Shader "Custom/TerrainHeightUnlit"
{
    Properties
    {
        [NoScaleOffset] _HeightColorMap ("Height Color Map", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "TerrainCompatible" = "True"
        }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Cull Back
            ZWrite On

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            sampler2D _HeightColorMap;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.uv = input.uv;
                UNITY_TRANSFER_FOG(output, output.positionCS);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                fixed4 color = tex2D(_HeightColorMap, input.uv);
                UNITY_APPLY_FOG(input.fogCoord, color);
                return fixed4(color.rgb, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
