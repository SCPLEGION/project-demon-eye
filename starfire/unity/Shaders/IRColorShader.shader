Shader "Starfire/IRColorShader"
{
    Properties
    {
        _MainTex        ("Passthrough Texture", 2D)    = "white" {}
        _EdgeIntensity  ("Edge Intensity",      Range(0,1)) = 0.8
        _ColorIntensity ("Color Intensity",     Range(0,1)) = 1.0
        _EdgeColor      ("Edge Color",          Color)  = (0.0, 1.0, 0.4, 1.0)
        _EdgeThreshold  ("Edge Threshold",      Range(0,1)) = 0.15
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Cull Off
        ZWrite Off
        Lighting Off

        Pass
        {
            Name "IRColorAndEdge"
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);  SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float  _EdgeIntensity;
            float  _ColorIntensity;
            float  _EdgeThreshold;
            float4 _EdgeColor;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

            float3 Thermal(float b)
            {
                // 0.00–0.20 deep purple/blue (#1a0033 → #0033aa)
                // 0.20–0.50 green             (#003311 → #00ff66)
                // 0.50–0.80 amber/orange      (#ff6600 → #ffaa00)
                // 0.80–1.00 white/hot         (#ffaa00 → #ffffff)
                float3 c0 = float3(0.102, 0.000, 0.200); // #1a0033
                float3 c1 = float3(0.000, 0.200, 0.667); // #0033aa
                float3 c2 = float3(0.000, 0.200, 0.067); // #003311
                float3 c3 = float3(0.000, 1.000, 0.400); // #00ff66
                float3 c4 = float3(1.000, 0.400, 0.000); // #ff6600
                float3 c5 = float3(1.000, 0.667, 0.000); // #ffaa00
                float3 c6 = float3(1.000, 1.000, 1.000); // #ffffff

                float3 col;
                if (b < 0.20)
                    col = lerp(c0, c1, smoothstep(0.00, 0.20, b));
                else if (b < 0.50)
                    col = lerp(c2, c3, smoothstep(0.20, 0.50, b));
                else if (b < 0.80)
                    col = lerp(c4, c5, smoothstep(0.50, 0.80, b));
                else
                    col = lerp(c5, c6, smoothstep(0.80, 1.00, b));
                return col;
            }

            float Sobel(float2 uv, float2 px)
            {
                float tl = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(-px.x,  px.y)).rgb);
                float t  = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2( 0.0,   px.y)).rgb);
                float tr = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2( px.x,  px.y)).rgb);
                float l  = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(-px.x, 0.0  )).rgb);
                float r  = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2( px.x, 0.0  )).rgb);
                float bl = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(-px.x,-px.y)).rgb);
                float b  = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2( 0.0, -px.y)).rgb);
                float br = Luma(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2( px.x,-px.y)).rgb);
                float gx = -tl - 2.0 * l - bl + tr + 2.0 * r + br;
                float gy = -tl - 2.0 * t  - tr + bl + 2.0 * b + br;
                return saturate(sqrt(gx * gx + gy * gy));
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float3 src = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).rgb;
                float  b   = Luma(src);
                float3 col = Thermal(b) * _ColorIntensity;

                float edge = Sobel(IN.uv, _MainTex_TexelSize.xy);
                edge = step(_EdgeThreshold, edge) * edge;
                col += _EdgeColor.rgb * edge * _EdgeIntensity;
                return half4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
