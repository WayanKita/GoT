using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

// =====================================================================================
//  BuildingClusterer.cs
//
//  Clusters direct child objects based on their spatial position.
//
//  Position mode:
//      Pivot        -> Uses the Transform pivot.
//      BoundsCenter -> Uses the combined Renderer bounds center.
//
//  Hierarchy:
//
//      BuildingClusterer
//        |- Cluster 0
//        |    |- Building_A
//        |    |- Building_B
//        |
//        |- Cluster 1
//             |- Building_C
//
//  Children inside Building_A / Building_B / etc. remain untouched.
// =====================================================================================

[DisallowMultipleComponent]
public class BuildingClusterer : MonoBehaviour
{
    public enum ProjectionPlane
    {
        XY,
        XZ,
        YZ
    }

    public enum MemberOrder
    {
        InputOrder,
        Alphabetical,
        DistanceToCenter
    }

    public enum ClusterPositionMode
    {
        Pivot,
        BoundsCenter
    }

    // ---------------------------------------------------------------- INPUT

    [Header("Input")]

    [Tooltip("The objects to cluster. Nulls are ignored.")]
    public List<Transform> buildings = new List<Transform>();


    // ---------------------------------------------------------------- CLUSTERING

    [Header("Clustering")]

    [Min(1)]
    public int clusterCount = 5;

    [Tooltip(
        "Pivot uses the object's Transform position. " +
        "Bounds Center uses the combined Renderer bounds of the object and its descendants."
    )]
    public ClusterPositionMode positionMode = ClusterPositionMode.Pivot;

    [Tooltip(
        "Which two axes to measure distance on. " +
        "XY ignores Z, XZ ignores Y (Unity's usual ground plane), YZ ignores X."
    )]
    public ProjectionPlane plane = ProjectionPlane.XY;

    [Min(1)]
    public int maxIterations = 100;

    [Tooltip(
        "Independent runs; the tightest result wins. " +
        "Higher = more stable, slightly slower."
    )]
    [Min(1)]
    public int restarts = 5;

    [Tooltip("Same seed + same input = same clusters.")]
    public int seed = 1;


    // ---------------------------------------------------------------- HIERARCHY

    [Header("Hierarchy")]

    [Tooltip(
        "Optional prefab for the cluster centre. " +
        "A plain empty GameObject is used if unset."
    )]
    public GameObject clusterCenterPrefab;

    [Tooltip("{0} = index, {1} = member count.")]
    public string clusterNameFormat = "Cluster {0} ({1})";

    [Tooltip("How the buildings are ordered underneath their cluster.")]
    public MemberOrder memberOrder = MemberOrder.DistanceToCenter;


    // ---------------------------------------------------------------- EDITOR

    [Header("Editor")]

    public bool autoRebuild = true;

    public bool drawGizmos = true;

    public bool colorizeMembers = false;


    // ---------------------------------------------------------------- INTERNAL DATA

    [SerializeField, HideInInspector]
    private List<BuildingCluster> clusters = new List<BuildingCluster>();


    /// <summary>
    /// The cluster centre objects, index-aligned with cluster indices.
    /// </summary>
    public IReadOnlyList<BuildingCluster> Clusters => clusters;


    /// <summary>
    /// Fires after every rebuild, once the cluster objects are valid.
    /// </summary>
    public event Action<BuildingClusterer> ClustersRebuilt;


    /// <summary>
    /// Convenience handle. Only meaningful if there's one clusterer in the scene.
    /// </summary>
    public static BuildingClusterer Instance { get; private set; }


    // =================================================================================
    // UNITY
    // =================================================================================

