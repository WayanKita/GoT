using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// =====================================================================================
//  BuildingClusterer.cs
//
//  Clusters objects by distance on a 2D projection of their world position (default XY,
//  i.e. the Z axis is ignored) and physically reorganizes them in the hierarchy:
//
//      BuildingClusterer          <- this component
//        |- Cluster 0 (12)        <- BuildingCluster, sits at the cluster centre
//        |    |- Building_A
//        |    |- Building_B
//        |    +- ...
//        |- Cluster 1 (8)
//        +- ...
//
//  Setup:
//    1. Add this component to an empty GameObject.
//    2. Parent your buildings under it and hit "Populate From Children", or drag them
//       into the Buildings list by hand.
//    3. Set Cluster Count. With Auto Rebuild on, the hierarchy regroups as you edit.
//    4. "Ungroup" (context menu) flattens everything back and removes the cluster objects.
//
//  Note: this file holds three types. Unity only needs the file name to match the
//  MonoBehaviour you add from the Add Component menu (BuildingClusterer). BuildingCluster
//  is only ever added from code, so it is happy living here.
// =====================================================================================

[DisallowMultipleComponent]
public class BuildingClusterer : MonoBehaviour
{
    public enum ProjectionPlane { XY, XZ, YZ }
    public enum MemberOrder { InputOrder, Alphabetical, DistanceToCenter }

    [Header("Input")]
    [Tooltip("The objects to cluster. Nulls are ignored.")]
    public List<Transform> buildings = new List<Transform>();

    [Header("Clustering")]
    [Min(1)] public int clusterCount = 5;

    [Tooltip("Which two axes to measure distance on. XY ignores Z, XZ ignores Y (Unity's usual ground plane).")]
    public ProjectionPlane plane = ProjectionPlane.XY;

    [Min(1)] public int maxIterations = 100;

    [Tooltip("Independent runs; the tightest result wins. Higher = more stable, slightly slower.")]
    [Min(1)] public int restarts = 5;

    [Tooltip("Same seed + same input = same clusters.")]
    public int seed = 1;

    [Header("Hierarchy")]
    [Tooltip("Optional prefab for the cluster centre (marker mesh, icon, label...). A plain empty is used if unset.")]
    public GameObject clusterCenterPrefab;

    [Tooltip("{0} = index, {1} = member count.")]
    public string clusterNameFormat = "Cluster {0} ({1})";

    [Tooltip("How the buildings are ordered underneath their cluster.")]
    public MemberOrder memberOrder = MemberOrder.DistanceToCenter;

    [Header("Editor")]
    public bool autoRebuild = true;
    public bool drawGizmos = true;
    public bool colorizeMembers = false;

    [SerializeField, HideInInspector] private List<BuildingCluster> clusters = new List<BuildingCluster>();

    /// <summary>The cluster centre objects, index-aligned with cluster indices.</summary>
    public IReadOnlyList<BuildingCluster> Clusters => clusters;

    /// <summary>Fires after every rebuild, once the cluster objects are valid.</summary>
    public event Action<BuildingClusterer> ClustersRebuilt;

    /// <summary>Convenience handle. Only meaningful if there's one clusterer in the scene.</summary>
    public static BuildingClusterer Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        if (clusters.Count == 0 && buildings.Count > 0) RebuildClusters();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------- public API

    public void SetClusterCount(int k)
    {
        clusterCount = Mathf.Max(1, k);
        RebuildClusters();
    }

    /// <summary>Overload for hooking straight to a UI slider's OnValueChanged.</summary>
    public void SetClusterCount(float k) => SetClusterCount(Mathf.RoundToInt(k));

    public BuildingCluster GetCluster(int index) =>
        index >= 0 && index < clusters.Count ? clusters[index] : null;

    /// <summary>Finds the cluster a given building ended up in.</summary>
    public BuildingCluster GetClusterOf(Transform building)
    {
        foreach (var c in clusters)
            if (c != null && c.Contains(building)) return c;
        return null;
    }

