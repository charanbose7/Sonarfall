// Additive.shader
// Generic unlit additive shader for glowing elements: player dot, exit glow,
// and the sonar ping ring. Samples _MainTex, tints by _Color and the per-vertex
// color (SpriteRenderer / LineRenderer feed tint through vertex color), then
// premultiplies alpha so transparent texels add nothing.
//
// No LightMode tag -> renders as SRPDefaultUnlit under URP and unlit under Built-in.
Shader "Sonarfall/Additive"
{
    Properties
    {
        // [PerRendererData] is not cosmetic. It tells the sprite batcher the texture comes from each
        // SpriteRenderer's own sprite, not from the material. Without it, every sprite sharing a
        // material was batched under ONE texture — decoys, the exit reticle and the bonus echo all
        // drew with whichever sprite happened to lead the batch (solid squares, outlines, rings).
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color   ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Blend One One      // additive glow
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _SonarGain;   // BRIGHTNESS setting, shared with the walls; floored at 1

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };
            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Color * i.color;
                // Premultiply so fully transparent texels contribute nothing to
                // the additive blend (avoids square halos around round sprites).
                float gain = max(_SonarGain, 1.0);
                return fixed4(c.rgb * c.a * gain, c.a);
            }
            ENDCG
        }
    }
}
