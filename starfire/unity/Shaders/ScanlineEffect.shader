Shader "Starfire/ScanlineEffect"
{
    Properties
    {
        _Color      ("Tint",       Color)      = (0.0, 1.0, 0.4, 1.0)
        _Speed      ("Scroll px/s",Float)      = 30.0
        _LineHeight ("Line Height (px)", Float)= 4.0
        _ScreenH    ("Screen Height (px)", Float) = 1832.0
        _Opacity    ("Opacity",    Range(0,1)) = 0.10
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Scanlines"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _Color;
            float  _Speed;
            float  _LineHeight;
            float  _ScreenH;
            float  _Opacity;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float y_px      = IN.uv.y * max(1.0, _ScreenH);
                float scroll_px = _Speed * _Time.y;
                float band      = frac((y_px + scroll_px) / max(1.0, _LineHeight));
                float line      = step(0.5, band);  // alternating bands
                float a = _Opacity * line;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
