using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural texture / sprite factory. Everything the game draws is generated
/// here at runtime with Texture2D — no imported art. All sprites are built with a
/// centered pivot and a pixels-per-unit chosen so that a localScale of 1 == 1 world unit.
/// </summary>
public static class VisualUtils
{

    // Every sprite here is an immutable generated asset, so identical requests must share one
    // instance. Without this the project built ELEVEN separate copies of the radial glow alone —
    // eleven 128x128 RGBA textures plus eleven Sprites, all pixel-identical.
    private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>(16);

    private static Sprite Cached(string key, Func<Sprite> build)
    {
        // The null test also catches a Unity object destroyed under us (editor domain reload), so
        // a stale entry is rebuilt rather than handed back as a broken reference.
        if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;
        var made = build();
        _cache[key] = made;
        return made;
    }

    public static Sprite RadialGlow(int size = 128) => Cached("RadialGlow" + size, () => BuildRadialGlow(size));
    public static Sprite Ring(int size = 256, float thickness = 0.06f) => Cached("Ring" + size + "," + thickness, () => BuildRing(size, thickness));
    public static Sprite Disc(int size = 64) => Cached("Disc" + size, () => BuildDisc(size));
    public static Sprite Vignette(int size = 256) => Cached("Vignette" + size, () => BuildVignette(size));
    public static Sprite HollowRing(int size = 256, float radiusFrac = 0.86f, float thickness = 0.07f) => Cached("HollowRing" + size + "," + radiusFrac + "," + thickness, () => BuildHollowRing(size, radiusFrac, thickness));
    public static Sprite RoundedRect(int size = 64, int radius = 20) => Cached("RoundedRect" + size + "," + radius, () => BuildRoundedRect(size, radius));
    public static Sprite CornerBrackets(int size = 64, int arm = 22, float thickness = 4f) => Cached("CornerBrackets" + size + "," + arm + "," + thickness, () => BuildCornerBrackets(size, arm, thickness));
    public static Sprite Check(int size = 48, float thickness = 5f) => Cached("Check" + size + "," + thickness, () => BuildCheck(size, thickness));
    public static Sprite Gear(int size = 96) => Cached("Gear" + size, () => BuildGear(size));
    public static Sprite PingStar(int size = 128) => Cached("PingStar" + size, () => BuildPingStar(size));
    public static Sprite SonarIcon(int size = 128) => Cached("SonarIcon" + size, () => BuildSonarIcon(size));

