using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The sector rules — one new idea per sector from THE HOLLOW on (see GameConfig.RulesFor):
///
///   HUNTER      (THE HOLLOW)  something in the maze moves toward every ping you fire
///   GATES       (ECHO CORE)   passages on the route open and shut on a beat
///   FAST FADE   (LONG DARK)   pure tuning — GameConfig.ApplyRules shortens the echo; nothing here
///   STILL WATER (STILLWATER)  patches where a ping shows nothing and only your own glow reaches
///   CURRENTS    (UNDERTOW)    straight corridors that push you along
///
/// Each is placed from the level's Difficulty, ticked from TickPlaying, and introduced by a
/// one-line callout the first time the player actually meets it (CheckRuleEncounters).
/// </summary>
public partial class GameManager
{
    // =========================================================================================
    //  State
    // =========================================================================================
    private const int MaxHunters = 2, MaxGates = 4, MaxStill = 4, MaxCurrents = 4, ChevronsPerCurrent = 10;

    private sealed class Hunter
    {
        public SpriteRenderer sr;
        public Vector2 pos;
        public Vector2Int cell, lair;
        public readonly List<Vector2Int> path = new List<Vector2Int>(32);
        public int pathIdx;
        public bool awake, moving;
        public float stunUntil;
        public Vector2 heading = Vector2.right;
    }

    private sealed class Gate
    {
        public SpriteRenderer a, b;     // the two halves of the bar, each retracting into its wall
        public BoxCollider2D col;
        public Vector2 centre, along;   // centre of the doorway; unit vector along the bar
        public float span;              // bar length
        public float phase;             // 0..1 offset into the shared beat
        public bool closed;
    }

    private sealed class Current
    {
        public readonly List<Vector2Int> cells = new List<Vector2Int>(12);
        public Vector2 dir;             // unit push direction
        public SpriteRenderer[] chevrons;
        public float offset;            // scroll position, in cells
    }

    private readonly Hunter[] _hunters = new Hunter[MaxHunters];
    private int _hunterCount;
    private float _hunterGraceUntil;

    private readonly Gate[] _gates = new Gate[MaxGates];
    private int _gateCount;

    private SpriteRenderer[] _stillMurk, _stillEdge;
    private readonly List<Vector3> _stillZones = new List<Vector3>(MaxStill);

    private readonly Current[] _currents = new Current[MaxCurrents];
    private int _currentCount;
    private int[,] _currentAt;          // current index + 1 per cell, 0 for none
    private bool _inCurrent;

    private readonly List<Vector2Int> _scratchCells = new List<Vector2Int>(64);

