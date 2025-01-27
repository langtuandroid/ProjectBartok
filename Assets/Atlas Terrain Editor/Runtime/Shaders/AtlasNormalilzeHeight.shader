Shader "Hidden/Atlas/AtlasNormalizeHeight"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _MaxHeight;
            float _MinHeight;

            float InverseLerp(float4 A, float4 B, float4 T)
            {
                return (T - A) / (B - A);
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {

                float4 color = tex2D(_MainTex, i.uv);

                return float4(InverseLerp(_MinHeight,_MaxHeight,color.r), InverseLerp(_MinHeight, _MaxHeight, color.g), InverseLerp(_MinHeight, _MaxHeight, color.b), InverseLerp(_MinHeight, _MaxHeight, color.a));

            }

            ENDCG
        }
    }
}
