Shader "Custom/TerrainHeightUnlit"
{
    Properties
    {
        [NoScaleOffset] _HeightColorMap ("Height Color Map", 2D) = "white" {}
        [HideInInspector] _GridEnabled ("Grid Enabled", Float) = 1
        [HideInInspector] _GridCells ("Grid Cells", Vector) = (200, 200, 0, 0)
        [HideInInspector] _GridEvery ("Grid Every Nth Cell", Float) = 8
        [HideInInspector] _GridColor ("Grid Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _GridAlpha ("Grid Alpha", Range(0, 1)) = 0.35
        [HideInInspector] _GridLineWidth ("Grid Line Width", Float) = 1
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
            half _GridEnabled;
            float4 _GridCells;
            float _GridEvery;
            fixed4 _GridColor;
            half _GridAlpha;
            float _GridLineWidth;

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

                float2 gridDivisions = max(_GridCells.xy / max(_GridEvery, 1.0), 1.0);
                float2 gridPosition = input.uv * gridDivisions;
                float2 distanceToLine = min(frac(gridPosition), 1.0 - frac(gridPosition));
                float2 antialiasWidth = max(fwidth(gridPosition) * _GridLineWidth, 0.0001);
                float2 lineMask = 1.0 - smoothstep(0.0, antialiasWidth, distanceToLine);
                float gridMask = max(lineMask.x, lineMask.y)
                    * saturate(_GridEnabled)
                    * saturate(_GridAlpha)
                    * _GridColor.a;
                color.rgb = lerp(color.rgb, _GridColor.rgb, saturate(gridMask));

                UNITY_APPLY_FOG(input.fogCoord, color);
                return fixed4(color.rgb, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