    private void Awake()
    {
        if (Instance == null)
            Instance = this;

        if (clusters.Count == 0 && buildings.Count > 0)
            RebuildClusters();
    }


    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }


    // =================================================================================
    // PUBLIC API
    // =================================================================================

    public void SetClusterCount(int k)
    {
        clusterCount = Mathf.Max(1, k);
        RebuildClusters();
    }


    /// <summary>
    /// Overload for hooking straight to a UI slider's OnValueChanged.
    /// </summary>
    public void SetClusterCount(float k)
    {
        SetClusterCount(Mathf.RoundToInt(k));
    }


    public BuildingCluster GetCluster(int index)
    {
        return index >= 0 && index < clusters.Count
            ? clusters[index]
            : null;
    }


    /// <summary>
    /// Finds the cluster a given building ended up in.
    /// </summary>
    public BuildingCluster GetClusterOf(Transform building)
    {
        foreach (var c in clusters)
        {
            if (c != null && c.Contains(building))
                return c;
        }

        return null;
    }


    /// <summary>
    /// Returns the cluster whose centre is nearest a world position.
    /// </summary>
    public BuildingCluster GetNearestCluster(Vector3 worldPosition)
    {
        BuildingCluster best = null;
        float bestD = float.PositiveInfinity;

        foreach (var c in clusters)
        {
            if (c == null)
                continue;

            float d = (c.Center - worldPosition).sqrMagnitude;

            if (d < bestD)
            {
                bestD = d;
                best = c;
            }
        }

        return best;
    }


    // =================================================================================
    // CLUSTERING
    // =================================================================================

    [ContextMenu("Rebuild Clusters")]
    public void RebuildClusters()
    {
        // Pull existing members back out of generated cluster objects.
        DetachAllMembers();


        // -------------------------------------------------------------------------
        // Build clean list
        // -------------------------------------------------------------------------

        var valid = new List<Transform>(buildings.Count);

        foreach (var t in buildings)
        {
            if (t != null)
                valid.Add(t);
        }


        // -------------------------------------------------------------------------
        // Determine clustering positions
        // -------------------------------------------------------------------------

        var points = new Vector2[valid.Count];

        for (int i = 0; i < valid.Count; i++)
        {
            Vector3 clusterPosition = GetClusterPosition(valid[i]);

            points[i] = Project(clusterPosition);
        }


        // -------------------------------------------------------------------------
        // K-Means
        // -------------------------------------------------------------------------

        Vector2[] centroids;

        int[] labels = KMeans2D.Fit(
            points,
            clusterCount,
            out centroids,
            maxIterations,
            restarts,
            seed
        );

        int k = centroids?.Length ?? 0;


        // -------------------------------------------------------------------------
        // Bucket members
        // -------------------------------------------------------------------------

        var buckets = new List<Transform>[k];

        for (int j = 0; j < k; j++)
        {
            buckets[j] = new List<Transform>();
        }

        for (int i = 0; i < valid.Count; i++)
        {
            buckets[labels[i]].Add(valid[i]);
        }


        // -------------------------------------------------------------------------
        // Create / reuse cluster objects
        // -------------------------------------------------------------------------

        SyncClusterObjects(k);


        // -------------------------------------------------------------------------
        // Configure clusters
        // -------------------------------------------------------------------------

        for (int j = 0; j < k; j++)
        {
            Vector3 center = Unproject(
                centroids[j],
                AverageDepth(buckets[j])
            );

            SortMembers(buckets[j], center);

            var cluster = clusters[j];

            cluster.name = string.Format(
                clusterNameFormat,
                j,
                buckets[j].Count
            );

            cluster.transform.SetSiblingIndex(j);

            cluster.Configure(
                j,
                buckets[j],
                centroids[j],
                center,
                ClusterColor(j)
            );
        }


        // -------------------------------------------------------------------------
        // Optional colouring
        // -------------------------------------------------------------------------

        if (colorizeMembers)
        {
            foreach (var c in clusters)
            {
                if (c != null)
                    c.Tint();
            }
        }


        MarkDirty();

        ClustersRebuilt?.Invoke(this);
    }


    // =================================================================================
    // POSITION CALCULATION
    // =================================================================================

    /// <summary>
    /// Returns the position used for clustering.
    ///
    /// Pivot:
    ///     Uses Transform.position.
    ///
    /// BoundsCenter:
    ///     Calculates the combined world-space Renderer bounds for the building.
    ///     Descendants contribute to the bounds but are NOT clustered individually.
    /// </summary>
    private Vector3 GetClusterPosition(Transform t)
    {
        if (t == null)
            return Vector3.zero;


        // Pivot mode
        if (positionMode == ClusterPositionMode.Pivot)
        {
            return t.position;
        }


        // Bounds Center mode
        Renderer[] renderers = t.GetComponentsInChildren<Renderer>();

        if (renderers == null || renderers.Length == 0)
        {
            // No renderer available -> fall back to pivot.
            return t.position;
        }


        bool boundsStarted = false;
        Bounds combinedBounds = new Bounds();


        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            if (!boundsStarted)
            {
                combinedBounds = renderer.bounds;
                boundsStarted = true;
            }
            else
            {
                combinedBounds.Encapsulate(renderer.bounds);
            }
        }


        if (!boundsStarted)
        {
            return t.position;
        }


        return combinedBounds.center;
    }


    // =================================================================================
    // POPULATE
    // =================================================================================

    /// <summary>
    /// Adds ONLY the immediate children of this GameObject.
    ///
    /// Children-of-children are NOT added as individual buildings.
    /// </summary>
    [ContextMenu("Populate From Children")]
    public void PopulateFromChildren()
    {
        buildings.Clear();

        foreach (Transform child in transform)
        {
            // Don't accidentally add generated cluster objects.
            if (child.GetComponent<BuildingCluster>() != null)
                continue;

            buildings.Add(child);
        }

        RebuildClusters();
    }


    // =================================================================================
    // UNGROUP
    // =================================================================================

    /// <summary>
    /// Flattens the generated cluster hierarchy.
    /// Buildings are returned directly underneath this GameObject.
    /// </summary>
    [ContextMenu("Ungroup")]
    public void Ungroup()
    {
        DetachAllMembers();
        SyncClusterObjects(0);
        MarkDirty();
    }


    // =================================================================================
    // PROJECTION
    // =================================================================================

    public Vector2 Project(Vector3 p)
    {
        switch (plane)
        {
            case ProjectionPlane.XZ:
                return new Vector2(p.x, p.z);

            case ProjectionPlane.YZ:
                return new Vector2(p.y, p.z);

            default:
                return new Vector2(p.x, p.y);
        }
    }


    public Vector3 Unproject(Vector2 p, float depth)
    {
        switch (plane)
        {
            case ProjectionPlane.XZ:
                return new Vector3(p.x, depth, p.y);

            case ProjectionPlane.YZ:
                return new Vector3(depth, p.x, p.y);

            default:
                return new Vector3(p.x, p.y, depth);
        }
    }


    /// <summary>
    /// Evenly spaced hues.
    /// </summary>
    public static Color ClusterColor(int index)
    {
        return Color.HSVToRGB(
            (index * 0.61803f) % 1f,
            0.65f,
            1f
        );
    }


    // =================================================================================
    // INTERNALS
    // =================================================================================

    /// <summary>
    /// Returns ONLY the objects explicitly contained in the buildings list
    /// back to the BuildingClusterer's transform.
    ///
    /// Descendants of those objects remain untouched.
    /// </summary>
    private void DetachAllMembers()
    {
        foreach (var t in buildings)
        {
            if (t == null)
                continue;

            if (t.parent != transform)
            {
                t.SetParent(transform, true);
            }
        }
    }


    private void SyncClusterObjects(int k)
    {
        clusters.RemoveAll(c => c == null);


        // Remove surplus cluster objects.
        for (int j = clusters.Count - 1; j >= k; j--)
        {
            SafeDestroy(clusters[j].gameObject);

            clusters.RemoveAt(j);
        }


        // Create missing cluster objects.
        while (clusters.Count < k)
        {
            GameObject go;

            if (clusterCenterPrefab != null)
            {
                go = Instantiate(
                    clusterCenterPrefab,
                    transform
                );
            }
            else
            {
                go = new GameObject("Cluster");
            }


            go.transform.SetParent(
                transform,
                false
            );


            var cluster = go.GetComponent<BuildingCluster>();

            if (cluster == null)
            {
                cluster = go.AddComponent<BuildingCluster>();
            }


            clusters.Add(cluster);
        }
    }


    private void SortMembers(
        List<Transform> members,
        Vector3 center
    )
    {
        switch (memberOrder)
        {
            case MemberOrder.Alphabetical:

                members.Sort(
                    (a, b) =>
                        string.CompareOrdinal(
                            a.name,
                            b.name
                        )
                );

                break;


            case MemberOrder.DistanceToCenter:

                members.Sort(
                    (a, b) =>
                        (GetClusterPosition(a) - center)
                        .sqrMagnitude
                        .CompareTo(
                            (GetClusterPosition(b) - center)
                            .sqrMagnitude
                        )
                );

                break;
        }
    }


    /// <summary>
    /// Calculates the average ignored-axis position.
    /// Uses the selected Pivot / Bounds Center mode.
    /// </summary>
    private float AverageDepth(List<Transform> group)
    {
        if (group.Count == 0)
            return 0f;


        float sum = 0f;


        foreach (var t in group)
        {
            Vector3 p = GetClusterPosition(t);

            switch (plane)
            {
                case ProjectionPlane.XZ:

                    sum += p.y;

                    break;


                case ProjectionPlane.YZ:

                    sum += p.x;

                    break;


                default:

                    sum += p.z;

                    break;
            }
        }


        return sum / group.Count;
    }


    private static void SafeDestroy(UnityEngine.Object o)
    {
        if (o == null)
            return;


        if (Application.isPlaying)
        {
            Destroy(o);
        }
        else
        {
            DestroyImmediate(o);
        }
    }


    private void MarkDirty()
    {
#if UNITY_EDITOR

        if (Application.isPlaying)
            return;


        EditorUtility.SetDirty(this);


        if (gameObject.scene.IsValid())
        {
            UnityEditor.SceneManagement.EditorSceneManager
                .MarkSceneDirty(gameObject.scene);
        }

#endif
    }


    // =================================================================================
    // VALIDATION
    // =================================================================================

    private void OnValidate()
    {
        clusterCount = Mathf.Max(
            1,
            clusterCount
        );


        if (!autoRebuild)
            return;


#if UNITY_EDITOR

        // Creating / destroying / reparenting GameObjects directly
        // inside OnValidate is unsafe, so defer by one editor tick.

        EditorApplication.delayCall += () =>
        {
            if (this == null)
                return;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            RebuildClusters();
        };

#endif
    }


    // =================================================================================
    // GIZMOS
    // =================================================================================

    private void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;


        foreach (var c in clusters)
        {
            if (c == null)
                continue;


            Gizmos.color = c.Color;

            Gizmos.DrawSphere(
                c.Center,
                0.6f
            );


            foreach (var t in c.Members)
            {
                if (t == null)
                    continue;


                Vector3 memberPosition =
                    GetClusterPosition(t);


                Gizmos.DrawLine(
                    c.Center,
                    memberPosition
                );


                Gizmos.DrawWireSphere(
                    memberPosition,
                    0.25f
                );
            }
        }
    }
}


