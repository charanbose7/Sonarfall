using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The glowing dot. Movement is FRAME-RATE and DIRECT: while you drag, the dot moves by
/// your finger's world delta every rendered frame (no physics step, no interpolation, no
/// catch-up), with wall collisions resolved by a swept CircleCast + slide. This is what
/// makes it feel glued to your thumb. A quick touch that never becomes a drag = a ping.
/// WASD/arrows also work.
///
/// It also records a short position history so touching a decoy can rewind the dot 5s.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class PlayerController : MonoBehaviour
{
    private Rigidbody2D _rb;
    private Camera _cam;
    private GameManager _gm;
    private SonarManager _sonar;
    private FxManager _fx;
    private ProceduralAudio _audio;
    private Transform _glow;
    private TrailRenderer _trail;
    private Vector3 _glowBase;
    private Vector3 _glowScale;
    private float _spawnT = 1f;

    // Drag state.
    private bool _dragging;
    private bool _gestureOnUI;      // this press began on a UI element -> gameplay ignores it
    private Vector2 _downScreen;
    private float _downTime;
    private Vector2 _fingerPrevWorld;
    private Vector2 _estVel;         // estimated velocity (for squash + audio), since we're kinematic
    private float _lastSlideFx;

    // Collision casting.
    private ContactFilter2D _castFilter;
    private readonly List<RaycastHit2D> _castHits = new List<RaycastHit2D>(8);

    // Path history for the rewind penalty.
    private readonly List<Vector2> _pathPos = new List<Vector2>(160);
    private readonly List<float> _pathTime = new List<float>(160);
    private float _lastSample;

    public bool IsRewinding { get; private set; }
    public bool Moving => _estVel.sqrMagnitude > 0.4f;

    /// <summary>
    /// Where the dot sat at the START of the current frame. Together with its position now this
    /// gives the segment it travelled, which pickup tests sweep against. A drag applies its finger
    /// delta directly — there is no speed cap — so a fast flick can cross a whole cell in one
    /// frame, and a test that only samples the end position misses everything in between.
    /// </summary>
    public Vector2 PrevPosition { get; private set; }

    public void Init(GameManager gm, SonarManager sonar, Camera cam, Transform glow,
                     FxManager fx, TrailRenderer trail, ProceduralAudio audio)
    {
        _gm = gm; _sonar = sonar; _cam = cam; _glow = glow; _fx = fx; _trail = trail; _audio = audio;
        _rb = GetComponent<Rigidbody2D>();
        _glowBase = Vector3.one * GameConfig.PlayerGlowScale;
        _glowScale = _glowBase;
        if (_glow != null) _glow.localScale = _glowBase;

        _castFilter = ContactFilter2D.noFilter;
        _castFilter.useTriggers = false;
    }

    public void PlaceAt(Vector2 worldPos)
    {
        _rb.position = worldPos;
        transform.position = new Vector3(worldPos.x, worldPos.y, 0f);
        PrevPosition = worldPos;   // a respawn must not read as a sweep from the old cell
        _rb.linearVelocity = Vector2.zero;
        _estVel = Vector2.zero;
        _dragging = false;
        _gestureOnUI = false;
        _pathPos.Clear(); _pathTime.Clear();

        if (_trail != null) _trail.Clear();
        _spawnT = 0f;
        _glowScale = Vector3.zero;
        if (_glow != null) _glow.localScale = Vector3.zero;
    }

    private void Update()
    {
        // Stamped before every early-out, so a frame the dot didn't move leaves a zero-length
        // segment rather than a stale one that sweeps across the maze.
        PrevPosition = _rb.position;

        if (IsRewinding) { AnimateGlow(); return; }

        if (!_gm.AcceptsInput)
        {
            _dragging = false;
            _estVel = Vector2.zero;
            if (_audio != null) _audio.SetMoveLevel(0f);
            AnimateGlow();
            return;
        }

        float dt = Time.deltaTime;
        Vector2 moveDelta = Vector2.zero;

        // ---- WASD / arrows (fixed speed) ----
        Vector2 wasd = EchoInput.MoveAxis;
        if (wasd.sqrMagnitude > 0.01f)
        {
            _dragging = false;
            moveDelta = wasd * (GameConfig.PlayerMoveSpeed * dt);
        }
        else
        {
            // ---- Direct finger-drag ----
            if (EchoInput.PingKeyDown) _gm.RequestPing();

            if (EchoInput.PointerDown)
            {
                // A press that starts on a button belongs to the UI, not the game — otherwise
                // tapping CLOSE or the gear would also fire a ping.
                //
                // The HUD band is a second, separate case: the clock, level, stars and reveal
                // counter all have raycastTarget off (they are readouts, not controls), so the UI
                // hit test above returns false for them and a thumb resting on the clock was
                // steering the dot and firing pings. Ask the UI for its readout band explicitly.
                Vector2 down = EchoInput.PointerScreen;
                _gestureOnUI = EchoInput.IsOverUI(down)
                            || (_gm != null && _gm.IsOverHud(down));
                _dragging = false;
                _downScreen = EchoInput.PointerScreen;
                _downTime = Time.time;
                _fingerPrevWorld = ScreenToWorld(_downScreen);
            }

            if (!_gestureOnUI && EchoInput.PointerHeld)
            {
                Vector2 screen = EchoInput.PointerScreen;
                if (!_dragging && (screen - _downScreen).magnitude > GameConfig.MoveEngagePx)
                {
                    _dragging = true;
                    _fingerPrevWorld = ScreenToWorld(screen); // re-anchor so there's no jump
                }

                if (_dragging)
                {
                    Vector2 fingerWorld = ScreenToWorld(screen);
                    moveDelta = (fingerWorld - _fingerPrevWorld) * GameConfig.MoveSensitivity;
                    _fingerPrevWorld = fingerWorld;
                }
            }

            if (EchoInput.PointerUp)
            {
                // Only a genuine gameplay tap pings. Two independent guards: the press didn't start
                // on a UI element, and no button fired its onClick for this same press.
                if (!_gestureOnUI && !_dragging && !_gm.UiJustPressed) _gm.RequestPing();
                _dragging = false;
                _gestureOnUI = false;
            }
        }

        // Apply movement with wall collision, and estimate velocity for juice/audio.
        Vector2 moved = MoveWithCollision(moveDelta);
        _estVel = dt > 0f ? moved / dt : Vector2.zero;

        if (_audio != null)
            _audio.SetMoveLevel(Mathf.Clamp01(_estVel.magnitude / GameConfig.PlayerMoveSpeed));

        SamplePath();
        AnimateGlow();
    }

    /// <summary>Swept move with up to 3 collide-and-slide iterations. Returns the actual displacement.</summary>
    private Vector2 MoveWithCollision(Vector2 delta)
    {
        Vector2 start = _rb.position;
        Vector2 pos = start;
        float remaining = delta.magnitude;
        if (remaining < 1e-6f) return Vector2.zero;

        Vector2 dir = delta / remaining;
        float skin = GameConfig.PlayerCollideSkin;
        float radius = GameConfig.PlayerRadius;

        for (int iter = 0; iter < 3 && remaining > 1e-6f; iter++)
        {
            int n = Physics2D.CircleCast(pos, radius, dir, _castFilter, _castHits, remaining + skin);
            RaycastHit2D best = default; float bestD = float.MaxValue; bool has = false;
            for (int k = 0; k < n; k++)
            {
                if (!(_castHits[k].collider is BoxCollider2D)) continue; // walls only, not self
                // Only block on surfaces we're moving INTO. When the dot already touches a wall,
                // the cast reports it at distance 0 for every direction; without this guard, moving
                // AWAY from the wall would get cancelled and the dot would stick (the freeze bug).
                if (Vector2.Dot(dir, _castHits[k].normal) >= -1e-4f) continue;
                if (_castHits[k].distance < bestD) { bestD = _castHits[k].distance; best = _castHits[k]; has = true; }
            }

            if (!has) { pos += dir * remaining; break; }

            float travel = Mathf.Max(0f, bestD - skin);
            pos += dir * travel;

            // Slide the leftover motion along the wall.
            Vector2 leftover = dir * (remaining - travel);
            Vector2 nrm = best.normal;
            Vector2 slide = leftover - Vector2.Dot(leftover, nrm) * nrm;

            if (_fx != null && Time.time - _lastSlideFx > 0.05f)
            {
                _fx.PlaySlide(best.point);
                _lastSlideFx = Time.time;
                Haptics.Light();
            }

            remaining = slide.magnitude;
            if (remaining < 1e-6f) break;
            dir = slide / remaining;
        }

        _rb.position = pos;
        transform.position = new Vector3(pos.x, pos.y, 0f);
        return pos - start;
    }

    private void SamplePath()
    {
        if (Time.time - _lastSample < GameConfig.PathSampleStep) return;
        _lastSample = Time.time;
        _pathPos.Add(transform.position);
        _pathTime.Add(Time.time);

        // Drop anything older than the rewind window (+a little slack).
        float cutoff = Time.time - (GameConfig.RewindSeconds + 1f);
        int drop = 0;
        while (drop < _pathTime.Count && _pathTime[drop] < cutoff) drop++;
        if (drop > 0) { _pathPos.RemoveRange(0, drop); _pathTime.RemoveRange(0, drop); }
    }

    /// <summary>Freeze time and retrace the dot back to where it was ~5s ago (decoy penalty).</summary>
    public void TriggerRewind()
    {
        if (!IsRewinding) StartCoroutine(RewindRoutine());
    }

    private IEnumerator RewindRoutine()
    {
        IsRewinding = true;
        _dragging = false;
        _estVel = Vector2.zero;
        if (_audio != null) { _audio.SetMoveLevel(0f); _audio.PlayRewind(); }
        Haptics.Heavy();
        if (_sonar != null) _sonar.ResetPings(); // wipe the current reveal — back into the dark

        // Path to retrace: everything recorded within the window, plus the current point last.
        var path = new List<Vector2>(_pathPos);
        path.Add(transform.position);
        int last = path.Count - 1;

        Time.timeScale = 0f; // stop the world; animate on unscaled time
        float e = 0f;
        while (e < GameConfig.RewindDuration)
        {
            e += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(e / GameConfig.RewindDuration);
            float f = (1f - Easing.InOutSine(t)) * last;   // last -> 0 (now -> 5s ago), smooth ease
            int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, last);
            int j = Mathf.Min(i + 1, last);
            Vector2 p = Vector2.Lerp(path[i], path[j], f - i);
            transform.position = new Vector3(p.x, p.y, 0f);
            _rb.position = p;
            AnimateGlow();
            yield return null;
        }

        Vector2 final = path[0];
        transform.position = new Vector3(final.x, final.y, 0f);
        _rb.position = final;
        _pathPos.Clear(); _pathTime.Clear();
        if (_trail != null) _trail.Clear();

        Time.timeScale = 1f;
        IsRewinding = false;
    }

    private void OnDisable()
    {
        // Safety: never leave the game frozen if a rewind is interrupted (level reload, quit).
        if (IsRewinding) { Time.timeScale = 1f; IsRewinding = false; }
    }

    private void AnimateGlow()
    {
        if (_glow == null) return;

        if (_spawnT < 1f)
        {
            _spawnT = Mathf.Min(1f, _spawnT + Time.unscaledDeltaTime / GameConfig.SpawnPopTime);
            float s = Easing.OutBack(_spawnT);
            _glowScale = _glowBase * s;
            _glow.localScale = _glowScale;
            _glow.localRotation = Quaternion.identity;
            return;
        }

        Vector2 v = _estVel;
        float speedFrac = Mathf.Clamp01(v.magnitude / GameConfig.PlayerMoveSpeed);

        Vector3 target;
        if (speedFrac > 0.06f)
        {
            float stretch = 1f + GameConfig.SquashAmount * speedFrac;
            float squash = 1f - GameConfig.SquashAmount * 0.5f * speedFrac;
            target = new Vector3(_glowBase.x * stretch, _glowBase.y * squash, _glowBase.z);
            float ang = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            _glow.localRotation = Quaternion.Euler(0f, 0f, ang);
        }
        else
        {
            float breath = 1f + GameConfig.BreathAmplitude * Mathf.Sin(Time.time * GameConfig.BreathSpeed);
            target = _glowBase * breath;
            _glow.localRotation = Quaternion.identity;
        }

        _glowScale = Vector3.Lerp(_glowScale, target, Time.unscaledDeltaTime * GameConfig.SquashLerp);
        _glow.localScale = _glowScale;
    }

    private Vector2 ScreenToWorld(Vector2 screen)
    {
        var world = _cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_cam.transform.position.z));
        return new Vector2(world.x, world.y);
    }
}