    // =========================================================================================
    //  Setup
    // =========================================================================================
    private void InitHazards()
    {
        var root = new GameObject("Hazards").transform;
        root.SetParent(transform, false);

        for (int i = 0; i < MaxHunters; i++)
        {
            var go = new GameObject("Hunter" + i);
            go.transform.SetParent(root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            VisualUtils.SetAdditive(sr, VisualUtils.HunterEyes());
            sr.color = new Color(0f, 0f, 0f, 0f);
            sr.sortingOrder = 33;
            go.transform.localScale = Vector3.one * (GameConfig.CellSize * 0.85f);
            go.SetActive(false);
            _hunters[i] = new Hunter { sr = sr };
        }

        for (int i = 0; i < MaxGates; i++)
        {
            var go = new GameObject("Gate" + i);
            go.transform.SetParent(root, false);
            var col = go.AddComponent<BoxCollider2D>();
            col.enabled = false;
            var g = new Gate { col = col };
            g.a = NewBar(go.transform, "A");
            g.b = NewBar(go.transform, "B");
            go.SetActive(false);
            _gates[i] = g;
        }

        _stillMurk = new SpriteRenderer[MaxStill];
        _stillEdge = new SpriteRenderer[MaxStill];
        for (int i = 0; i < MaxStill; i++)
        {
            // The murk is alpha-blended on the default sprite material — it has to DARKEN, which
            // additive can't — and sits just above the marine snow, so a still patch reads as a
            // hole in the water where even the drifting motes are gone.
            var mgo = new GameObject("StillMurk" + i);
            mgo.transform.SetParent(root, false);
            var m = mgo.AddComponent<SpriteRenderer>();
            m.sprite = VisualUtils.SoftDisc();
            m.color = new Color(GameConfig.StillHazeColor.r, GameConfig.StillHazeColor.g, GameConfig.StillHazeColor.b, 0.94f);
            m.sortingOrder = 6;
            mgo.SetActive(false);
            _stillMurk[i] = m;

            var ego = new GameObject("StillEdge" + i);
            ego.transform.SetParent(root, false);
            var e = ego.AddComponent<SpriteRenderer>();
            VisualUtils.SetAdditive(e, VisualUtils.HollowRing());
            e.color = new Color(0f, 0f, 0f, 0f);
            e.sortingOrder = 7;
            ego.SetActive(false);
            _stillEdge[i] = e;
        }

        for (int i = 0; i < MaxCurrents; i++)
        {
            var c = new Current { chevrons = new SpriteRenderer[ChevronsPerCurrent] };
            for (int k = 0; k < ChevronsPerCurrent; k++)
            {
                var go = new GameObject("Current" + i + "_" + k);
                go.transform.SetParent(root, false);
                var sr = go.AddComponent<SpriteRenderer>();
                VisualUtils.SetAdditive(sr, VisualUtils.Chevron());
                sr.color = new Color(0f, 0f, 0f, 0f);
                sr.sortingOrder = 8;
                go.transform.localScale = Vector3.one * (GameConfig.CellSize * 0.42f);
                go.SetActive(false);
                c.chevrons[k] = sr;
            }
            _currents[i] = c;
        }
    }

    private static SpriteRenderer NewBar(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        VisualUtils.SetAdditive(sr, VisualUtils.OutlineBar());
        sr.drawMode = SpriteDrawMode.Sliced;      // outline weight stays fixed as the bar slides
        sr.color = new Color(0f, 0f, 0f, 0f);
        sr.sortingOrder = 26;
        return sr;
    }

    /// <summary>Show or hide every hazard renderer (the fail screen, the menu).</summary>
    private void SetHazardVisuals(bool on)
    {
        for (int i = 0; i < MaxHunters; i++) if (_hunters[i] != null) _hunters[i].sr.enabled = on;
        for (int i = 0; i < MaxGates; i++)
            if (_gates[i] != null) { _gates[i].a.enabled = on; _gates[i].b.enabled = on; }
        if (_stillMurk != null)
            for (int i = 0; i < MaxStill; i++) { _stillMurk[i].enabled = on; _stillEdge[i].enabled = on; }
        for (int i = 0; i < MaxCurrents; i++)
            if (_currents[i] != null)
                for (int k = 0; k < ChevronsPerCurrent; k++) _currents[i].chevrons[k].enabled = on;
    }

    private void ClearHazards()
    {
        _hunterCount = 0;
        for (int i = 0; i < MaxHunters; i++) _hunters[i].sr.gameObject.SetActive(false);
        _gateCount = 0;
        for (int i = 0; i < MaxGates; i++) { _gates[i].col.enabled = false; _gates[i].col.gameObject.SetActive(false); }
        _stillZones.Clear();
        for (int i = 0; i < MaxStill; i++) { _stillMurk[i].gameObject.SetActive(false); _stillEdge[i].gameObject.SetActive(false); }
        _currentCount = 0;
        for (int i = 0; i < MaxCurrents; i++)
            for (int k = 0; k < ChevronsPerCurrent; k++) _currents[i].chevrons[k].gameObject.SetActive(false);
        _inCurrent = false;
        if (_player != null) _player.ExternalPush = Vector2.zero;
        _audio.SetHunter(0f, 0f);
        _audio.SetCurrent(0f, 0f);
    }

    /// <summary>Lay out this level's rule hazards. Runs after the decoys and the orb are placed.</summary>
    private void PlaceHazards()
    {
        ClearHazards();
        var d = _profile;
        if (d.hunterCount > 0)  PlaceHunters(d.hunterCount);
        if (d.gateCount > 0)    PlaceGates(d.gateCount);
        if (d.stillZones > 0)   PlaceStillZones(d.stillZones);
        if (d.currentCount > 0) PlaceCurrents(d.currentCount);
        _sonar.SetStillZones(_stillZones);
    }

    private Vector2Int CellOf(Vector2 world) => new Vector2Int(
        Mathf.Clamp(Mathf.RoundToInt(world.x / GameConfig.CellSize), 0, _maze.size - 1),
        Mathf.Clamp(Mathf.RoundToInt(world.y / GameConfig.CellSize), 0, _maze.size - 1));

    /// <summary>Route length (in steps) from the start to every cell.</summary>
    private int[,] RouteDistances(Vector2Int from)
    {
        int n = _maze.size;
        var dist = new int[n, n];
        for (int x = 0; x < n; x++) for (int y = 0; y < n; y++) dist[x, y] = -1;
        var q = new Queue<Vector2Int>();
        dist[from.x, from.y] = 0;
        q.Enqueue(from);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            int nd = dist[c.x, c.y] + 1;
            if (_maze.IsOpen(c.x, c.y, MazeGenerator.N) && dist[c.x, c.y + 1] < 0) { dist[c.x, c.y + 1] = nd; q.Enqueue(new Vector2Int(c.x, c.y + 1)); }
            if (_maze.IsOpen(c.x, c.y, MazeGenerator.E) && dist[c.x + 1, c.y] < 0) { dist[c.x + 1, c.y] = nd; q.Enqueue(new Vector2Int(c.x + 1, c.y)); }
            if (_maze.IsOpen(c.x, c.y, MazeGenerator.S) && dist[c.x, c.y - 1] < 0) { dist[c.x, c.y - 1] = nd; q.Enqueue(new Vector2Int(c.x, c.y - 1)); }
            if (_maze.IsOpen(c.x, c.y, MazeGenerator.W) && dist[c.x - 1, c.y] < 0) { dist[c.x - 1, c.y] = nd; q.Enqueue(new Vector2Int(c.x - 1, c.y)); }
        }
        return dist;
    }