// =====================================================================================
// BUILDING CLUSTER
// =====================================================================================

/// <summary>
/// Represents one generated cluster.
/// </summary>
[DisallowMultipleComponent]
public class BuildingCluster : MonoBehaviour
{
    [SerializeField]
    private int index;


    [SerializeField]
    private List<Transform> members =
        new List<Transform>();


    [SerializeField]
    private Vector2 centroid;


    [SerializeField]
    private Color color = Color.white;


    public int Index => index;

    public IReadOnlyList<Transform> Members => members;

    public int Count => members.Count;

    public Color Color => color;

    public Vector2 Centroid => centroid;

    public Vector3 Center => transform.position;


    // =================================================================================
    // CONFIGURATION
    // =================================================================================

    internal void Configure(
        int index,
        List<Transform> members,
        Vector2 centroid,
        Vector3 worldCenter,
        Color color
    )
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
            if (members[i] == null)
                continue;


            members[i].SetParent(
                transform,
                true
            );


            members[i].SetSiblingIndex(i);
        }
    }


    // =================================================================================
    // MANIPULATION
    // =================================================================================

    public void MoveTo(Vector3 worldPosition)
    {
        transform.position = worldPosition;
    }


    public void MoveBy(Vector3 delta)
    {
        transform.position += delta;
    }


    /// <summary>
    /// 1 = unchanged.
    /// 2 = twice as spread out.
    /// </summary>
    public void SetSpread(float factor)
    {
        foreach (var t in members)
        {
            if (t != null)
            {
                t.localPosition *= factor;
            }
        }
    }


    public void SetMembersActive(bool active)
    {
        foreach (var t in members)
        {
            if (t != null)
            {
                t.gameObject.SetActive(active);
            }
        }
    }


    // =================================================================================
    // TINT
    // =================================================================================

    private static MaterialPropertyBlock _mpb;


    /// <summary>
    /// Tints renderers belonging to every building.
    /// </summary>
    public void Tint(Color? overrideColor = null)
    {
        Color c = overrideColor ?? color;


        if (_mpb == null)
        {
            _mpb = new MaterialPropertyBlock();
        }


        foreach (var t in members)
        {
            if (t == null)
                continue;


            Renderer[] renderers =
                t.GetComponentsInChildren<Renderer>();


            foreach (var r in renderers)
            {
                r.GetPropertyBlock(_mpb);

                // URP / HDRP
                _mpb.SetColor(
                    "_BaseColor",
                    c
                );

                // Built-in pipeline
                _mpb.SetColor(
                    "_Color",
                    c
                );

                r.SetPropertyBlock(_mpb);
            }
        }
    }


    // =================================================================================
    // RECENTER
    // =================================================================================

    /// <summary>
    /// Recomputes the centre using member pivot positions without
    /// dragging the buildings along.
    /// </summary>
    public void Recenter()
    {
        Vector3 sum = Vector3.zero;

        int n = 0;


        foreach (var t in members)
        {
            if (t == null)
                continue;


            sum += t.position;

            n++;
        }


        if (n == 0)
            return;


        var saved =
            new Vector3[members.Count];


        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] != null)
            {
                saved[i] =
                    members[i].position;
            }
        }


        transform.position =
            sum / n;


        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] != null)
            {
                members[i].position =
                    saved[i];
            }
        }
    }


    // =================================================================================
    // QUERIES
    // =================================================================================

    public Bounds GetBounds()
    {
        bool started = false;

        Bounds bounds =
            new Bounds(
                transform.position,
                Vector3.zero
            );


        foreach (var t in members)
        {
            if (t == null)
                continue;


            Renderer[] renderers =
                t.GetComponentsInChildren<Renderer>();


            if (renderers.Length == 0)
            {
                if (!started)
                {
                    bounds =
                        new Bounds(
                            t.position,
                            Vector3.zero
                        );

                    started = true;
                }
                else
                {
                    bounds.Encapsulate(
                        t.position
                    );
                }


                continue;
            }


            foreach (var r in renderers)
            {
                if (!started)
                {
                    bounds = r.bounds;

                    started = true;
                }
                else
                {
                    bounds.Encapsulate(
                        r.bounds
                    );
                }
            }
        }


        return bounds;
    }


    /// <summary>
    /// Distance from the cluster centre to the farthest member pivot.
    /// </summary>
    public float GetRadius()
    {
        float max = 0f;


        foreach (var t in members)
        {
            if (t != null)
            {
                max = Mathf.Max(
                    max,
                    Vector3.Distance(
                        transform.position,
                        t.position
                    )
                );
            }
        }


        return max;
    }


    public bool Contains(Transform building)
    {
        return members.Contains(building);
    }


    public Transform GetClosestMember(
        Vector3 worldPosition
    )
    {
        Transform best = null;

        float bestD =
            float.PositiveInfinity;


        foreach (var t in members)
        {
            if (t == null)
                continue;


            float d =
                (t.position - worldPosition)
                .sqrMagnitude;


            if (d < bestD)
            {
                bestD = d;

                best = t;
            }
        }


        return best;
    }
}


