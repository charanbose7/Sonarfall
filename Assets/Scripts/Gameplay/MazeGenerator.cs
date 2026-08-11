using System.Collections.Generic;
using UnityEngine;

/// <summary>One wall segment in world space (center + size), ready to become mesh + collider.</summary>
public struct WallSegment
{
    public Vector2 center;
    public Vector2 size;
    public WallSegment(Vector2 c, Vector2 s) { center = c; size = s; }
}

/// <summary>Result of generating a maze: walls, connectivity, and useful world bounds.</summary>
public class MazeData
{
    public int size;
    public float cellSize;
    public List<WallSegment> walls;
    public List<WallSegment> posts;      // corner fillers — rendered only, no colliders
    public int[,] cells;                 // per-cell wall bitmask (bit set = wall present)
    public List<Vector2Int> deadEnds;    // cells with 3 walls (excl. start & exit)
    /// <summary>Dead ends reachable WITHOUT crossing the exit — the only safe spots for pickups.</summary>
    public List<Vector2Int> reachableDeadEnds;
    public List<Vector2Int> solutionPath;// unique start->exit path (for on-path decoys)
    public Vector2 startPos;
    public Vector2 exitPos;
    public Vector2Int exitCell;
    public Vector2 worldMin;
    public Vector2 worldMax;
    public Vector2 worldCenter;
    public float worldWidth;
    public float worldHeight;

    public Vector2 CellCenter(int x, int y) => new Vector2(x * cellSize, y * cellSize);

    /// <summary>Is the wall on the given side of cell (x,y) open (carved)?</summary>
    public bool IsOpen(int x, int y, int dirBit) => (cells[x, y] & dirBit) == 0;
}

/// <summary>
/// Recursive-backtracker maze generator. Produces a perfect maze (exactly one path
/// between any two cells), so the exit is always reachable. Pure data — no GameObjects.
/// </summary>
public static class MazeGenerator
{
    public const int N = 1, E = 2, S = 4, W = 8;

    /// <param name="horizontalStart">
    /// Force the first carve out of (0,0) to go East, guaranteeing the player can move left/right
    /// from the spawn. The tutorial's first card says "slide your finger" over a finger miming a
    /// horizontal drag — on a maze whose start cell only opened north, that instruction was a lie
    /// and the step could not be completed the way it was demonstrated. Biasing the first DFS step
    /// (rather than knocking the wall out afterwards) keeps the maze perfect, which SolvePath and
    /// the dead-end scan both depend on.
    /// </param>
    public static MazeData Generate(int size, float cellSize, int seed, bool horizontalStart = false)
    {
        var rng = new System.Random(seed);

        int[,] cells = new int[size, size];
        for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
                cells[x, y] = N | E | S | W;

        bool[,] visited = new bool[size, size];
        var stack = new Stack<Vector2Int>();
        var start = new Vector2Int(0, 0);
        visited[start.x, start.y] = true;
        stack.Push(start);
        bool biased = false;

        while (stack.Count > 0)
        {
            var cur = stack.Peek();
            var neighbors = UnvisitedNeighbors(cur, size, visited);
            if (neighbors.Count == 0) { stack.Pop(); continue; }

            Vector2Int next;
            if (horizontalStart && cur == start && !biased && size > 1
                && neighbors.Contains(new Vector2Int(1, 0)))
            {
                next = new Vector2Int(1, 0);   // spawn cell opens East, so a sideways drag works
                biased = true;
            }
            else
            {
                next = neighbors[rng.Next(neighbors.Count)];
            }
            RemoveWallBetween(cells, cur, next);
            visited[next.x, next.y] = true;
            stack.Push(next);
        }

        // Wall segments in world space (emit each shared wall once + outer border).
        //
        // Walls are exactly (cellSize - thickness) long so they sit strictly BETWEEN the corner
        // junctions, and each used junction gets a single thickness-sized post. Previously walls
        // ran cellSize + thickness, so two perpendicular walls overlapped in the corner square —
        // and because the sonar shader blends additively (One One), that overlap rendered at
        // double brightness: a visible box at every intersection.
        var walls = new List<WallSegment>();
        float half = cellSize * 0.5f;
        float t = GameConfig.WallThickness;
        float span = cellSize - t;                       // clear run between two posts
        var junction = new bool[size + 1, size + 1];     // lattice corner used by >= 1 wall

        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            Vector2 c = new Vector2(x * cellSize, y * cellSize);
            if ((cells[x, y] & E) != 0)
            {
                walls.Add(new WallSegment(c + new Vector2(half, 0f), new Vector2(t, span)));
                junction[x + 1, y] = true; junction[x + 1, y + 1] = true;
            }
            if ((cells[x, y] & N) != 0)
            {
                walls.Add(new WallSegment(c + new Vector2(0f, half), new Vector2(span, t)));
                junction[x, y + 1] = true; junction[x + 1, y + 1] = true;
            }
            if (x == 0 && (cells[x, y] & W) != 0)
            {
                walls.Add(new WallSegment(c + new Vector2(-half, 0f), new Vector2(t, span)));
                junction[0, y] = true; junction[0, y + 1] = true;
            }
            if (y == 0 && (cells[x, y] & S) != 0)
            {
                walls.Add(new WallSegment(c + new Vector2(0f, -half), new Vector2(span, t)));
                junction[x, 0] = true; junction[x + 1, 0] = true;
            }
        }

