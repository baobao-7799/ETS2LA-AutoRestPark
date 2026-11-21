// ETS2LA AutoRestPark — A* route planner over the TruckLib road graph.
// Copyright (C) 2026 baobao-7799
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Numerics;

using TruckLib.ScsMap;
using ETS2LA.Game.Data;

namespace FishLeia.AutoRestPark;

/// <summary>路径中的一步：一段道路 / 一个 prefab 内部路径 / 直线。</summary>
public sealed class RouteStep
{
    public MapItem? Item;          // Road 或 Prefab；null = 直线
    public Node Entry = null!;
    public Node Exit = null!;
    public float Cost;             // 米
    public bool Inverted;          // 道路逆向行驶（惩罚）
    public PrefabPath? Path;       // prefab 内部路径
}

/// <summary>
/// 在 MapData 上跑 A*：顶点 = TruckLib Node；
/// 边 = 道路（ForwardItem/BackwardItem 连接的 Road，逆向 ×1.25 惩罚）
///    + 非目标 prefab 过境（ParsedPrefab.GetPathsFromNodeToNode 最短路径，+8m 惩罚）
///    + 目标 prefab 内部到停车节点的边（+10m）。
/// </summary>
public sealed class RoutePlanner
{
    public sealed class PlanResult
    {
        public List<Vector3> Points = new();
        public float[] CumDist = Array.Empty<float>();
        public Node Goal = null!;
        public int Expanded;
    }

    private const int MaxExpansions = 400000;
    private const int MaxStartNodes = 8;
    private const int MaxPrefabTransits = 12;

    private readonly MapData _map;
    private readonly Dictionary<ulong, ParsedPrefab?> _ppCache = new();
    private readonly Dictionary<ulong, List<Node>> _boundaryCache = new();
    private readonly Dictionary<(ulong, ulong, ulong), PrefabPath?> _pathCache = new();
    private readonly Dictionary<ulong, ParsedRoad> _roadCache = new();

    public RoutePlanner(MapData map)
    {
        _map = map;
    }

    /// <summary>规划从 start 到 target（prefab 内停车节点）的路径。失败返回 null。</summary>
    public PlanResult? Plan(Vector3 start, Prefab target, float maxRadius, float sampleDist, Action<string>? log)
    {
        try
        {
            Node? park = PickParkingNode(target);
            if (park == null) { log?.Invoke("target prefab has no usable node"); return null; }

            var starts = FindStartNodes(start);
            if (starts.Count == 0) { log?.Invoke("no road node near truck"); return null; }

            ulong parkUid = park.Uid;
            var targetNodes = new HashSet<ulong>();
            foreach (var x in target.Nodes)
            {
                var n = x as Node;
                if (n != null) targetNodes.Add(n.Uid);
            }

            var open = new PriorityQueue<Node, float>();
            var g = new Dictionary<ulong, float>();
            var came = new Dictionary<ulong, (Node, RouteStep)>();
            var closed = new HashSet<ulong>();

            foreach (var s in starts)
            {
                float gs = Vector3.Distance(s.Position, start);
                g[s.Uid] = gs;
                open.Enqueue(s, gs + Heur(s.Position, park.Position));
            }

            int expanded = 0;
            while (open.TryDequeue(out var cur, out _))
            {
                if (!closed.Add(cur.Uid)) continue;
                if (++expanded > MaxExpansions) { log?.Invoke("expansion budget exceeded"); return null; }
                float cg = g[cur.Uid];
                if (Vector3.Distance(cur.Position, start) > maxRadius) continue;

                if (cur.Uid == parkUid)
                    return BuildResult(start, cur, came, sampleDist, expanded);

                var exits = new List<(Node, RouteStep)>();
                CollectRoadExits(cur, exits);
                CollectPrefabTransits(cur, target.Uid, park, exits);
                AddTargetInternalEdge(target, targetNodes, cur, park, exits);

                foreach (var e in exits)
                {
                    if (closed.Contains(e.Item1.Uid)) continue;
                    float ng = cg + e.Item2.Cost;
                    if (g.TryGetValue(e.Item1.Uid, out var og) && ng >= og) continue;
                    g[e.Item1.Uid] = ng;
                    came[e.Item1.Uid] = (cur, e.Item2);
                    open.Enqueue(e.Item1, ng + Heur(e.Item1.Position, park.Position));
                }
            }
            log?.Invoke("no path found within radius");
            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke("exception: " + ex.Message);
            return null;
        }
    }

    // ---------- 节点选择 ----------

