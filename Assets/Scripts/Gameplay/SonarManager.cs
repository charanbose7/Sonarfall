using System;
using UnityEngine;

/// <summary>
/// Drives the sonar. Keeps the last up-to-4 pings, publishes them to the wall shader as
/// global uniforms every frame, spawns a POWERFUL expanding ring + origin flash (pooled,
/// zero per-frame allocation), and fires cascading tick audio + a medium haptic when the
/// front reveals walls near the player. Tick detection is geometry-based (distance to
/// cached wall centers) — no physics queries in Update.
/// </summary>
public class SonarManager : MonoBehaviour
{
    private class Ping
    {
        public Vector2 origin;
        public float startTime;
        public bool active;
        public bool[] ticked;      // per-wall, this ping's front has passed it
        public int tickedWalls;    // how many of `ticked` are set — lets a finished ping skip its scan
        public int tickCount;
        public float lastTickTime;
    }

    private struct Pooled
    {
        public Transform tr;
        public SpriteRenderer sr;
        public bool active;
        public float startTime;
    }

    private readonly Ping[] _pings = new Ping[GameConfig.MaxPings];
    private readonly Vector4[] _gpu = new Vector4[GameConfig.MaxPings];
    private int _nextSlot;

    private ProceduralAudio _audio;
    private Transform _player;

    // Cached wall geometry for tick detection.
    private Vector2[] _wallCenters = Array.Empty<Vector2>();
    private int _wallCount;
    private float _maxDetectRadius = 100f;
    private float _lastNearHaptic;

    // Per-level tuning (set by GameManager from the Difficulty profile).
    private Color _ringColor = GameConfig.RingColor;
    private float _fade = GameConfig.FadeStart;
    private float _speed = GameConfig.RingSpeedStart;
    private float _band = GameConfig.BandStart;

    // Pools.
    private const int PoolSize = 8;
    private const int TickCap = 22;
    private Pooled[] _rings;
    private Pooled[] _flashes;
    private Transform _fxContainer;

    public void Init(ProceduralAudio audio)
    {
        _audio = audio;
        for (int i = 0; i < _pings.Length; i++)
            _pings[i] = new Ping { ticked = Array.Empty<bool>() };

        var container = new GameObject("SonarFx");
        _fxContainer = container.transform;
        _fxContainer.SetParent(transform, false);

        var mat = new Material(Shader.Find("Sonarfall/Additive")) { name = "SonarFxMat" };

        _rings = BuildPool("Ring", VisualUtils.Ring(), mat, 40);
        _flashes = BuildPool("Flash", VisualUtils.RadialGlow(), mat, 45);
    }

