namespace SmartX.Core.Collections;

/// <summary>
/// Self-balancing AVL tree used to index historical telemetry by timestamp (ticks).
/// <para>
/// Every insert re-balances with at most two rotations, so the height stays ≤ 1.44·log₂(n). A range query
/// such as "all moisture readings for Node A between 14:00 and 16:00" descends only into subtrees that can
/// overlap the range: cost is O(log n + k) where k is the number of results, instead of O(n) for a list scan.
/// </para>
/// Duplicate keys (two readings in the same tick) are allowed and stored to the right, preserving insertion order.
/// </summary>
public sealed class AvlTree<TKey, TValue> where TKey : IComparable<TKey>
{
    private sealed class Node(TKey key, TValue value)
    {
        public TKey Key = key;
        public TValue Value = value;
        public Node? Left;
        public Node? Right;
        public int Height = 1;
    }

    private Node? _root;

    public int Count { get; private set; }

    public int Height => H(_root);

    /// <summary>Number of rotations performed - shows the balancing work on the dashboard.</summary>
    public long Rotations { get; private set; }

    public void Insert(TKey key, TValue value)
    {
        _root = Insert(_root, key, value);
        Count++;
    }

    private Node Insert(Node? node, TKey key, TValue value)
    {
        if (node is null) return new Node(key, value);

        if (key.CompareTo(node.Key) < 0)
            node.Left = Insert(node.Left, key, value);
        else
            node.Right = Insert(node.Right, key, value); // equal keys go right (stable order)

        return Rebalance(node);
    }

    /// <summary>Removes the smallest key (used for retention trimming). O(log n).</summary>
    public bool RemoveMin()
    {
        if (_root is null) return false;
        _root = RemoveMin(_root);
        Count--;
        return true;
    }

    private Node? RemoveMin(Node node)
    {
        if (node.Left is null) return node.Right;
        node.Left = RemoveMin(node.Left);
        return Rebalance(node);
    }

    public bool TryGetMin(out TKey key, out TValue value)
    {
        var n = _root;
        if (n is null) { key = default!; value = default!; return false; }
        while (n.Left is not null) n = n.Left;
        key = n.Key; value = n.Value; return true;
    }

    public bool TryGetMax(out TKey key, out TValue value)
    {
        var n = _root;
        if (n is null) { key = default!; value = default!; return false; }
        while (n.Right is not null) n = n.Right;
        key = n.Key; value = n.Value; return true;
    }

    /// <summary>
    /// In-order range query [from, to]. Subtrees entirely outside the range are pruned.
    /// <paramref name="nodesVisited"/> reports how many nodes were touched (to compare against a linear scan).
    /// </summary>
    public List<KeyValuePair<TKey, TValue>> Range(TKey from, TKey to, out int nodesVisited, int limit = int.MaxValue)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        int visited = 0;
        // Iterative in-order traversal with an explicit stack - no recursion depth risk on huge trees.
        var stack = new Stack<Node>();
        var current = _root;
        while ((current is not null || stack.Count > 0) && result.Count < limit)
        {
            while (current is not null)
            {
                visited++;
                if (current.Key.CompareTo(from) < 0)
                {
                    current = current.Right; // whole left subtree (and this node) are too small: prune
                    continue;
                }
                stack.Push(current);
                current = current.Left;
            }
            if (stack.Count == 0) break;

            var node = stack.Pop();
            if (node.Key.CompareTo(to) > 0) break; // everything after is larger: stop
            result.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
            current = node.Right;
        }
        nodesVisited = visited;
        return result;
    }

    /// <summary>Count of items in [from, to] - same pruning, no allocation of results.</summary>
    public int CountRange(TKey from, TKey to) => CountRange(_root, from, to);

    private static int CountRange(Node? node, TKey from, TKey to)
    {
        if (node is null) return 0;
        if (node.Key.CompareTo(from) < 0) return CountRange(node.Right, from, to);
        if (node.Key.CompareTo(to) > 0) return CountRange(node.Left, from, to);
        return 1 + CountRange(node.Left, from, to) + CountRange(node.Right, from, to);
    }

    /// <summary>Verifies the AVL invariants (BST order + |balance| ≤ 1). Used by the self-test.</summary>
    public bool IsBalanced() => Check(_root, out _);

    private static bool Check(Node? n, out int height)
    {
        if (n is null) { height = 0; return true; }
        if (!Check(n.Left, out var lh) || !Check(n.Right, out var rh)) { height = 0; return false; }
        height = 1 + Math.Max(lh, rh);
        if (Math.Abs(lh - rh) > 1) return false;
        if (n.Left is not null && n.Left.Key.CompareTo(n.Key) > 0) return false;
        if (n.Right is not null && n.Right.Key.CompareTo(n.Key) < 0) return false;
        return true;
    }

    // ---- balancing helpers ----
    private static int H(Node? n) => n?.Height ?? 0;
    private static int BalanceFactor(Node n) => H(n.Left) - H(n.Right);
    private static void Update(Node n) => n.Height = 1 + Math.Max(H(n.Left), H(n.Right));

    private Node Rebalance(Node node)
    {
        Update(node);
        int balance = BalanceFactor(node);

        if (balance > 1) // left heavy
        {
            if (BalanceFactor(node.Left!) < 0) node.Left = RotateLeft(node.Left!); // Left-Right case
            return RotateRight(node);                                             // Left-Left case
        }
        if (balance < -1) // right heavy
        {
            if (BalanceFactor(node.Right!) > 0) node.Right = RotateRight(node.Right!); // Right-Left case
            return RotateLeft(node);                                                  // Right-Right case
        }
        return node;
    }

    private Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        Update(y);
        Update(x);
        Rotations++;
        return x;
    }

    private Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        Update(x);
        Update(y);
        Rotations++;
        return y;
    }
}