    private List<Node> FindStartNodes(Vector3 pos)
    {
        var res = new List<Node>();
        var nearby = _map.Nodes.Within(pos.X - 80.0, pos.Z - 80.0, pos.X + 80.0, pos.Z + 80.0);
        foreach (var x in nearby)
        {
            if (x is Node n && (n.ForwardItem is Road || n.BackwardItem is Road))
                res.Add(n);
        }
        res.Sort((a, b) =>
            Vector3.DistanceSquared(a.Position, pos).CompareTo(Vector3.DistanceSquared(b.Position, pos)));
        if (res.Count > MaxStartNodes) res.RemoveRange(MaxStartNodes, res.Count - MaxStartNodes);
        return res;
    }

    /// <summary>在目标 prefab 内选停车节点：优先「无外部连接」的内部节点（停车位），取离 Origin 250m 内最深的；兜底 Origin 节点。</summary>
    private static Node? PickParkingNode(Prefab pf)
    {
        if (pf.Nodes == null || pf.Nodes.Count == 0) return null;
        int oi = pf.Origin < pf.Nodes.Count ? pf.Origin : 0;
        Node origin = (Node)pf.Nodes[oi];
        var dead = new List<Node>();
        foreach (var x in pf.Nodes)
        {
            if (x is Node n && n.ForwardItem == null && n.BackwardItem == null)
                dead.Add(n);
        }
        var cand = dead.Count > 0 ? dead : new List<Node>();
        if (cand.Count == 0)
        {
            foreach (var x in pf.Nodes)
                if (x is Node n) cand.Add(n);
        }
        Node? best = null;
        float bestD = -1f;
        foreach (var n in cand)
        {
            float d = Vector3.Distance(n.Position, origin.Position);
            if (d > 250f) continue;
            if (d > bestD) { bestD = d; best = n; }
        }
        return best ?? origin;
    }

    // ---------- 边的收集 ----------

    private static void CollectRoadExits(Node cur, List<(Node, RouteStep)> exits)
    {
        AddRoadExit(cur, cur.ForwardItem, exits);
        AddRoadExit(cur, cur.BackwardItem, exits);
    }

    private static void AddRoadExit(Node cur, IMapObject? item, List<(Node, RouteStep)> exits)
    {
        if (!(item is Road r)) return;
        Node? other = null;
        bool inv = false;
        if (ReferenceEquals(r.Node, cur)) { other = r.ForwardNode as Node; inv = false; }
        else if (ReferenceEquals(r.ForwardNode, cur)) { other = r.Node as Node; inv = true; }
        if (other == null) return;
        exits.Add((other, new RouteStep
        {
            Item = r,
            Entry = cur,
            Exit = other,
            Inverted = inv,
            Cost = r.Length * (inv ? 1.25f : 1f)
        }));
    }

    private void CollectPrefabTransits(Node cur, ulong targetUid, Node park, List<(Node, RouteStep)> exits)
    {
        foreach (var itemObj in new[] { cur.ForwardItem, cur.BackwardItem })
        {
            if (!(itemObj is Prefab pf) || pf.Uid == targetUid || pf.Nodes.Count > 200) continue;
            var pp = TryGetParsedPrefab(pf);
            if (pp == null) continue;
            var bounds = GetBoundaryNodes(pf);
            // 按到目标停车节点的距离排序，只取近的 12 个出口
            var sorted = new List<Node>(bounds);
            sorted.RemoveAll(b => b.Uid == cur.Uid);
            sorted.Sort((a, b) =>
                Vector3.DistanceSquared(a.Position, park.Position)
                    .CompareTo(Vector3.DistanceSquared(b.Position, park.Position)));
            int added = 0;
            foreach (var b in sorted)
            {
                if (added >= MaxPrefabTransits) break;
                try
                {
                    var pth = ShortestPath(pf, pp, cur, b);
                    if (pth == null) continue;
                    exits.Add((b, new RouteStep
                    {
                        Item = pf,
                        Entry = cur,
                        Exit = b,
                        Cost = pth.Length + 8f,
                        Path = pth
                    }));
                    added++;
                }
                catch { }
            }
        }
    }

    private void AddTargetInternalEdge(Prefab target, HashSet<ulong> targetNodes, Node cur, Node park, List<(Node, RouteStep)> exits)
    {
        if (!targetNodes.Contains(cur.Uid)) return;
        if (cur.ForwardItem == null && cur.BackwardItem == null) return;
        var pp = TryGetParsedPrefab(target);
        if (pp == null) return;
        try
        {
            var pth = ShortestPath(target, pp, cur, park);
            if (pth != null && pth.Length > 0.5f)
            {
                exits.Add((park, new RouteStep
                {
                    Item = target,
                    Entry = cur,
                    Exit = park,
                    Cost = pth.Length + 10f,
                    Path = pth
                }));
            }
            else
            {
                exits.Add((park, new RouteStep
                {
                    Item = null,
                    Entry = cur,
                    Exit = park,
                    Cost = Vector3.Distance(cur.Position, park.Position)
                }));
            }
        }
        catch { }
    }