    private Pooled[] BuildPool(string name, Sprite sprite, Material mat, int sorting)
    {
        var pool = new Pooled[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            var go = new GameObject(name + i);
            go.transform.SetParent(_fxContainer, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = mat;
            sr.sortingOrder = sorting;
            go.SetActive(false);
            pool[i] = new Pooled { tr = go.transform, sr = sr, active = false };
        }
        return pool;
    }

    public void SetPlayer(Transform player) => _player = player;

    public void SetMazeDiagonal(float diagonal) => _maxDetectRadius = diagonal;

    /// <summary>Apply the per-level sonar tuning.</summary>
    public void ApplyProfile(Difficulty d)
    {
        _fade = d.fade;
        _speed = d.ringSpeed;
        _band = d.band;
    }

    /// <summary>
    /// Current sonar reveal intensity (0..1) at a world position, using the same timing as the
    /// wall shader. Lets decoy highlights light up exactly as the ring front sweeps over them.
    /// </summary>
    public float RevealAt(Vector2 p)
    {
        float best = 0f;
        for (int i = 0; i < _pings.Length; i++)
        {
            var ping = _pings[i];
            if (!ping.active) continue;
            float dist = Vector2.Distance(p, ping.origin);
            float ringR = (Time.time - ping.startTime) * _speed;
            float ts = (ringR - dist) / _speed;
            if (ts < 0f) continue;
            best = Mathf.Max(best, Mathf.Clamp01(1f - ts / _fade));
        }
        return best;
    }

    /// <summary>Cache wall centers for tick detection; sizes the per-ping "ticked" arrays.</summary>
    public void SetWalls(System.Collections.Generic.List<WallSegment> walls)
    {
        _wallCount = walls.Count;
        if (_wallCenters.Length < _wallCount)
            _wallCenters = new Vector2[_wallCount];
        for (int i = 0; i < _wallCount; i++) _wallCenters[i] = walls[i].center;

        for (int p = 0; p < _pings.Length; p++)
            if (_pings[p].ticked.Length < _wallCount)
                _pings[p].ticked = new bool[_wallCount];
    }

    public void ResetPings()
    {
        for (int i = 0; i < _pings.Length; i++)
        {
            _pings[i].active = false;
            _pings[i].tickCount = 0;
            if (_pings[i].ticked.Length > 0) Array.Clear(_pings[i].ticked, 0, _pings[i].ticked.Length);
            _pings[i].tickedWalls = 0;
        }
        _nextSlot = 0;
        for (int i = 0; i < PoolSize; i++)
        {
            Deactivate(ref _rings[i]);
            Deactivate(ref _flashes[i]);
        }
        PushGlobals();
    }

    public void EmitPing(Vector2 origin)
    {
        var p = _pings[_nextSlot];
        p.origin = origin;
        p.startTime = Time.time;
        p.active = true;
        p.tickCount = 0;
        if (p.ticked.Length > 0) Array.Clear(p.ticked, 0, p.ticked.Length);
        p.tickedWalls = 0;
        _nextSlot = (_nextSlot + 1) % _pings.Length;

        Spawn(_rings, origin, _ringColor);
        Spawn(_flashes, origin, Color.white);

        if (_audio != null) _audio.PlayPing(1f + UnityEngine.Random.Range(-GameConfig.PingPitchJitter, GameConfig.PingPitchJitter));
        Haptics.Light();
    }

    /// <summary>Per-sector ring tint.</summary>
    public void SetRingColor(Color c) => _ringColor = c;

    /// <summary>
    /// Flood the whole maze with light and hold it — the "you were THIS close" moment on a fail.
    /// Uses a very fast, very slow-fading sweep; the normal per-level tuning is restored by the
    /// next ApplyProfile() call when the level rebuilds.
    /// </summary>
    public void RevealAll(Vector2 origin, float holdSeconds)
    {
        _speed = 60f;
        _fade = holdSeconds;

        var p = _pings[_nextSlot];
        p.origin = origin;
        p.startTime = Time.time;
        p.active = true;
        p.tickCount = int.MaxValue; // suppress the cascading tick audio for this sweep
        if (p.ticked.Length > 0) Array.Clear(p.ticked, 0, p.ticked.Length);
        p.tickedWalls = 0;
        _nextSlot = (_nextSlot + 1) % _pings.Length;
    }

    private void Update()
    {
        // Nothing here is valid until Init() has built the ping slots and pools. In a build that
        // always happens first, but an editor domain reload during play wipes these (Ping is a
        // plain private class, so hot-reload restores the array with null entries) and every
        // frame then throws. Cheap guard, and it keeps the console honest while iterating.
        if (_pings[0] == null || _rings == null) return;

        PushGlobals();
        UpdateRings();
        UpdateFlashes();
        DetectTicks();
    }

    // Shader property IDs, resolved once. The string overloads of SetGlobal* hash the name on every
    // call, and these six run every frame for the life of the app — the integer overloads skip that
    // entirely. Standard Unity practice and free.
    private static readonly int IdSonarPings = Shader.PropertyToID("_SonarPings");
    private static readonly int IdSonarTime  = Shader.PropertyToID("_SonarTime");
    private static readonly int IdSonarSpeed = Shader.PropertyToID("_SonarSpeed");
    private static readonly int IdSonarFade  = Shader.PropertyToID("_SonarFade");
    private static readonly int IdSonarBand  = Shader.PropertyToID("_SonarBand");
    private static readonly int IdSonarFlash = Shader.PropertyToID("_SonarFlash");

    private void PushGlobals()
    {
        float now = Time.time;                       // one call, not one per ping
        for (int i = 0; i < _pings.Length; i++)
        {
            var p = _pings[i];
            _gpu[i] = p.active ? new Vector4(p.origin.x, p.origin.y, p.startTime, 1f) : Vector4.zero;
        }
        Shader.SetGlobalVectorArray(IdSonarPings, _gpu);
        Shader.SetGlobalFloat(IdSonarTime,  now);
        Shader.SetGlobalFloat(IdSonarSpeed, _speed);
        Shader.SetGlobalFloat(IdSonarFade,  _fade);
        Shader.SetGlobalFloat(IdSonarBand,  _band);
        Shader.SetGlobalFloat(IdSonarFlash, GameConfig.FlashBoost);
    }

    private void UpdateRings()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (!_rings[i].active) continue;
            float age = Time.time - _rings[i].startTime;
            if (age >= GameConfig.RingLife) { Deactivate(ref _rings[i]); continue; }

            float radius = age * _speed;
            _rings[i].tr.localScale = Vector3.one * (radius * 2f);
            // Bright at birth, easing out -> feels like a shockwave, not a fade.
            float a = 1f - Easing.OutQuad(age / GameConfig.RingLife);
            var c = _ringColor; c.a = 1f;
            _rings[i].sr.color = c * (a * 1.4f);
        }
    }

    private void UpdateFlashes()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (!_flashes[i].active) continue;
            float age = Time.time - _flashes[i].startTime;
            if (age >= GameConfig.OriginFlashTime) { Deactivate(ref _flashes[i]); continue; }

            float t = age / GameConfig.OriginFlashTime;
            float scale = Mathf.Lerp(0.2f, GameConfig.OriginFlashScale, Easing.OutCubic(t));
            _flashes[i].tr.localScale = Vector3.one * scale;
            float a = 1f - t;
            _flashes[i].sr.color = new Color(1f, 1f, 1f, 1f) * (a * a * 1.6f);
        }
    }

    private void DetectTicks()
    {
        if (_audio == null || _wallCount == 0) return;
        Vector2 pp = _player != null ? (Vector2)_player.position : Vector2.zero;
        float nearSqr = GameConfig.NearRevealRadius * GameConfig.NearRevealRadius;

        for (int i = 0; i < _pings.Length; i++)
        {
            var p = _pings[i];
            // Pings are never deactivated mid-level (only ResetPings clears them), so without the
            // tickedWalls check a ping whose front left the maze long ago still walked all ~400
            // wall entries every frame just to find them all already flagged.
            if (!p.active || p.tickCount >= TickCap || p.tickedWalls >= _wallCount) continue;

            float radius = (Time.time - p.startTime) * _speed;
            if (radius > _maxDetectRadius) continue;

            float radiusSqr = radius * radius;       // hoisted out of the inner loop
            bool anyNew = false, anyNearNew = false;
            for (int w = 0; w < _wallCount; w++)
            {
                if (p.ticked[w]) continue;
                Vector2 wc = _wallCenters[w];
                float dx = wc.x - p.origin.x, dy = wc.y - p.origin.y;
                if (dx * dx + dy * dy > radiusSqr) continue; // front hasn't reached it

                p.ticked[w] = true;
                p.tickedWalls++;
                anyNew = true;
                float pdx = wc.x - pp.x, pdy = wc.y - pp.y;
                if (pdx * pdx + pdy * pdy < nearSqr) anyNearNew = true;
            }

            if (anyNew && Time.time - p.lastTickTime > GameConfig.TickThrottle)
            {
                // Pitch rises with radius so a sweep reads as a cascade.
                float pitch = 1f + radius * 0.02f + UnityEngine.Random.Range(-GameConfig.TickPitchJitter, GameConfig.TickPitchJitter);
                _audio.PlayTick(pitch);
                p.lastTickTime = Time.time;
                p.tickCount++;
            }

            if (anyNearNew && Time.time - _lastNearHaptic > GameConfig.NearHapticThrottle)
            {
                Haptics.Medium();
                _lastNearHaptic = Time.time;
            }
        }
    }

    // ---- pool helpers ----
    private void Spawn(Pooled[] pool, Vector2 origin, Color _)
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (pool[i].active) continue;
            pool[i].active = true;
            pool[i].startTime = Time.time;
            pool[i].tr.position = new Vector3(origin.x, origin.y, 0f);
            pool[i].tr.localScale = Vector3.zero;
            pool[i].tr.gameObject.SetActive(true);
            return;
        }
        // All busy: stomp the oldest (index 0) — 8 slots is plenty for 4 pings.
        pool[0].startTime = Time.time;
        pool[0].tr.position = new Vector3(origin.x, origin.y, 0f);
    }

    private void Deactivate(ref Pooled p)
    {
        if (!p.active && !p.tr.gameObject.activeSelf) return;
        p.active = false;
        p.tr.gameObject.SetActive(false);
    }
}