    /// <summary>The cluster whose centre is nearest a world position.</summary>
    public BuildingCluster GetNearestCluster(Vector3 worldPosition)
    {
        BuildingCluster best = null;
        float bestD = float.PositiveInfinity;
        foreach (var c in clusters)
        {
            if (c == null) continue;
            float d = (c.Center - worldPosition).sqrMagnitude;
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    [ContextMenu("Rebuild Clusters")]
    public void RebuildClusters()
    {
        // 1. Pull every building back out to the root so old groupings don't interfere.
        DetachAllMembers();

        var valid = new List<Transform>(buildings.Count);
        foreach (var t in buildings)
            if (t != null) valid.Add(t);

        var points = new Vector2[valid.Count];
        for (int i = 0; i < valid.Count; i++)
            points[i] = Project(valid[i].position);

        // 2. Cluster.
        Vector2[] centroids;
        int[] labels = KMeans2D.Fit(points, clusterCount, out centroids, maxIterations, restarts, seed);
        int k = centroids?.Length ?? 0;

        // 3. Bucket the members.
        var buckets = new List<Transform>[k];
        for (int j = 0; j < k; j++) buckets[j] = new List<Transform>();
        for (int i = 0; i < valid.Count; i++) buckets[labels[i]].Add(valid[i]);

        // 4. Create / reuse / trim the centre objects, then reparent.
        SyncClusterObjects(k);

        for (int j = 0; j < k; j++)
        {
            Vector3 center = Unproject(centroids[j], AverageDepth(buckets[j]));
            SortMembers(buckets[j], center);

            var cluster = clusters[j];
            cluster.name = string.Format(clusterNameFormat, j, buckets[j].Count);
            cluster.transform.SetSiblingIndex(j);
            cluster.Configure(j, buckets[j], centroids[j], center, ClusterColor(j));
        }

        if (colorizeMembers)
            foreach (var c in clusters) c.Tint();

        MarkDirty();
        ClustersRebuilt?.Invoke(this);
    }

    /// <summary>Adds every current child (that isn't a cluster object) to the input list.</summary>
    [ContextMenu("Populate From Children")]
    public void PopulateFromChildren()
    {
        buildings.Clear();
        foreach (Transform child in transform)
        {
            if (child.GetComponent<BuildingCluster>() != null) continue;
            buildings.Add(child);
        }
        RebuildClusters();
    }

    /// <summary>Flattens the hierarchy: all buildings back to the root, cluster objects removed.</summary>
    [ContextMenu("Ungroup")]
    public void Ungroup()
    {
        DetachAllMembers();
        SyncClusterObjects(0);
        MarkDirty();
    }

    public Vector2 Project(Vector3 p)
    {
        switch (plane)
        {
            case ProjectionPlane.XZ: return new Vector2(p.x, p.z);
            case ProjectionPlane.YZ: return new Vector2(p.y, p.z);
            default: return new Vector2(p.x, p.y);
        }
    }

    public Vector3 Unproject(Vector2 p, float depth)
    {
        switch (plane)
        {
            case ProjectionPlane.XZ: return new Vector3(p.x, depth, p.y);
            case ProjectionPlane.YZ: return new Vector3(depth, p.x, p.y);
            default: return new Vector3(p.x, p.y, depth);
        }
    }

    /// <summary>Evenly spaced hues, so neighbouring cluster indices stay distinguishable.</summary>
    public static Color ClusterColor(int index) =>
        Color.HSVToRGB((index * 0.61803f) % 1f, 0.65f, 1f);

    // ---------------------------------------------------------------- internals

    private void DetachAllMembers()
    {
        foreach (var t in buildings)
        {
            if (t == null) continue;
            if (t.parent != transform) t.SetParent(transform, true); // keep world position
        }

        // Anything left under a cluster object that isn't in the list still gets rescued,
        // so a surplus cluster can never be destroyed with children inside it.
        foreach (var c in clusters)
        {
            if (c == null) continue;
            for (int i = c.transform.childCount - 1; i >= 0; i--)
                c.transform.GetChild(i).SetParent(transform, true);
        }
    }

    private void SyncClusterObjects(int k)
    {
        clusters.RemoveAll(c => c == null);

        for (int j = clusters.Count - 1; j >= k; j--)
        {
            SafeDestroy(clusters[j].gameObject);
            clusters.RemoveAt(j);
        }

        while (clusters.Count < k)
        {
            GameObject go = clusterCenterPrefab != null
                ? Instantiate(clusterCenterPrefab, transform)
                : new GameObject("Cluster");

            go.transform.SetParent(transform, false);

            var cluster = go.GetComponent<BuildingCluster>();
            if (cluster == null) cluster = go.AddComponent<BuildingCluster>();
            clusters.Add(cluster);
        }
    }

    private void SortMembers(List<Transform> members, Vector3 center)
    {
        switch (memberOrder)
        {
            case MemberOrder.Alphabetical:
                members.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                break;
            case MemberOrder.DistanceToCenter:
                members.Sort((a, b) =>
                    (a.position - center).sqrMagnitude.CompareTo((b.position - center).sqrMagnitude));
                break;
        }
    }

    private float AverageDepth(List<Transform> group)
    {
        if (group.Count == 0) return 0f;
        float sum = 0f;
        foreach (var t in group)
        {
            var p = t.position;
            sum += plane == ProjectionPlane.XZ ? p.y : plane == ProjectionPlane.YZ ? p.x : p.z;
        }
        return sum / group.Count;
    }

    private static void SafeDestroy(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    private void MarkDirty()
    {
#if UNITY_EDITOR
        if (Application.isPlaying) return;
        EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid())
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
    }

    private void OnValidate()
    {
        clusterCount = Mathf.Max(1, clusterCount);
        if (!autoRebuild) return;

#if UNITY_EDITOR
        // Creating, destroying and reparenting GameObjects directly inside OnValidate is
        // illegal in Unity, so defer by one editor tick.
        EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            RebuildClusters();
        };
#endif
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        foreach (var c in clusters)
        {
            if (c == null) continue;
            Gizmos.color = c.Color;
            Gizmos.DrawSphere(c.Center, 0.6f);
            foreach (var t in c.Members)
            {
                if (t == null) continue;
                Gizmos.DrawLine(c.Center, t.position);
                Gizmos.DrawWireSphere(t.position, 0.25f);
            }
        }
    }
}

// =====================================================================================

/// <summary>
/// Lives on the GameObject at the centre of a cluster, with the cluster's buildings as
/// its children. This is the handle other scripts grab: it knows its index, members,
/// centre and colour, and can move, spread, hide, tint or measure the whole group.
///
/// Created and maintained by <see cref="BuildingClusterer"/>. Don't add it by hand.
/// </summary>
[DisallowMultipleComponent]
public class BuildingCluster : MonoBehaviour
{
    [SerializeField] private int index;
    [SerializeField] private List<Transform> members = new List<Transform>();
    [SerializeField] private Vector2 centroid;
    [SerializeField] private Color color = Color.white;