    private List<Node> GetBoundaryNodes(Prefab pf)
    {
        if (_boundaryCache.TryGetValue(pf.Uid, out var cached)) return cached;
        var list = new List<Node>();
        foreach (var x in pf.Nodes)
        {
            if (x is Node n && (n.ForwardItem != null || n.BackwardItem != null))
                list.Add(n);
        }
        _boundaryCache[pf.Uid] = list;
        return list;
    }

    private PrefabPath? ShortestPath(Prefab pf, ParsedPrefab pp, Node from, Node to)
    {
        var key = (pf.Uid, from.Uid, to.Uid);
        if (_pathCache.TryGetValue(key, out var cached)) return cached;
        PrefabPath? bestP = null;
        try
        {
            var (best, others) = pp.GetPathsFromNodeToNode(from, to, from.Position);
            float bl = float.MaxValue;
            if (best != null) foreach (var p in best) if (p.Length < bl) { bl = p.Length; bestP = p; }
            if (others != null) foreach (var p in others) if (p.Length < bl) { bl = p.Length; bestP = p; }
        }
        catch { }
        _pathCache[key] = bestP;
        return bestP;
    }

    private ParsedPrefab? TryGetParsedPrefab(Prefab pf)
    {
        if (_ppCache.TryGetValue(pf.Uid, out var cached)) return cached;
        ParsedPrefab? result = null;
        try { result = new ParsedPrefab(pf); }
        catch { }
        _ppCache[pf.Uid] = result;
        return result;
    }

    private static float Heur(Vector3 a, Vector3 b)
    {
        var d = a - b;
        d.Y = 0f;
        return d.Length() * 0.9f;
    }

    // ---------- 结果：采样成折线 ----------

    private PlanResult BuildResult(Vector3 truckPos, Node goal, Dictionary<ulong, (Node, RouteStep)> came, float sd, int expanded)
    {
        var steps = new List<RouteStep>();
        var cur = goal;
        while (came.TryGetValue(cur.Uid, out var cf))
        {
            steps.Add(cf.Item2);
            cur = cf.Item1;
        }
        steps.Reverse();

        var pts = new List<Vector3> { truckPos };
        foreach (var st in steps)
        {
            if (st.Item is Road r) SampleRoad(r, st, sd, pts);
            else if (st.Path != null) SamplePrefabPath(st, sd, pts);
            else SampleStraight(st, sd, pts);
        }

        var clean = new List<Vector3>();
        foreach (var p in pts)
        {
            if (clean.Count == 0 || Vector3.DistanceSquared(clean[clean.Count - 1], p) > 0.25f)
                clean.Add(p);
        }
        var cum = new float[clean.Count];
        for (int i = 1; i < clean.Count; i++)
            cum[i] = cum[i - 1] + Vector3.Distance(clean[i - 1], clean[i]);

        return new PlanResult { Points = clean, CumDist = cum, Goal = goal, Expanded = expanded };
    }

    private void SampleRoad(Road r, RouteStep st, float sd, List<Vector3> pts)
    {
        if (!_roadCache.TryGetValue(r.Uid, out var pr))
        {
            pr = new ParsedRoad(r);
            _roadCache[r.Uid] = pr;
        }
        int best = pr.GetBestLaneFor(st.Entry.Position, st.Inverted, true);
        Side side;
        int lane;
        if (best < 0) { side = Side.Left; lane = -best - 1; }
        else { side = Side.Right; lane = best - 1; }

        float tFrom = st.Entry.Uid == pr.StartNode.Uid ? 0f : 1f;
        float tTo = 1f - tFrom;
        float len = r.Length;
        int n = Math.Max(2, (int)Math.Ceiling(len / sd));
        for (int i = 0; i <= n; i++)
        {
            float t = tFrom + (tTo - tFrom) * i / n;
            try
            {
                var p = pr.InterpolateLane(t, side, lane);
                pts.Add(p.Position);
            }
            catch { }
        }
    }

    private static void SamplePrefabPath(RouteStep st, float sd, List<Vector3> pts)
    {
        if (st.Path == null) return;
        float len = st.Path.Length;
        int n = Math.Max(2, (int)Math.Ceiling(len / sd));
        for (int i = 0; i <= n; i++)
        {
            try
            {
                var p = st.Path.InterpolateDist(len * i / n);
                if (p.HasValue) pts.Add(p.Value.Position);
            }
            catch { }
        }
    }

    private static void SampleStraight(RouteStep st, float sd, List<Vector3> pts)
    {
        float len = Vector3.Distance(st.Entry.Position, st.Exit.Position);
        int n = Math.Max(2, (int)Math.Ceiling(len / sd));
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            pts.Add(Vector3.Lerp(st.Entry.Position, st.Exit.Position, t));
        }
    }
}