    // =========================================================================================
    //  HUNTER — THE HOLLOW
    // =========================================================================================
    // It sleeps in a lair deep in the maze until the first ping, then moves toward wherever the
    // last ping came from, along the corridors, at a little under your speed. Seeing costs you:
    // every ping tells it where you are. It shows itself only as two eyes — lit by your pings like
    // everything else, and faintly on its own when it is close — and growls, panned to where it is.

    private void PlaceHunters(int count)
    {
        var dist = RouteDistances(new Vector2Int(0, 0));
        int far = 0;
        for (int x = 0; x < _maze.size; x++) for (int y = 0; y < _maze.size; y++) far = Mathf.Max(far, dist[x, y]);
        int minSteps = Mathf.Max(GameConfig.HunterLairMinSteps, Mathf.RoundToInt(far * 0.55f));

        _scratchCells.Clear();
        Vector2Int orbCell = _orbActive ? CellOf(_orbPos) : new Vector2Int(-1, -1);
        for (int x = 0; x < _maze.size; x++)
        for (int y = 0; y < _maze.size; y++)
        {
            var c = new Vector2Int(x, y);
            if (dist[x, y] < minSteps || c == _maze.exitCell || c == orbCell) continue;
            _scratchCells.Add(c);
        }
        if (_scratchCells.Count == 0) return;

        for (int i = 0; i < count && i < MaxHunters; i++)
        {
            // A second hunter takes the candidate farthest from the first, so they come at the
            // player from two directions rather than as a pair.
            Vector2Int lair = _scratchCells[Random.Range(0, _scratchCells.Count)];
            if (i > 0)
            {
                float bestD = -1f;
                for (int k = 0; k < _scratchCells.Count; k++)
                {
                    float dd = (_scratchCells[k] - _hunters[0].lair).sqrMagnitude;
                    if (dd > bestD) { bestD = dd; lair = _scratchCells[k]; }
                }
            }
            var h = _hunters[i];
            h.lair = lair; h.cell = lair;
            h.pos = _maze.CellCenter(lair.x, lair.y);
            h.path.Clear(); h.pathIdx = 0;
            h.awake = false; h.moving = false; h.stunUntil = 0f;
            h.sr.transform.position = new Vector3(h.pos.x, h.pos.y, 0f);
            h.sr.color = new Color(0f, 0f, 0f, 0f);
            h.sr.gameObject.SetActive(true);
            _hunterCount++;
        }
        _hunterGraceUntil = 0f;
    }

    /// <summary>A ping went off: every hunter that isn't stunned turns toward it.</summary>
    private void OnPingHazards(Vector2 origin)
    {
        if (_hunterCount == 0) return;
        Vector2Int target = CellOf(origin);
        for (int i = 0; i < _hunterCount; i++)
        {
            var h = _hunters[i];
            if (Time.time < h.stunUntil) continue;
            var route = MazeGenerator.SolvePath(_maze.cells, _maze.size, h.cell, target);
            h.path.Clear();
            if (route != null) h.path.AddRange(route);
            // Already past the middle of its cell toward the next one: don't walk back first.
            h.pathIdx = 0;
            if (h.path.Count > 1)
            {
                Vector2 c0 = _maze.CellCenter(h.path[0].x, h.path[0].y);
                Vector2 c1 = _maze.CellCenter(h.path[1].x, h.path[1].y);
                if (Vector2.Dot(h.pos - c0, c1 - c0) > 0f) h.pathIdx = 1;
            }
            h.awake = true;
            h.moving = h.path.Count > 0;
        }
    }

