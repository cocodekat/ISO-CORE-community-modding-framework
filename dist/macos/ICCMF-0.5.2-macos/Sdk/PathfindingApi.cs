using System;
using System.Collections.Generic;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    /// <summary>Bounded A* plus an attachable ISO-CORE terrain path follower.</summary>
    public sealed class PathfindingApi
    {
        public Queue<Vector3Int> Find(Vector3Int start, Func<Vector3Int, bool> reached,
            Func<Vector3Int, int> estimate, Func<Vector3Int, IEnumerable<PathEdge>> edges,
            int budget = 400, bool allowPartial = true)
        {
            if (reached == null || estimate == null || edges == null)
                throw new ArgumentNullException("Path callbacks cannot be null.");
            return AStar.Find(start, reached, estimate, edges, budget, allowPartial);
        }

        public TerrainAgentBuilder TerrainAgent(GameObject actor, Vector3Int startCell)
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            return new TerrainAgentBuilder(actor, startCell);
        }

        public static int Manhattan(Vector3Int first, Vector3Int second)
        {
            return Math.Abs(first.x - second.x) + Math.Abs(first.y - second.y) +
                Math.Abs(first.z - second.z);
        }
    }

    public struct PathEdge
    {
        public PathEdge(Vector3Int cell, int cost = 10) { Cell = cell; Cost = cost; }
        public Vector3Int Cell { get; }
        public int Cost { get; }
    }

    public sealed class TerrainAgentBuilder
    {
        internal GameObject Actor;
        internal Vector3Int Start;
        internal Func<Vector3Int> TargetValue;
        internal float SpeedValue = 2f;
        internal int StopDistanceValue = 1;
        internal float RepathValue = .8f;
        internal int BudgetValue = 400;
        internal bool CanClimbValue = true;
        internal Action<Vector3Int> ArrivedValue;

        internal TerrainAgentBuilder(GameObject actor, Vector3Int start)
        { Actor = actor; Start = start; }
        public TerrainAgentBuilder Target(Func<Vector3Int> target) { TargetValue = target; return this; }
        public TerrainAgentBuilder Target(Transform target)
        {
            TargetValue = delegate
            {
                TerrainPhysicsBody body = target == null ? null : target.GetComponent<TerrainPhysicsBody>();
                return body == null ? Start : body.GetIntPosition() + new Vector3Int(0, 0, 1);
            };
            return this;
        }
        public TerrainAgentBuilder TargetPlayer()
        {
            TargetValue = delegate
            {
                TerrainPhysicsBody body = Main.Player == null ? null : Main.Player.GetComponent<TerrainPhysicsBody>();
                return body == null ? Start : body.GetIntPosition() + new Vector3Int(0, 0, 1);
            };
            return this;
        }
        public TerrainAgentBuilder Speed(float cellsPerSecond) { SpeedValue = cellsPerSecond; return this; }
        public TerrainAgentBuilder StopWithin(int cells) { StopDistanceValue = cells; return this; }
        public TerrainAgentBuilder RepathEvery(float seconds) { RepathValue = seconds; return this; }
        public TerrainAgentBuilder SearchBudget(int cells) { BudgetValue = cells; return this; }
        public TerrainAgentBuilder CanClimb(bool enabled = true) { CanClimbValue = enabled; return this; }
        public TerrainAgentBuilder OnArrived(Action<Vector3Int> action) { ArrivedValue = action; return this; }
        public TerrainPathAgent Attach()
        {
            TerrainPathAgent agent = Actor.AddComponent<TerrainPathAgent>();
            agent.Initialize(this);
            return agent;
        }
    }

    public sealed class TerrainPathAgent : MonoBehaviour
    {
        private static readonly Vector3Int Vertical = new Vector3Int(0, 0, 1);
        private static readonly Vector3Int[] Directions =
        {
            Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down
        };
        private TerrainAgentBuilder _settings;
        private Queue<Vector3Int> _route = new Queue<Vector3Int>();
        private Vector3Int _cell;
        private Vector3Int _next;
        private float _move;
        private float _repathAt;
        private bool _moving;
        private bool _arrived;

        public Vector3Int Cell => _cell;
        public bool HasRoute => _route.Count > 0 || _moving;

        internal void Initialize(TerrainAgentBuilder settings)
        {
            _settings = settings;
            _cell = settings.Start;
            Place(_cell);
        }

        public void RepathNow() { _repathAt = 0f; _route.Clear(); }
        public void MoveTo(Vector3Int cell) { _settings.TargetValue = () => cell; RepathNow(); }

        private void Update()
        {
            if (_settings == null || Terrain.singleton == null ||
                Terrain.singleton.worldSpaceTransitionActive) return;
            Vector3Int target = _settings.TargetValue == null ? _cell : _settings.TargetValue();
            if (Distance(_cell, target) <= _settings.StopDistanceValue)
            {
                if (!_arrived) { _arrived = true; _settings.ArrivedValue?.Invoke(_cell); }
                _route.Clear();
                return;
            }
            _arrived = false;

            if (_moving)
            {
                _move += Time.deltaTime * Mathf.Max(.01f, _settings.SpeedValue);
                transform.position = Vector3.Lerp(World(_cell), World(_next), Mathf.Clamp01(_move));
                if (_move < 1f) return;
                _cell = _next; _moving = false; _move = 0f; Place(_cell);
            }

            if (Time.time >= _repathAt || _route.Count == 0)
            {
                _repathAt = Time.time + Mathf.Max(.1f, _settings.RepathValue);
                Vector3Int origin = _cell;
                _route = AStar.Find(_cell,
                    cell => Distance(cell, target) <= _settings.StopDistanceValue,
                    cell => 10 * Math.Max(0, Distance(cell, target) - _settings.StopDistanceValue),
                    cell => Edges(cell, origin), _settings.BudgetValue, true);
            }
            if (_route.Count == 0) return;
            _next = _route.Dequeue();
            _moving = true;
            _move = 0f;
        }

        private IEnumerable<PathEdge> Edges(Vector3Int from, Vector3Int origin)
        {
            foreach (Vector3Int direction in Directions)
            for (int height = _settings.CanClimbValue ? -2 : 0;
                height <= (_settings.CanClimbValue ? 1 : 0); height++)
            {
                Vector3Int next = from + direction + Vertical * height;
                if (Distance(next, origin) > 32 || !Standable(next)) continue;
                if (Terrain.singleton.CheckForCollider(next) ||
                    Terrain.singleton.CheckForCollider(next + Vertical)) continue;
                yield return new PathEdge(next, height > 0 ? 15 : 10);
            }
        }

        private static bool Standable(Vector3Int foot)
        {
            if (Terrain.singleton == null || Terrain.singleton.CellContainsWater(foot)) return false;
            bool ground = Terrain.singleton.loadedBlocks.TryGetValue(foot - Vertical, out Block block) &&
                block != null && block != Terrain.singleton.waterBlock;
            bool feet = Terrain.singleton.loadedBlocks.TryGetValue(foot, out block) &&
                block != null && block != Terrain.singleton.waterBlock;
            bool head = Terrain.singleton.loadedBlocks.TryGetValue(foot + Vertical, out block) &&
                block != null && block != Terrain.singleton.waterBlock;
            return ground && !feet && !head;
        }

        private static int Distance(Vector3Int first, Vector3Int second)
        { return Math.Abs(first.x - second.x) + Math.Abs(first.y - second.y); }
        private static Vector3 World(Vector3Int foot)
        { return Terrain.singleton.TerrainToWorldPosition(foot - Vertical); }
        private void Place(Vector3Int foot)
        {
            transform.position = World(foot);
            SpriteRenderer renderer = GetComponent<SpriteRenderer>();
            if (renderer != null) renderer.sortingOrder = Terrain.singleton.GetObjectSortingOrder(foot - Vertical) + 1;
        }
    }

    internal static class AStar
    {
        private struct Node
        {
            internal Vector3Int Cell;
            internal int Cost;
            internal int Estimate;
            internal int Priority => Cost + Estimate;
        }

        internal static Queue<Vector3Int> Find(Vector3Int start, Func<Vector3Int, bool> reached,
            Func<Vector3Int, int> estimate, Func<Vector3Int, IEnumerable<PathEdge>> edges,
            int budget, bool partial)
        {
            var frontier = new List<Node> { new Node { Cell = start, Estimate = estimate(start) } };
            var parent = new Dictionary<Vector3Int, Vector3Int>();
            var cost = new Dictionary<Vector3Int, int> { { start, 0 } };
            Vector3Int best = start;
            int bestEstimate = estimate(start);
            while (frontier.Count > 0 && budget-- > 0)
            {
                Node current = Pop(frontier);
                if (cost[current.Cell] != current.Cost) continue;
                if (reached(current.Cell)) return Reconstruct(parent, current.Cell);
                if (current.Estimate < bestEstimate) { best = current.Cell; bestEstimate = current.Estimate; }
                foreach (PathEdge edge in edges(current.Cell))
                {
                    int nextCost = current.Cost + Math.Max(1, edge.Cost);
                    if (cost.TryGetValue(edge.Cell, out int previous) && previous <= nextCost) continue;
                    cost[edge.Cell] = nextCost;
                    parent[edge.Cell] = current.Cell;
                    Push(frontier, new Node { Cell = edge.Cell, Cost = nextCost,
                        Estimate = estimate(edge.Cell) });
                }
            }
            return partial ? Reconstruct(parent, best) : new Queue<Vector3Int>();
        }

        private static Queue<Vector3Int> Reconstruct(Dictionary<Vector3Int, Vector3Int> parent,
            Vector3Int end)
        {
            var steps = new Stack<Vector3Int>();
            while (parent.TryGetValue(end, out Vector3Int previous)) { steps.Push(end); end = previous; }
            return new Queue<Vector3Int>(steps);
        }
        private static bool Before(Node a, Node b)
        { return a.Priority < b.Priority || (a.Priority == b.Priority && a.Estimate < b.Estimate); }
        private static void Push(List<Node> heap, Node node)
        {
            int index = heap.Count; heap.Add(node);
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (!Before(node, heap[parent])) break;
                heap[index] = heap[parent]; index = parent;
            }
            heap[index] = node;
        }
        private static Node Pop(List<Node> heap)
        {
            Node result = heap[0]; int last = heap.Count - 1; Node replacement = heap[last];
            heap.RemoveAt(last); if (last == 0) return result;
            int index = 0;
            while (index * 2 + 1 < heap.Count)
            {
                int child = index * 2 + 1;
                if (child + 1 < heap.Count && Before(heap[child + 1], heap[child])) child++;
                if (!Before(heap[child], replacement)) break;
                heap[index] = heap[child]; index = child;
            }
            heap[index] = replacement; return result;
        }
    }
}
