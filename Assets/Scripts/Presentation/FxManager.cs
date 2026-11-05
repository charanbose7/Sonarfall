using UnityEngine;

/// <summary>
/// Owns the code-built ParticleSystems: marine snow drifting down through the water (so the dark
/// screen never feels dead), the exit-clear burst, and the wall-slide sparks — plus the depth
/// gradient behind everything. All GPU-batched, all created from code, no imported assets.
///
/// The backdrop used to be a twinkling starfield, while every sector was named for the sea. One
/// had to go, and the sea is the one that explains the sonar: the field is now marine snow sinking
/// through a water column that is faintly lit from above and black at the bottom.
/// </summary>
public class FxManager : MonoBehaviour
{
    private Camera _cam;
    private ParticleSystem _dust;
    private ParticleSystem _burst;
    private ParticleSystem _slide;
    private Transform _backdrop;

    public void Init(Camera cam)
    {
        _cam = cam;

        var mat = new Material(Shader.Find("Sonarfall/Additive")) { name = "ParticleMat" };
        mat.mainTexture = VisualUtils.RadialGlow(64).texture;

        _dust  = CreateDust(mat);
        _burst = CreateBurst(mat);
        _slide = CreateSlide(mat);
        _backdrop = CreateBackdrop(cam);
    }

    /// <summary>
    /// The water column. A child of the camera so it can never be panned or shaken off screen, far
    /// behind the maze and first in the sort, alpha-blended on the default sprite material (it has
    /// to replace the clear colour, which additive could never do).
    /// </summary>
    private static Transform CreateBackdrop(Camera cam)
    {
        var go = new GameObject("DepthBackdrop");
        go.transform.SetParent(cam.transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, 30f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = VisualUtils.DepthGradient();
        sr.sortingOrder = -100;
        return go.transform;
    }

    /// <summary>Celebration puff from the exit on level clear.</summary>
    public void PlayExitBurst(Vector2 pos)
    {
        if (_burst == null) return;
        var m = _burst.main; m.startColor = GameConfig.ExitColor;
        _burst.transform.position = new Vector3(pos.x, pos.y, 0f);
        _burst.Emit(46);
    }

    /// <summary>Warm puff when the player touches a decoy — "that's not the exit".</summary>
    public void PlayDecoyPop(Vector2 pos)
    {
        if (_burst == null) return;
        var m = _burst.main; m.startColor = GameConfig.DecoyColor;
        _burst.transform.position = new Vector3(pos.x, pos.y, 0f);
        _burst.Emit(24);
        m.startColor = GameConfig.ExitColor; // restore default for the next exit burst
    }

    /// <summary>A coloured puff anywhere — a hunter's bite, a gate, a current's mouth.</summary>
    public void PlayBurst(Vector2 pos, Color c, int count)
    {
        if (_burst == null) return;
        var m = _burst.main; m.startColor = c;
        _burst.transform.position = new Vector3(pos.x, pos.y, 0f);
        _burst.Emit(count);
        m.startColor = GameConfig.ExitColor;
    }

    /// <summary>A few sparks where the dot scrapes a wall.</summary>
    public void PlaySlide(Vector2 pos)
    {
        if (_slide == null) return;
        _slide.transform.position = new Vector3(pos.x, pos.y, 0f);
        _slide.Emit(3);
    }

    private float _dustW = -1f, _dustH = -1f;

    private void LateUpdate()
    {
        if (_dust == null || _cam == null) return;
        // Keep the dust field covering the current view (no per-frame allocation).
        float h = _cam.orthographicSize * 2f;
        float w = h * _cam.aspect;
        var t = _dust.transform;
        var cp = _cam.transform.position;
        t.position = new Vector3(cp.x, cp.y, 0f);
        // The shape module is a native round-trip per write; the view only changes size during a
        // punch-zoom, so skip it the other ~99% of frames.
        if (w != _dustW || h != _dustH)
        {
            _dustW = w; _dustH = h;
            var sh = _dust.shape;
            sh.scale = new Vector3(w, h, 1f);
            // Overscanned a little so the shake and punch-zoom never show its edge.
            if (_backdrop != null)
                _backdrop.localScale = new Vector3(w * 1.2f / VisualUtils.DepthGradientWidth, h * 1.2f, 1f);
        }
    }

    /// <summary>
    /// Marine snow: soft motes sinking slowly and swaying, rather than stars twinkling in place.
    /// Fewer and dimmer than the old field — it is texture behind the maze, not something to read.
    /// </summary>
    private ParticleSystem CreateDust(Material mat)
    {
        var go = new GameObject("MarineSnow");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = 9f;
        main.startSpeed = 0.02f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.018f, 0.10f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.62f, 0.86f, 0.92f, 0.55f),
                                                            new Color(0.80f, 0.95f, 1.00f, 0.85f));
        main.maxParticles = 300;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0f;

        // Sinking, every mote at its own rate...
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.x = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);
        vel.y = new ParticleSystem.MinMaxCurve(-0.16f, -0.05f);
        vel.z = new ParticleSystem.MinMaxCurve(0f, 0f);
        // ...and swaying a little on the way down, which is what sells it as water.
        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.05f;
        noise.frequency = 0.35f;
        noise.scrollSpeed = 0.1f;
        noise.quality = ParticleSystemNoiseQuality.Low;

        var em = ps.emission; em.enabled = true; em.rateOverTime = 26f;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(10, 10, 1);
        SetFadeInOut(ps, 0.3f);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = mat; r.sortingOrder = 5;
        ps.Play();
        return ps;
    }

    private ParticleSystem CreateBurst(Material mat)
    {
        var go = new GameObject("ExitBurst");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.75f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
        main.startColor = GameConfig.ExitColor;
        main.maxParticles = 200;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0f;

        var em = ps.emission; em.enabled = false;               // manual Emit()
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.05f;
        SetFadeOut(ps);
        SetShrink(ps);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = mat; r.sortingOrder = 60;
        return ps;
    }

    private ParticleSystem CreateSlide(Material mat)
    {
        var go = new GameObject("SlideSparks");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
        main.startColor = new Color(0.7f, 0.9f, 1f, 0.9f);
        main.maxParticles = 100;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.08f;
        SetFadeOut(ps);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = mat; r.sortingOrder = 55;
        return ps;
    }

    // ---- module helpers ----
    private static void SetFadeOut(ParticleSystem ps)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    private static void SetFadeInOut(ParticleSystem ps, float mid)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, mid), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    private static void SetShrink(ParticleSystem ps)
    {
        var sol = ps.sizeOverLifetime; sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 0f)));
    }
}