// =====================================================================================
// K-MEANS 2D
// =====================================================================================

/// <summary>
/// K-means clustering for 2D points.
///
/// Uses k-means++ seeding and keeps the best result from several restarts.
/// </summary>
public static class KMeans2D
{
    public static int[] Fit(
        IReadOnlyList<Vector2> points,
        int k,
        out Vector2[] centroids,
        int maxIterations = 100,
        int restarts = 5,
        int seed = 1
    )
    {
        int n = points.Count;


        centroids =
            new Vector2[0];


        if (n == 0 || k <= 0)
        {
            return new int[0];
        }


        k = Mathf.Min(
            k,
            n
        );


        var rng =
            new System.Random(seed);


        int[] bestLabels = null;

        Vector2[] bestCentroids = null;

        float bestInertia =
            float.PositiveInfinity;


        for (
            int r = 0;
            r < Mathf.Max(1, restarts);
            r++
        )
        {
            Vector2[] c =
                SeedPlusPlus(
                    points,
                    k,
                    rng
                );


            int[] labels =
                new int[n];


            for (
                int it = 0;
                it < maxIterations;
                it++
            )
            {
                bool changed =
                    AssignStep(
                        points,
                        c,
                        labels
                    );


                if (it > 0 && !changed)
                {
                    break;
                }


                UpdateStep(
                    points,
                    labels,
                    c
                );
            }


            float inertia =
                Inertia(
                    points,
                    labels,
                    c
                );


            if (inertia < bestInertia)
            {
                bestInertia =
                    inertia;

                bestLabels =
                    labels;

                bestCentroids =
                    c;
            }
        }


        centroids =
            bestCentroids;


        return bestLabels;
    }