    /// <summary>Index within <see cref="BuildingClusterer.Clusters"/>.</summary>
    public int Index => index;

    /// <summary>The buildings in this cluster. Read-only; the clusterer owns the list.</summary>
    public IReadOnlyList<Transform> Members => members;

    public int Count => members.Count;

    /// <summary>Cluster colour, evenly spaced around the hue wheel. Handy for tinting or UI.</summary>
    public Color Color => color;

    /// <summary>Cluster centre in the 2D projected space used for the distance maths.</summary>
    public Vector2 Centroid => centroid;

    /// <summary>World-space centre. Same as transform.position.</summary>
    public Vector3 Center => transform.position;

    internal void Configure(int index, List<Transform> members, Vector2 centroid, Vector3 worldCenter, Color color)
    {
        this.index = index;
        this.members = members;
        this.centroid = centroid;
        this.color = color;

        transform.position = worldCenter;
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] == null) continue;
            members[i].SetParent(transform, true); // keep world position
            members[i].SetSiblingIndex(i);
        }
    }

    // ------------------------------------------------------------ manipulation

    /// <summary>Moves the cluster and its buildings so the centre lands on this position.</summary>
    public void MoveTo(Vector3 worldPosition) => transform.position = worldPosition;

    /// <summary>Translates the cluster and its buildings by a world-space offset.</summary>
    public void MoveBy(Vector3 delta) => transform.position += delta;

    /// <summary>
    /// Pushes members away from (or pulls them toward) the centre. 1 = no change,
    /// 2 = twice as spread out. Building scale is untouched, only spacing.
    /// </summary>
    public void SetSpread(float factor)
    {
        foreach (var t in members)
            if (t != null) t.localPosition *= factor;
    }

    /// <summary>Shows or hides every building in the cluster.</summary>
    public void SetMembersActive(bool active)
    {
        foreach (var t in members)
            if (t != null) t.gameObject.SetActive(active);
    }

    /// <summary>
    /// Tints every renderer in the cluster via a MaterialPropertyBlock, so no material
    /// instances are created. Pass nothing to use the cluster's own colour.
    /// </summary>
    public void Tint(Color? overrideColor = null)
    {
        var c = overrideColor ?? color;
        if (_mpb == null) _mpb = new MaterialPropertyBlock();

        foreach (var t in members)
        {
            if (t == null) continue;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
            {
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", c); // URP / HDRP
                _mpb.SetColor("_Color", c);     // Built-in
                r.SetPropertyBlock(_mpb);
            }
        }
    }

    private static MaterialPropertyBlock _mpb;

    /// <summary>
    /// Recomputes the centre from the current member positions without dragging the
    /// buildings along.
    /// </summary>
    public void Recenter()
    {
        var sum = Vector3.zero;
        int n = 0;
        foreach (var t in members)
        {
            if (t == null) continue;
            sum += t.position;
            n++;
        }
        if (n == 0) return;

        var saved = new Vector3[members.Count];
        for (int i = 0; i < members.Count; i++)
            if (members[i] != null) saved[i] = members[i].position;

        transform.position = sum / n;

        for (int i = 0; i < members.Count; i++)
            if (members[i] != null) members[i].position = saved[i];
    }

    // ------------------------------------------------------------ queries

    /// <summary>World-space bounds of the cluster's renderers, falling back to positions.</summary>
    public Bounds GetBounds()
    {
        bool started = false;
        var bounds = new Bounds(transform.position, Vector3.zero);

        foreach (var t in members)
        {
            if (t == null) continue;
            var renderers = t.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                if (!started) { bounds = new Bounds(t.position, Vector3.zero); started = true; }
                else bounds.Encapsulate(t.position);
                continue;
            }

            foreach (var r in renderers)
            {
                if (!started) { bounds = r.bounds; started = true; }
                else bounds.Encapsulate(r.bounds);
            }
        }

        return bounds;
    }

    /// <summary>Distance from the centre to the farthest member.</summary>
    public float GetRadius()
    {
        float max = 0f;
        foreach (var t in members)
            if (t != null) max = Mathf.Max(max, Vector3.Distance(transform.position, t.position));
        return max;
    }

    public bool Contains(Transform building) => members.Contains(building);

    /// <summary>The member closest to a world position, or null if the cluster is empty.</summary>
    public Transform GetClosestMember(Vector3 worldPosition)
    {
        Transform best = null;
        float bestD = float.PositiveInfinity;
        foreach (var t in members)
        {
            if (t == null) continue;
            float d = (t.position - worldPosition).sqrMagnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }
}

