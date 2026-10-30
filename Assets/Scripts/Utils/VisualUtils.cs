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

    // ---- Object silhouettes ----
    // Colour alone could not carry the game's objects. With deuteranopia the orange decoy and the
    // green exit both land on the same khaki; with protanopia the decoy reads as the gold bonus
    // echo — the thing that costs a hull plate looks like the thing that gives one back. Every
    // object now has its own outline and its own motion, so colour is a second cue, never the only one.
    public static Sprite HullPlate(int size = 128) => Cached("HullPlate" + size, () => BuildHullPlate(size));
    public static Sprite Lure(int size = 128) => Cached("Lure" + size, () => BuildLure(size));
    public static Sprite Jaws(int size = 256) => Cached("Jaws" + size, () => BuildJaws(size));
    public static Sprite DashedRing(int size = 256, int dashes = 6) => Cached("DashedRing" + size + "," + dashes, () => BuildDashedRing(size, dashes));
    public static Sprite Crystal(int size = 128) => Cached("Crystal" + size, () => BuildCrystal(size));
    public static Sprite HunterEyes(int size = 128) => Cached("HunterEyes" + size, () => BuildHunterEyes(size));
    public static Sprite Chevron(int size = 64) => Cached("Chevron" + size, () => BuildChevron(size));
    public static Sprite Pixel() => Cached("Pixel", BuildPixel);
    public static Sprite SoftDisc(int size = 128) => Cached("SoftDisc" + size, () => BuildSoftDisc(size));
    public static Sprite DepthGradient() => Cached("DepthGradient", BuildDepthGradient);

    /// <summary>Shared tail for every builder below: upload, drop the CPU copy, wrap in a sprite.</summary>
    private static Sprite Finish(Texture2D tex, Color[] px, int size)
    {
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);
        // FullRect, not the default tight mesh. Unity's outline tracer mangles shapes made of
        // separate islands (the six dashes of the exit reticle rendered as nothing at all), and a
        // quad per sprite costs nothing at these sizes.
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size, 0, SpriteMeshType.FullRect);
    }

    /// <summary>
    /// One hull plate: a hexagonal armour panel with a bright rim. Replaced the star row, which
    /// players read as a score they were losing. Nothing else in the game is a hexagon, so it reads
    /// as "a part of you" rather than as a pickup or a rating.
    /// </summary>
    private static Sprite BuildHullPlate(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float ax = Mathf.Abs((x - c) / c), ay = Mathf.Abs((y - c) / c);
            // Flat-topped hexagon: inradius 0.80 on the vertical, vertices left and right.
            float d = Mathf.Max(ax * 0.866f + ay * 0.5f, ay) - 0.80f;   // < 0 inside
            float body = Mathf.Clamp01(-d * 40f + 0.5f);
            float rim  = Mathf.Clamp01(1f - Mathf.Abs(d + 0.07f) / 0.06f);
            // A single seam across the middle: the detail that makes it a panel, not a gem.
            float seam = ay < 0.035f && d < -0.12f ? 0.35f : 0f;
            float a = Mathf.Max(body * 0.50f, Mathf.Max(rim, seam * body));
            px[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        return Finish(tex, px, size);
    }

    /// <summary>
    /// The decoy, redrawn as an anglerfish's lure: a lit bulb hanging from a curved stalk. The
    /// sector names were already all ocean and the decoys were already bait — a light that looks
    /// like the way out — so this is what they always were. The bulb sits on the sprite centre,
    /// which is where the hazard actually is; the stalk trails up and over it.
    /// </summary>
    private static Sprite BuildLure(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        // Stalk: a quarter-ellipse from the bulb's crown sweeping up and back to the top-left.
        Vector2 arcC = new Vector2(-0.46f, 0.30f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float r = Mathf.Sqrt(u * u + v * v);

            float glow = Mathf.Clamp01(1f - r / 0.62f); glow *= glow;          // the bulb's light
            float core = Mathf.Clamp01((0.20f - r) / 0.05f);                     // hard lit centre

            float stalk = 0f;
            if (v > 0.18f && u < 0.06f && u > -0.95f)
            {
                float du = (u - arcC.x) / 0.48f, dv = (v - arcC.y) / 0.62f;      // ellipse through the crown
                float e = Mathf.Abs(Mathf.Sqrt(du * du + dv * dv) - 1f);
                stalk = Mathf.Clamp01(1f - e / 0.07f) * Mathf.Clamp01((0.98f - v) * 6f) * 0.75f;
            }
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(Mathf.Max(Mathf.Max(glow * 0.85f, core), stalk)));
        }
        return Finish(tex, px, size);
    }

    /// <summary>
    /// A ring of inward-pointing teeth: the anglerfish behind the lure, shown only when a ping
    /// sweeps over a decoy. It replaced a plain circle that looked exactly like the exit marker.
    /// </summary>
    private static Sprite BuildJaws(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        const int teeth = 13;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float r = Mathf.Sqrt(u * u + v * v);
            float ang = Mathf.Atan2(v, u) / (2f * Mathf.PI) + 0.5f;            // 0..1 round the ring
            float f = ang * teeth - Mathf.Floor(ang * teeth);
            float tooth = 1f - Mathf.Abs(f - 0.5f) * 2f;                        // 1 mid-tooth, 0 between
            float tip = 0.88f - 0.24f * tooth;                                  // teeth point inward
            float inTooth = (r < 0.88f && r > tip) ? Mathf.Clamp01((r - tip) * 30f) : 0f;
            float gum = Mathf.Clamp01(1f - Mathf.Abs(r - 0.90f) / 0.035f);
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(Mathf.Max(inTooth, gum)));
        }
        return Finish(tex, px, size);
    }

    /// <summary>The exit's turning reticle: a ring broken into dashes, so it reads as a target
    /// even when its colour does not.</summary>
    private static Sprite BuildDashedRing(int size, int dashes)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float r = Mathf.Sqrt(u * u + v * v);
            float band = Mathf.Clamp01(1f - Mathf.Abs(r - 0.84f) / 0.075f);
            float ang = Mathf.Atan2(v, u) / (2f * Mathf.PI) + 0.5f;
            float f = ang * dashes - Mathf.Floor(ang * dashes);
            float dash = Mathf.Clamp01((0.32f - Mathf.Abs(f - 0.5f)) * 18f);  // ~64% filled
            px[y * size + x] = new Color(1f, 1f, 1f, band * band * dash);
        }
        return Finish(tex, px, size);
    }

    /// <summary>
    /// The bonus echo as a cut crystal: a tall diamond with a bright rim and two facet lines. The
    /// only object with straight edges and corners, and it spins, where the lure sways.
    /// </summary>
    private static Sprite BuildCrystal(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float d = Mathf.Abs(u) / 0.58f + Mathf.Abs(v) / 0.96f - 1f;         // < 0 inside
            float body = Mathf.Clamp01(-d * 30f + 0.5f);
            float rim = Mathf.Clamp01(1f - Mathf.Abs(d + 0.05f) / 0.07f);
            float facet = (Mathf.Abs(u) < 0.03f || Mathf.Abs(v - 0.18f) < 0.025f) ? 0.85f : 0f;
            float a = Mathf.Max(body * 0.45f, Mathf.Max(rim, facet * body));
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
        }
        return Finish(tex, px, size);
    }

    /// <summary>
    /// The hunter: two slanted eyes and nothing else. Seen from above, facing +X — the renderer is
    /// rotated to its heading. A pair of eyes is the one shape nobody mistakes for a light to chase.
    /// </summary>
    private static Sprite BuildHunterEyes(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float a = 0f;
            for (int side = -1; side <= 1; side += 2)
            {
                // Each eye an almond, angled inward toward the snout.
                float ex = u - 0.22f, ey = v - side * 0.30f;
                float cs = Mathf.Cos(side * 0.42f), sn = Mathf.Sin(side * 0.42f);
                float rx = ex * cs + ey * sn, ry = -ex * sn + ey * cs;
                float e = (rx * rx) / (0.20f * 0.20f) + (ry * ry) / (0.075f * 0.075f);
                a = Mathf.Max(a, Mathf.Clamp01((1f - e) * 3f));
            }
            float r = Mathf.Sqrt(u * u + v * v);
            float halo = Mathf.Clamp01(1f - r / 0.95f); halo = halo * halo * 0.22f;
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Max(a, halo));
        }
        return Finish(tex, px, size);
    }

    /// <summary>A single "&gt;" pointing +X, for the flow of a current.</summary>
    private static Sprite BuildChevron(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        Vector2 a = new Vector2(0.18f, 0.86f) * size, b = new Vector2(0.70f, 0.5f) * size, d = new Vector2(0.18f, 0.14f) * size;
        float th = size * 0.13f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float dist = Mathf.Min(DistToSegment(p, a, b), DistToSegment(p, b, d));
            px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(th * 0.5f - dist + 0.5f));
        }
        return Finish(tex, px, size);
    }

    /// <summary>Solid white, one world unit square at scale 1 — gate bars are scaled from it.</summary>
    private static Sprite BuildPixel()
    {
        const int size = 8;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = Color.white;
        return Finish(tex, px, size);
    }

    /// <summary>Flat disc with a soft rim — the murk of a still-water patch.</summary>
    private static Sprite BuildSoftDisc(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float r = Mathf.Sqrt(u * u + v * v);
            px[y * size + x] = new Color(1f, 1f, 1f, 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((r - 0.72f) / 0.28f)));
        }
        return Finish(tex, px, size);
    }

    /// <summary>
    /// The water column behind the maze: faint blue-green light from the surface at the top,
    /// falling to black at the bottom. Tall and thin — it is only ever stretched, never read.
    /// The backdrop used to be a starfield, which argued with every sector name in the game.
    /// </summary>
    private static Sprite BuildDepthGradient()
    {
        const int w = 4, h = 256;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color[w * h];
        var top = new Color(0.030f, 0.085f, 0.120f, 1f);
        var mid = new Color(0.012f, 0.032f, 0.060f, 1f);
        var bot = new Color(0.004f, 0.008f, 0.020f, 1f);
        for (int y = 0; y < h; y++)
        {
            float t = y / (float)(h - 1);                    // 0 bottom .. 1 top
            Color col = t > 0.55f ? Color.Lerp(mid, top, (t - 0.55f) / 0.45f)
                                  : Color.Lerp(bot, mid, Easing.InOutSine(t / 0.55f));
            for (int x = 0; x < w; x++) px[y * w + x] = col;
        }
        tex.SetPixels(px);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply(false, true);
        // One world unit TALL at scale 1 (and w/h wide); the caller stretches it to the view.
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), h);
    }

    /// <summary>World width of <see cref="DepthGradient"/> at scale 1, for stretching it.</summary>
    public const float DepthGradientWidth = 4f / 256f;

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