    // =================================================================================
    // K-MEANS++ SEEDING
    // =================================================================================

    private static Vector2[] SeedPlusPlus(
        IReadOnlyList<Vector2> pts,
        int k,
        System.Random rng
    )
    {
        int n = pts.Count;


        var c =
            new Vector2[k];


        c[0] =
            pts[rng.Next(n)];


        var d2 =
            new float[n];


        for (int i = 0; i < n; i++)
        {
            d2[i] =
                (pts[i] - c[0])
                .sqrMagnitude;
        }


        for (int j = 1; j < k; j++)
        {
            double total = 0;


            for (int i = 0; i < n; i++)
            {
                total += d2[i];
            }


            int chosen;


            if (total <= 0.0)
            {
                chosen =
                    rng.Next(n);
            }
            else
            {
                double target =
                    rng.NextDouble() * total;

                double acc = 0;


                chosen = n - 1;


                for (int i = 0; i < n; i++)
                {
                    acc += d2[i];


                    if (acc >= target)
                    {
                        chosen = i;

                        break;
                    }
                }
            }


            c[j] =
                pts[chosen];


            for (int i = 0; i < n; i++)
            {
                d2[i] =
                    Mathf.Min(
                        d2[i],
                        (pts[i] - c[j])
                        .sqrMagnitude
                    );
            }
        }


        return c;
    }