// =====================================================================================

/// <summary>
/// K-means for 2D points. Uses k-means++ seeding and keeps the best of several restarts,
/// so results are stable for a given seed.
/// </summary>
public static class KMeans2D
{
    /// <summary>Returns a cluster index per point. K is clamped to the number of points.</summary>
    public static int[] Fit(
        IReadOnlyList<Vector2> points,
        int k,
        out Vector2[] centroids,
        int maxIterations = 100,
        int restarts = 5,
        int seed = 1)
    {
        int n = points.Count;
        centroids = new Vector2[0];
        if (n == 0 || k <= 0) return new int[0];

        k = Mathf.Min(k, n);
        var rng = new System.Random(seed);

        int[] bestLabels = null;
        Vector2[] bestCentroids = null;
        float bestInertia = float.PositiveInfinity;

        for (int r = 0; r < Mathf.Max(1, restarts); r++)
        {
            var c = SeedPlusPlus(points, k, rng);
            var labels = new int[n];

            for (int it = 0; it < maxIterations; it++)
            {
                bool changed = AssignStep(points, c, labels);
                if (it > 0 && !changed) break;
                UpdateStep(points, labels, c);
            }

            float inertia = Inertia(points, labels, c);
            if (inertia < bestInertia)
            {
                bestInertia = inertia;
                bestLabels = labels;
                bestCentroids = c;
            }
        }

        centroids = bestCentroids;
        return bestLabels;
    }