    private void UpdateHunters(Vector2 pp)
    {
        if (_hunterCount == 0) { _audio.SetHunter(0f, 0f); return; }
        float step = _profile.hunterSpeed * GameConfig.CellSize * Time.deltaTime;
        float hitR = GameConfig.HunterHitRadius * GameConfig.CellSize;
        float loudest = 0f, pan = 0f;

        for (int i = 0; i < _hunterCount; i++)
        {
            var h = _hunters[i];
            bool stunned = Time.time < h.stunUntil;

            if (h.moving && !stunned)
            {
                var targetCell = h.path[h.pathIdx];
                Vector2 target = _maze.CellCenter(targetCell.x, targetCell.y);
                Vector2 to = target - h.pos;
                if (to.sqrMagnitude > 1e-6f) h.heading = to.normalized;
                if (to.magnitude <= step)
                {
                    h.pos = target;
                    h.cell = targetCell;
                    h.pathIdx++;
                    if (h.pathIdx >= h.path.Count) h.moving = false;   // arrived: lurk here
                }
                else h.pos += to.normalized * step;
            }

            // Eyes: lit by a ping like any wall, faint on their own when close, a dull glint while
            // it is on the move anywhere — and slow blinks, so it reads as alive.
            float dist = (h.pos - pp).magnitude;
            float reveal = _sonar.RevealAt(h.pos);
            float near = Mathf.Clamp01(1f - dist / GameConfig.HunterGlowRadius);
            float blink = Mathf.Repeat(Time.time * 0.45f + i * 0.37f, 1f) > 0.95f ? 0.15f : 1f;
            float a = Mathf.Max(reveal, near * 0.6f, h.moving ? 0.10f : 0f) * blink * (stunned ? 0.35f : 1f);
            var c = GameConfig.HunterColor; c.a = Mathf.Clamp01(a);
            h.sr.color = c;
            h.sr.transform.position = new Vector3(h.pos.x, h.pos.y, 0f);
            h.sr.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(h.heading.y, h.heading.x) * Mathf.Rad2Deg);

            float vol = Mathf.Clamp01(1f - dist / 5f) * (h.moving ? 0.6f : 0.3f) * (stunned ? 0f : 1f);
            if (vol > loudest) { loudest = vol; pan = Mathf.Clamp((h.pos.x - pp.x) / 3f, -1f, 1f) * 0.85f; }

            if (!stunned && Time.time >= _hunterGraceUntil && SweptHit(h.pos, hitR))
            {
                BiteByHunter(h);
                return;
            }
        }
        _audio.SetHunter(loudest, pan);
    }

    private void BiteByHunter(Hunter h)
    {
        _hunterGraceUntil = Time.time + GameConfig.HunterGraceSeconds;
        _fx.PlayBurst(h.pos, GameConfig.HunterColor, 30);
        // It retreats to its lair and sulks there for a moment: one mistake costs one plate, not a
        // chain of them while the player is still pinned in a corridor with it.
        h.pos = _maze.CellCenter(h.lair.x, h.lair.y);
        h.cell = h.lair;
        h.moving = false;
        h.path.Clear();
        h.stunUntil = Time.time + GameConfig.HunterStunSeconds;
        DamageHull(GameConfig.HunterColor);
    }

    // =========================================================================================
    //  GATES — ECHO CORE
    // =========================================================================================
    // Doors across passages ON the route, opening and shutting on one shared beat with their
    // phases staggered — the corridor has a rhythm to read rather than a wall to wait at. A gate
    // is never fully dark (GameConfig.GateIdleGlow), flickers before it shuts, slams with a sound
    // panned to where it is, and will not close on the player: a door that crushes reads as a bug.

    private void PlaceGates(int count)
    {
        var path = _maze.solutionPath;
        if (path == null || path.Count < 6) return;

        // Cells that already hold a decoy: a gate in the passage next to one stacks two timing
        // puzzles into one cell, which is noise rather than difficulty.
        var decoyCells = new HashSet<Vector2Int>();
        for (int i = 0; i < _decoyCount; i++) decoyCells.Add(CellOf(_decoyPos[i]));

        int lo = 2, hi = path.Count - 3;       // passage k joins path[k] and path[k+1]
        int lastK = -10;
        for (int g = 0; g < count && _gateCount < MaxGates; g++)
        {
            int k = Mathf.RoundToInt(Mathf.Lerp(lo, hi, (g + 1f) / (count + 1f)));
            // Nudge off decoys and away from the previous gate.
            for (int tries = 0; tries < 6 && (decoyCells.Contains(path[k]) || decoyCells.Contains(path[k + 1]) || k - lastK < 2); tries++)
                k = Mathf.Min(hi, k + 1);
            if (k > hi || k - lastK < 2) continue;
            lastK = k;

            Vector2 a = _maze.CellCenter(path[k].x, path[k].y);
            Vector2 b = _maze.CellCenter(path[k + 1].x, path[k + 1].y);
            var gate = _gates[_gateCount];
            gate.centre = (a + b) * 0.5f;
            bool horizontalMove = path[k].y == path[k + 1].y;
            gate.along = horizontalMove ? Vector2.up : Vector2.right;   // the bar lies across the move
            gate.span = GameConfig.CellSize - GameConfig.WallThickness;
            gate.phase = _gateCount * 0.5f + Random.Range(-0.08f, 0.08f);
            gate.closed = false;

            gate.col.transform.position = new Vector3(gate.centre.x, gate.centre.y, 0f);
            gate.col.offset = Vector2.zero;
            gate.col.size = horizontalMove ? new Vector2(GameConfig.WallThickness, gate.span)
                                           : new Vector2(gate.span, GameConfig.WallThickness);
            gate.col.enabled = false;
            gate.col.gameObject.SetActive(true);
            _gateCount++;
        }
    }

    private void UpdateGates(Vector2 pp)
    {
        if (_gateCount == 0) return;
        float period = Mathf.Max(1f, _profile.gatePeriod);
        float openSecs = period * GameConfig.GateOpenFraction;
        float pr = GameConfig.PlayerRadius + 0.02f;

        for (int i = 0; i < _gateCount; i++)
        {
            var g = _gates[i];
            float t = Mathf.Repeat(Time.time / period + g.phase, 1f) * period;   // seconds into the beat
            bool wantClosed = t >= openSecs;

            // Never shut on the player. Held open while they are in the doorway; it slams the
            // moment they are clear of it.
            Vector2 rel = pp - g.centre;
            float acrossHalf = GameConfig.WallThickness * 0.5f + pr;
            float alongHalf = g.span * 0.5f + pr;
            float across = Mathf.Abs(Vector2.Dot(rel, new Vector2(g.along.y, g.along.x)));
            float alongD = Mathf.Abs(Vector2.Dot(rel, g.along));
            bool inDoor = across < acrossHalf && alongD < alongHalf;
            if (wantClosed && inDoor && !g.closed) wantClosed = false;

            if (wantClosed != g.closed)
            {
                g.closed = wantClosed;
                g.col.enabled = wantClosed;
                if (wantClosed)
                {
                    float dist = (g.centre - pp).magnitude;
                    _audio.PlayGate(Mathf.Clamp(-rel.x / 3f, -1f, 1f) * 0.85f, Mathf.Clamp01(1f - dist / 7f) * 0.7f);
                }
            }

            // Bars slide: 0 = fully shut, 1 = fully open (retracted into the walls). Shut slides
            // in over 0.12s from when the beat turned; open slides out over 0.15s at the top of
            // the beat. A door held open for the player simply stays at 1.
            float open = g.closed ? 1f - Mathf.Clamp01((t - openSecs) / 0.12f)
                       : t < openSecs ? Mathf.Clamp01(t / 0.15f) : 1f;

            float half = g.span * 0.5f * (1f - open);
            Vector2 endA = g.centre - g.along * (g.span * 0.5f);
            Vector2 endB = g.centre + g.along * (g.span * 0.5f);
            // A door frame stays visible even when open. The floor is the outline sprite's two
            // borders, below which a sliced sprite stops shrinking and starts to deform.
            float stub = Mathf.Max(half, GateMinStub);
            PlaceBar(g.a, endA + g.along * (stub * 0.5f), stub, g.along);
            PlaceBar(g.b, endB - g.along * (stub * 0.5f), stub, g.along);

            // Colour carries the state: ice-cyan outline while open, red and pulsing while shut,
            // and a red flicker in the last half-second before it shuts — the telegraph that makes
            // the timing fair. Never fully dark, and brighter under a ping.
            float reveal = _sonar.RevealAt(g.centre);
            bool warning = !g.closed && t > openSecs - GameConfig.GateTelegraph && t < openSecs;
            Color c;
            if (g.closed)
            {
                c = GameConfig.GateShutColor;
                c.a = Mathf.Clamp01(0.62f + 0.3f * Mathf.Sin(Time.time * 9f) + reveal * 0.3f);
            }
            else if (warning)
            {
                c = Mathf.Repeat(Time.time * 14f, 1f) > 0.5f ? GameConfig.GateShutColor : GameConfig.GateColor;
                c.a = Mathf.Clamp01(0.55f + reveal * 0.4f);
            }
            else
            {
                c = GameConfig.GateColor;
                c.a = Mathf.Clamp01(GameConfig.GateIdleGlow + reveal * 0.7f);
            }
            g.a.color = c; g.b.color = c;
        }
    }

    private const float GateMinStub = 0.17f;   // 2 x the outline sprite's 0.08 border, plus a hair
    private const float GateBarThick = 0.22f;  // wider than a wall, so the outline reads as a door

    private static void PlaceBar(SpriteRenderer sr, Vector2 centre, float length, Vector2 along)
    {
        sr.transform.position = new Vector3(centre.x, centre.y, 0f);
        sr.transform.localScale = Vector3.one;
        bool vertical = Mathf.Abs(along.y) > 0.5f;
        sr.size = vertical ? new Vector2(GateBarThick, length) : new Vector2(length, GateBarThick);
    }

    // =========================================================================================
    //  STILL WATER — STILLWATER
    // =========================================================================================
    // Patches on the route where the echo dies: walls inside return no light and no sound. The
    // only thing that shows the way through is the player's own glow, which lights walls within
    // arm's reach (SonarWall's _PlayerLight). The patch itself shows as a void — the marine snow
    // vanishes inside it — with a faint shimmering rim that flares when a ping front touches it.

    private void PlaceStillZones(int count)
    {
        var path = _maze.solutionPath;
        if (path == null || path.Count < 8) return;
        float r = GameConfig.StillZoneRadius;
        Vector2 start = _maze.startPos, exit = _maze.exitPos;

        for (int z = 0; z < count && _stillZones.Count < MaxStill; z++)
        {
            float f = Mathf.Lerp(0.22f, 0.78f, count == 1 ? 0.5f : z / (float)(count - 1));
            int idx = Mathf.Clamp(Mathf.RoundToInt(f * (path.Count - 1)), 0, path.Count - 1);
            Vector2 c = _maze.CellCenter(path[idx].x, path[idx].y);

            // Clear of the start, the exit, the bonus echo (which only a ping can find, so inside
            // a still patch it would be unfindable) and the other patches.
            if ((c - start).magnitude < r + 1.2f || (c - exit).magnitude < r + 1.0f) continue;
            if (_orbActive && (c - _orbPos).magnitude < r + 0.6f) continue;
            bool clash = false;
            for (int k = 0; k < _stillZones.Count; k++)
                if ((c - (Vector2)_stillZones[k]).magnitude < r * 2f) clash = true;
            if (clash) continue;

            int i = _stillZones.Count;
            _stillZones.Add(new Vector3(c.x, c.y, r));
            _stillMurk[i].transform.position = new Vector3(c.x, c.y, 0f);
            _stillMurk[i].transform.localScale = Vector3.one * (r * 2f);
            _stillMurk[i].gameObject.SetActive(true);
            _stillEdge[i].transform.position = new Vector3(c.x, c.y, 0f);
            _stillEdge[i].transform.localScale = Vector3.one * (r * 2f / 0.86f);   // HollowRing's line sits at 0.86
            _stillEdge[i].gameObject.SetActive(true);
        }
    }

    private void UpdateStillZones()
    {
        for (int i = 0; i < _stillZones.Count; i++)
        {
            Vector2 c = _stillZones[i];
            float shimmer = 0.10f + 0.06f * Mathf.Sin(Time.time * 1.3f + i * 2.1f);
            // The rim flares as a ping front reaches it — "something swallowed that echo".
            float rim = 0f;
            for (int k = 0; k < 4; k++)
            {
                float ang = k * Mathf.PI * 0.5f;
                Vector2 p = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * (_stillZones[i].z + 0.05f);
                rim = Mathf.Max(rim, _sonar.RevealAt(p));
            }
            var col = GameConfig.StillEdgeColor; col.a = Mathf.Clamp01(shimmer + rim * 0.7f);
            _stillEdge[i].color = col;
        }
    }

    // =========================================================================================
    //  CURRENTS — UNDERTOW
    // =========================================================================================
    // Long straight corridors that push you along them, slower than you can swim, so a current is
    // either a free ride or a fight. Drawn as chevrons drifting in the flow; heard as a rush.

    private void PlaceCurrents(int count)
    {
        int n = _maze.size;
        if (_currentAt == null || _currentAt.GetLength(0) != n) _currentAt = new int[n, n];
        System.Array.Clear(_currentAt, 0, _currentAt.Length);

        var onPath = new HashSet<Vector2Int>(_maze.solutionPath);
        var runs = new List<List<Vector2Int>>();
        var dirs = new List<Vector2Int>();

        // Every maximal straight run, along X and along Y.
        for (int x = 0; x < n; x++)
        for (int y = 0; y < n; y++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                int bit = axis == 0 ? MazeGenerator.E : MazeGenerator.N;
                int back = axis == 0 ? MazeGenerator.W : MazeGenerator.S;
                Vector2Int step = axis == 0 ? new Vector2Int(1, 0) : new Vector2Int(0, 1);
                if (_maze.IsOpen(x, y, back) && (axis == 0 ? x > 0 : y > 0)) continue;   // not a run start

                var run = new List<Vector2Int> { new Vector2Int(x, y) };
                var cur = new Vector2Int(x, y);
                while (_maze.IsOpen(cur.x, cur.y, bit) && (axis == 0 ? cur.x + 1 < n : cur.y + 1 < n))
                {
                    cur += step;
                    run.Add(cur);
                }
                if (run.Count < GameConfig.CurrentMinRun) continue;
                if (run.Contains(new Vector2Int(0, 0)) || run.Contains(_maze.exitCell)) continue;
                runs.Add(run);
                dirs.Add(step);
            }
        }
        if (runs.Count == 0) return;

        // Prefer runs the route actually uses — a current nobody has to swim through is scenery.
        var order = new List<int>(runs.Count);
        for (int i = 0; i < runs.Count; i++) order.Add(i);
        for (int i = order.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
        order.Sort((p, q) => Overlap(runs[q], onPath).CompareTo(Overlap(runs[p], onPath)));

        for (int oi = 0; oi < order.Count && _currentCount < count && _currentCount < MaxCurrents; oi++)
        {
            var run = runs[order[oi]];
            bool clash = false;
            for (int k = 0; k < run.Count; k++) if (_currentAt[run[k].x, run[k].y] != 0) clash = true;
            if (clash) continue;

            var cur = _currents[_currentCount];
            cur.cells.Clear();
            cur.cells.AddRange(run);
            Vector2 d = dirs[order[oi]];
            if (Random.value < 0.5f) { d = -d; cur.cells.Reverse(); }     // downstream = list order
            cur.dir = d;
            cur.offset = Random.value;
            _currentCount++;
            for (int k = 0; k < run.Count; k++) _currentAt[run[k].x, run[k].y] = _currentCount;

            int shown = Mathf.Min(ChevronsPerCurrent, cur.cells.Count);
            for (int k = 0; k < ChevronsPerCurrent; k++)
            {
                cur.chevrons[k].gameObject.SetActive(k < shown);
                cur.chevrons[k].transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            }
        }
    }

    private static int Overlap(List<Vector2Int> run, HashSet<Vector2Int> path)
    {
        int c = 0;
        for (int i = 0; i < run.Count; i++) if (path.Contains(run[i])) c++;
        return c;
    }

    private void UpdateCurrents(Vector2 pp)
    {
        if (_currentCount == 0) { _inCurrent = false; return; }

        Vector2Int pc = CellOf(pp);
        int idx = _currentAt[pc.x, pc.y] - 1;
        _inCurrent = idx >= 0;
        _player.ExternalPush = _inCurrent ? _currents[idx].dir * _profile.currentSpeed : Vector2.zero;
        _audio.SetCurrent(_inCurrent ? 0.32f : 0f, 0f);

        for (int i = 0; i < _currentCount; i++)
        {
            var cur = _currents[i];
            int len = cur.cells.Count;
            cur.offset = Mathf.Repeat(cur.offset + _profile.currentSpeed * 1.4f * Time.deltaTime / GameConfig.CellSize, 1f);
            Vector2 first = _maze.CellCenter(cur.cells[0].x, cur.cells[0].y) - cur.dir * (GameConfig.CellSize * 0.5f);
            int shown = Mathf.Min(ChevronsPerCurrent, len);
            float spacing = len / (float)shown;
            for (int k = 0; k < shown; k++)
            {
                float s = Mathf.Repeat((k + cur.offset) * spacing, len);          // cells downstream
                Vector2 p = first + cur.dir * (s * GameConfig.CellSize);
                var sr = cur.chevrons[k];
                sr.transform.position = new Vector3(p.x, p.y, 0f);
                // Fade in at the mouth and out at the end, so chevrons don't pop at the loop.
                float edge = Mathf.Clamp01(Mathf.Min(s, len - s) / 0.6f);
                float reveal = _sonar.RevealAt(p);
                var c = GameConfig.CurrentColor; c.a = Mathf.Clamp01((0.16f + reveal * 0.7f) * edge);
                sr.color = c;
            }
        }
    }

    // =========================================================================================
    //  Tick + first encounters
    // =========================================================================================
    private void UpdateHazards()
    {
        Vector2 pp = _player.transform.position;
        UpdateGates(pp);
        UpdateStillZones();
        UpdateCurrents(pp);
        UpdateHunters(pp);      // last: it can end the level
    }

    /// <summary>
    /// Each rule's one-line callout, the first time the player meets it — the moment it is on
    /// screen and about to matter, never at the top of the level.
    /// </summary>
    private void CheckRuleEncounters(Vector2 pp)
    {
        var rules = _profile.rules;
        if (rules == LevelRule.None) return;

        if ((rules & LevelRule.Hunter) != 0 && !SaveData.TaughtRule(LevelRule.Hunter))
        {
            for (int i = 0; i < _hunterCount; i++)
            {
                var h = _hunters[i];
                if (!h.moving) continue;
                SaveData.MarkTaughtRule(LevelRule.Hunter);
                var c = GameConfig.HunterColor; c.a = 1f;
                h.sr.color = c;                         // held in plain sight under the callout
                _ui.ShowTeachCard("HUNTER", "It follows your pings. Every ping is a risk.",
                                  UIManager.Danger, GameConfig.HunterColor, null,
                                  _cam, new Vector3(h.pos.x, h.pos.y, 0f), "HUNTER", VisualUtils.HunterEyes());
                return;
            }
        }

        if ((rules & LevelRule.Gates) != 0 && !SaveData.TaughtRule(LevelRule.Gates))
        {
            for (int i = 0; i < _gateCount; i++)
            {
                if ((_gates[i].centre - pp).magnitude > GameConfig.GateCalloutRadius) continue;
                SaveData.MarkTaughtRule(LevelRule.Gates);
                var c = GameConfig.GateColor; c.a = 1f;
                _gates[i].a.color = c; _gates[i].b.color = c;
                _ui.ShowTeachCard("GATES", "They open and shut on a beat. Time your run.",
                                  UIManager.Accent, GameConfig.GateColor, null,
                                  _cam, new Vector3(_gates[i].centre.x, _gates[i].centre.y, 0f), "GATE");
                return;
            }
        }

        // LONG DARK is said after the player has SEEN it: once the first reveal has faded, which
        // on these levels is startlingly soon.
        if ((rules & LevelRule.FastFade) != 0 && !SaveData.TaughtRule(LevelRule.FastFade)
            && _revealsUsed > 0 && Time.time - _lastPingTime > _profile.fade + GameConfig.FastFadeCalloutLag)
        {
            SaveData.MarkTaughtRule(LevelRule.FastFade);
            _ui.ShowTeachCard("LONG DARK", "Echoes fade fast here. Remember what you saw.",
                              UIManager.Accent, Color.clear, null);
            return;
        }

        if ((rules & LevelRule.StillWater) != 0 && !SaveData.TaughtRule(LevelRule.StillWater) && _revealsUsed > 0)
        {
            for (int i = 0; i < _stillZones.Count; i++)
            {
                Vector2 c = _stillZones[i];
                if ((c - pp).magnitude > _stillZones[i].z + 1.6f) continue;
                SaveData.MarkTaughtRule(LevelRule.StillWater);
                var e = GameConfig.StillEdgeColor; e.a = 0.9f;
                _stillEdge[i].color = e;
                _ui.ShowTeachCard("STILL WATER", "Pings show nothing in here. Feel your way.",
                                  UIManager.Accent, GameConfig.StillEdgeColor, null,
                                  _cam, new Vector3(c.x, c.y, 0f), "STILL", VisualUtils.HollowRing());
                return;
            }
        }

        if ((rules & LevelRule.Currents) != 0 && !SaveData.TaughtRule(LevelRule.Currents) && _inCurrent)
        {
            SaveData.MarkTaughtRule(LevelRule.Currents);
            _ui.ShowTeachCard("CURRENT", "It pushes you along. Ride it or fight it.",
                              UIManager.Accent, GameConfig.CurrentColor, null,
                              _cam, new Vector3(pp.x, pp.y, 0f), "CURRENT", VisualUtils.Chevron());
        }
    }
}