    // =================================================================================
    // ASSIGNMENT
    // =================================================================================

    private static bool AssignStep(
        IReadOnlyList<Vector2> pts,
        Vector2[] c,
        int[] labels
    )
    {
        bool changed = false;


        for (int i = 0; i < pts.Count; i++)
        {
            int best = 0;

            float bestD =
                float.PositiveInfinity;


            for (int j = 0; j < c.Length; j++)
            {
                float d =
                    (pts[i] - c[j])
                    .sqrMagnitude;


                if (d < bestD)
                {
                    bestD = d;

                    best = j;
                }
            }


            if (labels[i] != best)
            {
                labels[i] = best;

                changed = true;
            }
        }


        return changed;
    }


    // =================================================================================
    // UPDATE
    // =================================================================================

    private static void UpdateStep(
        IReadOnlyList<Vector2> pts,
        int[] labels,
        Vector2[] c
    )
    {
        int k = c.Length;


        var sums =
            new Vector2[k];


        var counts =
            new int[k];


        for (int i = 0; i < pts.Count; i++)
        {
            sums[labels[i]] +=
                pts[i];

            counts[labels[i]]++;
        }


        for (int j = 0; j < k; j++)
        {
            if (counts[j] > 0)
            {
                c[j] =
                    sums[j] /
                    counts[j];

                continue;
            }


            // Empty cluster:
            // steal the point currently worst served.

            int far = -1;

            float farD = -1f;


            for (int i = 0; i < pts.Count; i++)
            {
                if (counts[labels[i]] <= 1)
                {
                    continue;
                }


                float d =
                    (pts[i] - c[labels[i]])
                    .sqrMagnitude;


                if (d > farD)
                {
                    farD = d;

                    far = i;
                }
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


    // =================================================================================
    // INERTIA
    // =================================================================================

    private static float Inertia(
        IReadOnlyList<Vector2> pts,
        int[] labels,
        Vector2[] c
    )
    {
        float sum = 0f;


        for (int i = 0; i < pts.Count; i++)
        {
            sum +=
                (pts[i] - c[labels[i]])
                .sqrMagnitude;
        }


        return sum;
    }
}