    /// <summary>Soft radial glow: bright at the center, fading to transparent at the edge.</summary>
    private static Sprite BuildRadialGlow(int size = 128)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c; // 0 center -> 1 edge
            float a = Mathf.Clamp01(1f - d);
            a = a * a;                       // tighten the falloff for a hotter core
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>Thin glowing annulus, used for the expanding ping ring. 1 unit diameter at scale 1.</summary>
    private static Sprite BuildRing(int size = 256, float thickness = 0.06f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c; // 0..1, ring lives near 0.5
            float edge = Mathf.Abs(d - 0.5f);                                // distance from the ring line
            float a = Mathf.Clamp01(1f - edge / thickness);
            a = a * a;
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>Solid soft-edged disc, used for UI ping dots.</summary>
    private static Sprite BuildDisc(int size = 64)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
            float a = Mathf.Clamp01((1f - d) * 6f); // solid disc with a 1px soft edge
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>
    /// Vignette: transparent in the center, opaque near-black toward the edges.
    /// Rendered on top (alpha blended) to darken the screen borders.
    /// </summary>
    private static Sprite BuildVignette(int size = 256)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Use the larger axis distance so corners darken like a real vignette.
            float dx = (x - c) / c;
            float dy = (y - c) / c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d - 0.55f) / 0.6f)) * 0.9f;
            px[y * size + x] = new Color(0.01f, 0.015f, 0.03f, a);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>
    /// Crisp hollow-circle OUTLINE near the sprite edge (for the decoy highlight). Because the
    /// ring sits at ~0.86 of the radius, a localScale of S gives a circle ~0.86*S world units
    /// across — so scaling it a bit above the ball's size draws the ring cleanly AROUND the ball.
    /// </summary>
    /// <summary>
    /// The reveal icon: a source dot with two arcs radiating from it, like a wifi glyph rotated to
    /// sweep rightward.
    ///
    /// Replaces the plain dot, which said nothing — a filled circle next to a number reads as a
    /// generic counter and gave no clue it meant "pings you can still fire". Arcs leaving a point
    /// is the one shape everyone already parses as an emitted signal, and it matches the sonar
    /// rings the game draws in the maze.
    /// </summary>
    private static Sprite BuildSonarIcon(int size = 128)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];

        // Emitter sits left-of-centre so the arcs have room to open to the right.
        float ox = size * 0.24f, oy = (size - 1) * 0.5f;
        float unit = size * 0.5f;
        float dotR = size * 0.085f;
        float stroke = size * 0.062f;
        float[] arcs = { size * 0.30f, size * 0.50f };   // two sweeps

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - ox, dy = y - oy;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = 0f;

            // Solid emitter dot.
            a = Mathf.Max(a, Mathf.Clamp01((dotR - d) / (unit * 0.05f)));

            // Arcs, but only the right-hand ~120 degrees of each — a full ring would just read
            // as a target reticle, which is what the exit marker already uses.
            if (dx > -unit * 0.04f)
            {
                float cosA = d > 0.001f ? dx / d : 1f;      // 1 straight right, 0 straight up
                float wedge = Mathf.Clamp01((cosA - 0.34f) / 0.22f);
                if (wedge > 0f)
                {
                    for (int i = 0; i < arcs.Length; i++)
                    {
                        float band = Mathf.Abs(d - arcs[i]);
                        a = Mathf.Max(a, Mathf.Clamp01(1f - band / (stroke * 0.5f)) * wedge);
                    }
                }
            }

            px[y * size + x] = new Color(1f, 1f, 1f, a * a);   // squared = crisper edge
        }

        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static Sprite BuildHollowRing(int size = 256, float radiusFrac = 0.86f, float thickness = 0.07f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);   // 0 center .. 1 at edge midpoints
            float ring = Mathf.Abs(d - radiusFrac);    // distance from the ring line
            float a = Mathf.Clamp01(1f - ring / thickness);
            a *= a;                                    // slightly crisp edge
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>
    /// Rounded rectangle for UI buttons/panels, returned as a 9-sliced sprite so it scales to any
    /// size without distorting the corners. Use with Image.type = Sliced.
    /// </summary>
    private static Sprite BuildRoundedRect(int size = 64, int radius = 20)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Distance outside the rounded-rect body, measured only in the corner regions.
            float dx = Mathf.Max(radius - x, x - (size - 1 - radius), 0f);
            float dy = Mathf.Max(radius - y, y - (size - 1 - radius), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(radius - d + 0.5f);   // 1 inside, soft 1px edge
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size,
                             0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
    }

    /// <summary>
    /// Corner brackets — four L-shaped arms, 9-sliced so the arms keep a constant size at any
    /// button dimension while the edges between them stay empty. This is the game's button frame:
    /// a targeting-reticle corner treatment rather than a closed border.
    /// </summary>
    private static Sprite BuildCornerBrackets(int size = 64, int arm = 22, float thickness = 4f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = Mathf.Min(x, size - 1 - x);   // distance to nearest vertical edge
            float dy = Mathf.Min(y, size - 1 - y);   // distance to nearest horizontal edge
            // An arm runs along one edge, but only within `arm` of a corner.
            bool inArm = (dx < thickness && dy < arm) || (dy < thickness && dx < arm);
            float a = inArm ? Mathf.Clamp01(Mathf.Min(thickness - Mathf.Min(dx, dy), 1f) + 0.5f) : 0f;
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size,
                             0, SpriteMeshType.FullRect, new Vector4(arm, arm, arm, arm));
    }

    /// <summary>
    /// Checkmark glyph, drawn as two thick strokes. Deliberately NOT the Unicode ✓ — that
    /// codepoint isn't in Chakra Petch, so TMP falls back to the missing-glyph box.
    /// </summary>
    private static Sprite BuildCheck(int size = 48, float thickness = 5f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        // Short down-stroke into the elbow, then the long up-stroke (texture y points up).
        Vector2 a = new Vector2(0.16f, 0.56f) * size;
        Vector2 b = new Vector2(0.40f, 0.28f) * size;
        Vector2 c = new Vector2(0.86f, 0.80f) * size;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float d = Mathf.Min(DistToSegment(p, a, b), DistToSegment(p, b, c));
            float al = Mathf.Clamp01(thickness * 0.5f - d + 0.5f);
            px[y * size + x] = new Color(1f, 1f, 1f, al);
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Simple gear glyph for the settings button.</summary>
    private static Sprite BuildGear(int size = 96)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        const int teeth = 8;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - c, dy = y - c;
            float r = Mathf.Sqrt(dx * dx + dy * dy) / c;      // 0..1
            float ang = Mathf.Atan2(dy, dx);
            // Outer edge wobbles between the tooth tip and root to form the cog.
            float tooth = Mathf.Cos(ang * teeth);
            float outer = 0.72f + 0.16f * Mathf.Sign(tooth);
            float a = (r < outer && r > 0.30f) ? 1f : 0f;      // ring body with a hollow centre
            a *= Mathf.Clamp01((outer - r) * 12f);             // soften the outer edge
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    /// <summary>
    /// Rating marker for the level-clear panel: a four-point sonar "ping" star — a sharp
    /// concave-sided diamond inside a thin ring. Reads as instrumentation rather than the
    /// cartoon five-point gold star it replaces, matching the game's sonar-HUD language.
    /// </summary>
    private static Sprite BuildPingStar(int size = 128)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;      // -1..1
            float ax = Mathf.Abs(dx), ay = Mathf.Abs(dy);
            float r = Mathf.Sqrt(dx * dx + dy * dy);

            // Four-point star: a diamond whose edges bow inward, giving sharp spikes on the axes.
            float diamond = ax + ay;                       // 1 on a straight-edged diamond
            float pinch = 1f - 0.55f * (ax * ay) * 4f;     // pull the edges toward the centre
            float body = Mathf.Clamp01((pinch - diamond) * 5f + 0.5f);

            // Thin outer ring, like the ping rings in-world.
            float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.93f) / 0.05f);

            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(Mathf.Max(body, ring * 0.85f)));
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);   // drop the CPU-side copy: halves what each texture costs
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

}