    private static Vector2[] SeedPlusPlus(IReadOnlyList<Vector2> pts, int k, System.Random rng)
    {
        int n = pts.Count;
        var c = new Vector2[k];
        c[0] = pts[rng.Next(n)];

        var d2 = new float[n];
        for (int i = 0; i < n; i++) d2[i] = (pts[i] - c[0]).sqrMagnitude;

        for (int j = 1; j < k; j++)
        {
            double total = 0;
            for (int i = 0; i < n; i++) total += d2[i];

            int chosen;
            if (total <= 0.0)
            {
                chosen = rng.Next(n); // remaining points are identical
            }
            else
            {
                double target = rng.NextDouble() * total, acc = 0;
                chosen = n - 1;
                for (int i = 0; i < n; i++)
                {
                    acc += d2[i];
                    if (acc >= target) { chosen = i; break; }
                }
            }

            c[j] = pts[chosen];
            for (int i = 0; i < n; i++)
                d2[i] = Mathf.Min(d2[i], (pts[i] - c[j]).sqrMagnitude);
        }

        return c;
    }

    private static bool AssignStep(IReadOnlyList<Vector2> pts, Vector2[] c, int[] labels)
    {
        bool changed = false;
        for (int i = 0; i < pts.Count; i++)
        {
            int best = 0;
            float bestD = float.PositiveInfinity;
            for (int j = 0; j < c.Length; j++)
            {
                float d = (pts[i] - c[j]).sqrMagnitude;
                if (d < bestD) { bestD = d; best = j; }
            }
            if (labels[i] != best) { labels[i] = best; changed = true; }
        }
        return changed;
    }

    private static void UpdateStep(IReadOnlyList<Vector2> pts, int[] labels, Vector2[] c)
    {
        int k = c.Length;
        var sums = new Vector2[k];
        var counts = new int[k];

        for (int i = 0; i < pts.Count; i++)
        {
            sums[labels[i]] += pts[i];
            counts[labels[i]]++;
        }

        for (int j = 0; j < k; j++)
        {
            if (counts[j] > 0)
            {
                c[j] = sums[j] / counts[j];
                continue;
            }

            // Empty cluster: steal the point that is currently worst served.
            int far = -1;
            float farD = -1f;
            for (int i = 0; i < pts.Count; i++)
            {
                if (counts[labels[i]] <= 1) continue; // don't empty another cluster
                float d = (pts[i] - c[labels[i]]).sqrMagnitude;
                if (d > farD) { farD = d; far = i; }
            }
            if (far >= 0)
            {
                counts[labels[far]]--;
                labels[far] = j;
                counts[j] = 1;
                c[j] = pts[far];
            }
        }
    }

    private static float Inertia(IReadOnlyList<Vector2> pts, int[] labels, Vector2[] c)
    {
        float sum = 0f;
        for (int i = 0; i < pts.Count; i++) sum += (pts[i] - c[labels[i]]).sqrMagnitude;
        return sum;
    }
}
