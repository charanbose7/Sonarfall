// SonarWall.shader
// Unlit, additive shader applied to every maze wall (all walls share one combined
// mesh, so this runs once over all of them).
//
// Walls are drawn PURELY by the sonar. Where nothing has revealed a pixel it returns
// black (0); under additive blend (One One) black contributes nothing, so unrevealed
// walls are invisible against the dark background.
//
// The reveal has TWO parts so a wall "blooms" rather than just appearing:
//   1) A bright WHITE FLASH in a thin band right at the expanding ring front.
//   2) A BLUE GLOW left behind that fades out over _SonarFade seconds.
//
// SonarManager.cs pushes global state every frame:
//   _SonarPings[i] = (originX, originY, emitTime, activeFlag)
//   _SonarTime     = current time (same clock as emitTime)
//   _SonarSpeed    = ring expansion speed (world units / second)
//   _SonarFade     = seconds for the blue glow to fade back to black
//   _SonarBand     = seconds-wide window of the white front flash (thinner = harder to read)
//   _SonarFlash    = strength multiplier of the white front flash
//   _SonarGain     = BRIGHTNESS setting (1 = normal). Floored at 1, so an unset global can't black
//                    the walls out.
//   _StillZones[i] = (centreX, centreY, radius, activeFlag) — STILLWATER patches. A ping reveals
//                    nothing inside one; the only light there is the player's own (_PlayerLight).
//   _PlayerLight   = (x, y, radius, strength)
//
// No LightMode tag -> URP renders this as "SRPDefaultUnlit"; Built-in renders it unlit.
Shader "Sonarfall/SonarWall"
{
    Properties
    {
        _Color ("Wall Glow Color", Color) = (0.55, 0.8, 1.0, 1.0)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Blend One One      // additive -> unrevealed (black) pixels are invisible
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            #define MAX_PINGS 4
            #define MAX_STILL 4

            // ---- Global sonar state (set from C# via Shader.SetGlobalXxx) ----
            float4 _SonarPings[MAX_PINGS];
            float  _SonarTime;
            float  _SonarSpeed;
            float  _SonarFade;
            float  _SonarBand;
            float  _SonarFlash;
            float  _SonarGain;
            float4 _StillZones[MAX_STILL];
            float4 _PlayerLight;

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 world : TEXCOORD0; // world-space XY, needed for distance-to-ping
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // The combined wall mesh is authored in world space at identity transform,
                // so this is simply the pixel's world XY.
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float glow = 0.0;   // settling blue reveal
                float flash = 0.0;  // white bloom at the ring front

                [unroll]
                for (int p = 0; p < MAX_PINGS; p++)
                {
                    float4 ping = _SonarPings[p];
                    if (ping.w < 0.5) continue;                 // unused slot

                    // Distance from this wall pixel to where the ping was emitted.
                    float dist = distance(i.world, ping.xy);

                    // Radius of the ring front right now, and how long ago (seconds) the
                    // front crossed THIS pixel's distance.
                    float ringRadius = (_SonarTime - ping.z) * _SonarSpeed;
                    float timeSincePassed = (ringRadius - dist) / _SonarSpeed;

                    float reached = step(0.0, timeSincePassed); // 0 until the front arrives

                    // (1) Blue glow: 1 right as the front passes, fading over _SonarFade.
                    float g = saturate(1.0 - timeSincePassed / _SonarFade) * reached;
                    glow = max(glow, g);

                    // (2) White flash: a short pulse confined to _SonarBand seconds after
                    //     the front passes. Squared for a sharp, punchy bloom.
                    float f = saturate(1.0 - timeSincePassed / _SonarBand) * reached;
                    f = f * f;
                    flash = max(flash, f);
                }

                // Still water swallows the echo. The soft 0.35 rim is deliberate: a hard cut would
                // read as a rendering bug, a fade reads as murk.
                float still = 0.0;
                [unroll]
                for (int z = 0; z < MAX_STILL; z++)
                {
                    float4 sz = _StillZones[z];
                    if (sz.w < 0.5) continue;
                    float d = distance(i.world, sz.xy);
                    still = max(still, 1.0 - smoothstep(sz.z - 0.35, sz.z, d));
                }
                glow  *= 1.0 - still;
                flash *= 1.0 - still;

                // ...and inside it you see only what is within arm's reach of your own glow.
                float reach = saturate(1.0 - distance(i.world, _PlayerLight.xy) / max(_PlayerLight.z, 0.001));
                glow = max(glow, still * reach * _PlayerLight.w);

                // Blue base + additive white bloom on top -> "blooms white, settles blue".
                float gain = max(_SonarGain, 1.0);
                float3 rgb = (_Color.rgb * glow + float3(1.0, 1.0, 1.0) * flash * _SonarFlash) * gain;
                float a = max(glow, flash);
                return fixed4(rgb, a); // additive blend uses rgb; a kept for non-additive setups
            }
            ENDCG
        }
    }
}