        // One post per used junction — fills each corner exactly once, never overlapping.
        // Visual only: at thickness 0.12 vs a 0.24-radius player these are impassable anyway, so
        // they get no colliders and are excluded from sonar tick detection.
        var posts = new List<WallSegment>();
        for (int i = 0; i <= size; i++)
        for (int j = 0; j <= size; j++)
        {
            if (!junction[i, j]) continue;
            posts.Add(new WallSegment(new Vector2(i * cellSize - half, j * cellSize - half),
                                      new Vector2(t, t)));
        }

        var exitCell = new Vector2Int(size - 1, size - 1);

        // Dead ends (3 walls) that aren't the start or the exit — good spots for decoys.
        //
        // A perfect maze has exactly ONE route between any two cells, so a dead end sitting
        // "behind" the exit can only be reached by passing through the exit cell — and touching
        // the exit ends the level. Anything placed there is unreachable by construction, so the
        // reachable set is computed with the exit treated as a wall.
        var reachable = ReachableAvoiding(cells, size, new Vector2Int(0, 0), exitCell);

        var deadEnds = new List<Vector2Int>();
        var reachableDeadEnds = new List<Vector2Int>();
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            if ((x == 0 && y == 0) || (x == exitCell.x && y == exitCell.y)) continue;
            if (WallCount(cells[x, y]) != 3) continue;
            var c = new Vector2Int(x, y);
            deadEnds.Add(c);
            if (reachable[x, y]) reachableDeadEnds.Add(c);
        }

        var solution = SolvePath(cells, size, new Vector2Int(0, 0), exitCell);

        var data = new MazeData
        {
            size = size,
            cellSize = cellSize,
            walls = walls,
            posts = posts,
            cells = cells,
            deadEnds = deadEnds,
            reachableDeadEnds = reachableDeadEnds,
            solutionPath = solution,
            startPos = new Vector2(0f, 0f),
            exitCell = exitCell,
            exitPos = new Vector2(exitCell.x * cellSize, exitCell.y * cellSize),
            worldMin = new Vector2(-half, -half),
            worldMax = new Vector2((size - 1) * cellSize + half, (size - 1) * cellSize + half),
        };
        data.worldWidth = data.worldMax.x - data.worldMin.x;
        data.worldHeight = data.worldMax.y - data.worldMin.y;
        data.worldCenter = (data.worldMin + data.worldMax) * 0.5f;
        return data;
    }

    /// <summary>
    /// Flood-fill from <paramref name="start"/> treating <paramref name="blocked"/> as impassable.
    /// Used to find the cells a player can actually visit before the exit ends the level.
    /// </summary>
    private static bool[,] ReachableAvoiding(int[,] cells, int size, Vector2Int start, Vector2Int blocked)
    {
        var seen = new bool[size, size];
        if (start == blocked) return seen;

        var q = new Queue<Vector2Int>();
        seen[start.x, start.y] = true;
        q.Enqueue(start);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            TryFlood(cells, seen, q, c, new Vector2Int(c.x, c.y + 1), N, blocked, size);
            TryFlood(cells, seen, q, c, new Vector2Int(c.x + 1, c.y), E, blocked, size);
            TryFlood(cells, seen, q, c, new Vector2Int(c.x, c.y - 1), S, blocked, size);
            TryFlood(cells, seen, q, c, new Vector2Int(c.x - 1, c.y), W, blocked, size);
        }
        return seen;
    }

    private static void TryFlood(int[,] cells, bool[,] seen, Queue<Vector2Int> q,
                                 Vector2Int from, Vector2Int to, int dirBit, Vector2Int blocked, int size)
    {
        if (to.x < 0 || to.y < 0 || to.x >= size || to.y >= size) return;
        if ((cells[from.x, from.y] & dirBit) != 0) return;   // wall between them
        if (to == blocked) return;                            // stepping here would end the level
        if (seen[to.x, to.y]) return;
        seen[to.x, to.y] = true;
        q.Enqueue(to);
    }

    /// <summary>BFS the (unique) start->exit path through the carved maze.</summary>
    /// <summary>
    /// The unique route between two cells. Public because GameManager needs the corridor leading
    /// to the bonus orb, so the drifting exit can be kept off it.
    /// </summary>
    public static List<Vector2Int> SolvePath(int[,] cells, int size, Vector2Int start, Vector2Int exit)
    {
        var prev = new Dictionary<Vector2Int, Vector2Int>();
        var seen = new HashSet<Vector2Int> { start };
        var q = new Queue<Vector2Int>();
        q.Enqueue(start);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (c == exit) break;

            // Step to a neighbor only if the wall between them is carved open.
            TryStep(cells, seen, prev, q, c, new Vector2Int(c.x, c.y + 1), N);
            TryStep(cells, seen, prev, q, c, new Vector2Int(c.x + 1, c.y), E);
            TryStep(cells, seen, prev, q, c, new Vector2Int(c.x, c.y - 1), S);
            TryStep(cells, seen, prev, q, c, new Vector2Int(c.x - 1, c.y), W);
        }

        var path = new List<Vector2Int>();
        var cur = exit;
        path.Add(cur);
        while (cur != start && prev.ContainsKey(cur))
        {
            cur = prev[cur];
            path.Add(cur);
        }
        path.Reverse(); // start -> exit
        return path;
    }

    private static void TryStep(int[,] cells, HashSet<Vector2Int> seen, Dictionary<Vector2Int, Vector2Int> prev,
                                Queue<Vector2Int> q, Vector2Int from, Vector2Int to, int dirBit)
    {
        if ((cells[from.x, from.y] & dirBit) != 0) return; // wall present -> can't pass
        if (seen.Contains(to)) return;
        seen.Add(to);
        prev[to] = from;
        q.Enqueue(to);
    }

    private static int WallCount(int mask)
    {
        int c = 0;
        if ((mask & N) != 0) c++;
        if ((mask & E) != 0) c++;
        if ((mask & S) != 0) c++;
        if ((mask & W) != 0) c++;
        return c;
    }

    private static List<Vector2Int> UnvisitedNeighbors(Vector2Int c, int size, bool[,] visited)
    {
        var list = new List<Vector2Int>(4);
        if (c.y + 1 < size && !visited[c.x, c.y + 1]) list.Add(new Vector2Int(c.x, c.y + 1));
        if (c.x + 1 < size && !visited[c.x + 1, c.y]) list.Add(new Vector2Int(c.x + 1, c.y));
        if (c.y - 1 >= 0 && !visited[c.x, c.y - 1]) list.Add(new Vector2Int(c.x, c.y - 1));
        if (c.x - 1 >= 0 && !visited[c.x - 1, c.y]) list.Add(new Vector2Int(c.x - 1, c.y));
        return list;
    }

    private static void RemoveWallBetween(int[,] cells, Vector2Int a, Vector2Int b)
    {
        int dx = b.x - a.x;
        int dy = b.y - a.y;
        if (dy == 1)      { cells[a.x, a.y] &= ~N; cells[b.x, b.y] &= ~S; }
        else if (dy == -1){ cells[a.x, a.y] &= ~S; cells[b.x, b.y] &= ~N; }
        else if (dx == 1) { cells[a.x, a.y] &= ~E; cells[b.x, b.y] &= ~W; }
        else if (dx == -1){ cells[a.x, a.y] &= ~W; cells[b.x, b.y] &= ~E; }
    }
}
