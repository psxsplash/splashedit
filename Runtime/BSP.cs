using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics;
using SplashEdit.RuntimeCode;

public class BSP
{
    private List<PSXObjectExporter> _objects;
    private Node root;
    private const float EPSILON = 1e-6f;
    private const int MAX_TRIANGLES_PER_LEAF = 256;
    private const int MAX_TREE_DEPTH = 50;
    private const int CANDIDATE_PLANE_COUNT = 15;

    // Statistics
    private int totalTrianglesProcessed;
    private int totalSplits;
    private int treeDepth;
    private Stopwatch buildTimer;

    public bool verboseLogging = false;

    // Store the triangle that was used for the split plane for debugging
    private Dictionary<Node, Triangle> splitPlaneTriangles = new Dictionary<Node, Triangle>();

    private struct Triangle
    {
        public Vector3 v0;
        public Vector3 v1;
        public Vector3 v2;
        public Vector3 n0;
        public Vector3 n1;
        public Vector3 n2;
        public Vector2 uv0;
        public Vector2 uv1;
        public Vector2 uv2;
        public Plane plane;
        public Bounds bounds;
        public PSXObjectExporter sourceExporter;
        public int materialIndex; // Store material index instead of submesh index

        public Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc,
                       Vector2 uva, Vector2 uvb, Vector2 uvc, PSXObjectExporter exporter, int matIndex)
        {
            v0 = a;
            v1 = b;
            v2 = c;
            n0 = na;
            n1 = nb;
            n2 = nc;
            uv0 = uva;
            uv1 = uvb;
            uv2 = uvc;
            sourceExporter = exporter;
            materialIndex = matIndex;

            // Calculate plane
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 normal = Vector3.Cross(edge1, edge2);

            if (normal.sqrMagnitude < 1e-4f)
            {
                plane = new Plane(Vector3.up, 0);
            }
            else
            {
                normal.Normalize();
                plane = new Plane(normal, v0);
            }

            // Calculate bounds
            bounds = new Bounds(v0, Vector3.zero);
            bounds.Encapsulate(v1);
            bounds.Encapsulate(v2);
        }

        public void Transform(Matrix4x4 matrix)
        {
            v0 = matrix.MultiplyPoint3x4(v0);
            v1 = matrix.MultiplyPoint3x4(v1);
            v2 = matrix.MultiplyPoint3x4(v2);

            // Transform normals (using inverse transpose for correct scaling)
            Matrix4x4 invTranspose = matrix.inverse.transpose;
            n0 = invTranspose.MultiplyVector(n0).normalized;
            n1 = invTranspose.MultiplyVector(n1).normalized;
            n2 = invTranspose.MultiplyVector(n2).normalized;

            // Recalculate plane and bounds after transformation
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 normal = Vector3.Cross(edge1, edge2);

            if (normal.sqrMagnitude < 1e-4f)
            {
                plane = new Plane(Vector3.up, 0);
            }
            else
            {
                normal.Normalize();
                plane = new Plane(normal, v0);
            }

            bounds = new Bounds(v0, Vector3.zero);
            bounds.Encapsulate(v1);
            bounds.Encapsulate(v2);
        }
    }

    private class Node
    {
        public Plane plane;
        public Node front;
        public Node back;
        public List<Triangle> triangles;
        public bool isLeaf = false;
        public Bounds bounds;
        public int depth;
        public int triangleSourceIndex = -1;
    }

    public BSP(List<PSXObjectExporter> objects)
    {
        _objects = objects;
        buildTimer = new Stopwatch();
    }

    public void Build()
    {
        buildTimer.Start();

        List<Triangle> triangles = ExtractTrianglesFromMeshes();
        totalTrianglesProcessed = triangles.Count;

        if (verboseLogging)
            UnityEngine.Debug.Log($"Starting BSP build with {totalTrianglesProcessed} triangles");

        if (triangles.Count == 0)
        {
            root = null;
            return;
        }

        // Calculate overall bounds
        Bounds overallBounds = CalculateBounds(triangles);

        // Build tree recursively with depth tracking
        root = BuildNode(triangles, overallBounds, 0);

        // Create modified meshes for all exporters
        CreateModifiedMeshes();

        buildTimer.Stop();

        if (verboseLogging)
        {
            UnityEngine.Debug.Log($"BSP build completed in {buildTimer.Elapsed.TotalMilliseconds}ms");
            UnityEngine.Debug.Log($"Total splits: {totalSplits}, Max depth: {treeDepth}");
        }
    }

    private List<Triangle> ExtractTrianglesFromMeshes()
    {
        List<Triangle> triangles = new List<Triangle>();

        foreach (var meshObj in _objects)
        {
            if (!meshObj.IsActive) continue;

            MeshFilter mf = meshObj.GetComponent<MeshFilter>();
            Renderer renderer = meshObj.GetComponent<Renderer>();
            if (mf == null || mf.sharedMesh == null || renderer == null) continue;

            Mesh mesh = mf.sharedMesh;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals.Length > 0 ? mesh.normals : new Vector3[vertices.Length];
            Vector2[] uvs = mesh.uv.Length > 0 ? mesh.uv : new Vector2[vertices.Length];
            Matrix4x4 matrix = meshObj.transform.localToWorldMatrix;

            // Handle case where normals are missing
            if (mesh.normals.Length == 0)
            {
                for (int i = 0; i < normals.Length; i++)
                {
                    normals[i] = Vector3.up;
                }
            }

            // Handle case where UVs are missing
            if (mesh.uv.Length == 0)
            {
                for (int i = 0; i < uvs.Length; i++)
                {
                    uvs[i] = Vector2.zero;
                }
            }

            // Process each submesh and track material index
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                int materialIndex = Mathf.Min(submesh, renderer.sharedMaterials.Length - 1);
                int[] indices = mesh.GetTriangles(submesh);

                for (int i = 0; i < indices.Length; i += 3)
                {
                    int idx0 = indices[i];
                    int idx1 = indices[i + 1];
                    int idx2 = indices[i + 2];

                    Vector3 v0 = vertices[idx0];
                    Vector3 v1 = vertices[idx1];
                    Vector3 v2 = vertices[idx2];

                    // Skip degenerate triangles
                    if (Vector3.Cross(v1 - v0, v2 - v0).sqrMagnitude < 1e-4f)
                        continue;

                    Vector3 n0 = normals[idx0];
                    Vector3 n1 = normals[idx1];
                    Vector3 n2 = normals[idx2];

                    Vector2 uv0 = uvs[idx0];
                    Vector2 uv1 = uvs[idx1];
                    Vector2 uv2 = uvs[idx2];

                    Triangle tri = new Triangle(v0, v1, v2, n0, n1, n2, uv0, uv1, uv2, meshObj, materialIndex);
                    tri.Transform(matrix);
                    triangles.Add(tri);
                }
            }
        }

        return triangles;
    }

    private Node BuildNode(List<Triangle> triangles, Bounds bounds, int depth)
    {
        if (triangles == null || triangles.Count == 0)
            return null;

        Node node = new Node
        {
            triangles = new List<Triangle>(),
            bounds = bounds,
            depth = depth
        };

        treeDepth = Mathf.Max(treeDepth, depth);

        // Create leaf node if conditions are met
        if (triangles.Count <= MAX_TRIANGLES_PER_LEAF || depth >= MAX_TREE_DEPTH)
        {
            node.isLeaf = true;
            node.triangles = triangles;

            if (verboseLogging && depth >= MAX_TREE_DEPTH)
                UnityEngine.Debug.LogWarning($"Max tree depth reached at depth {depth} with {triangles.Count} triangles");

            return node;
        }

        // Select the best splitting plane using multiple strategies
        Triangle? splitTriangle = null;
        if (!SelectBestSplittingPlane(triangles, bounds, out node.plane, out splitTriangle))
        {
            // Fallback: create leaf if no good split found
            node.isLeaf = true;
            node.triangles = triangles;

            if (verboseLogging)
                UnityEngine.Debug.Log($"Created leaf node with {triangles.Count} triangles (no good split found)");

            return node;
        }

        // Store the triangle that provided the split plane for debugging
        if (splitTriangle.HasValue)
        {
            splitPlaneTriangles[node] = splitTriangle.Value;
        }

        List<Triangle> frontList = new List<Triangle>();
        List<Triangle> backList = new List<Triangle>();
        List<Triangle> coplanarList = new List<Triangle>();

        // Classify all triangles
        foreach (var tri in triangles)
        {
            ClassifyTriangle(tri, node.plane, coplanarList, frontList, backList);
        }

        // Handle cases where splitting doesn't provide benefit
        if (frontList.Count == 0 || backList.Count == 0)
        {
            // If split doesn't separate geometry, create a leaf
            node.isLeaf = true;
            node.triangles = triangles;

            if (verboseLogging)
                UnityEngine.Debug.Log($"Created leaf node with {triangles.Count} triangles (ineffective split)");

            return node;
        }

        // Distribute coplanar triangles to the side with fewer triangles
        if (coplanarList.Count > 0)
        {
            if (frontList.Count <= backList.Count)
            {
                frontList.AddRange(coplanarList);
            }
            else
            {
                backList.AddRange(coplanarList);
            }
        }

        if (verboseLogging)
            UnityEngine.Debug.Log($"Node at depth {depth}: {triangles.Count} triangles -> {frontList.Count} front, {backList.Count} back");

        // Calculate bounds for children
        Bounds frontBounds = CalculateBounds(frontList);
        Bounds backBounds = CalculateBounds(backList);

        // Recursively build child nodes
        node.front = BuildNode(frontList, frontBounds, depth + 1);
        node.back = BuildNode(backList, backBounds, depth + 1);

        return node;
    }

    private bool SelectBestSplittingPlane(List<Triangle> triangles, Bounds bounds, out Plane bestPlane, out Triangle? splitTriangle)
    {
        bestPlane = new Plane();
        splitTriangle = null;
        int bestScore = int.MaxValue;
        bool foundValidPlane = false;

        // Strategy 1: Try planes from triangle centroids
        int candidatesToTry = Mathf.Min(CANDIDATE_PLANE_COUNT, triangles.Count);
        for (int i = 0; i < candidatesToTry; i++)
        {
            Triangle tri = triangles[i];
            Plane candidate = tri.plane;

            int score = EvaluateSplitPlane(triangles, candidate);
            if (score < bestScore && score >= 0)
            {
                bestScore = score;
                bestPlane = candidate;
                splitTriangle = tri;
                foundValidPlane = true;
            }
        }

        // Strategy 2: Try axis-aligned planes through bounds center
        if (!foundValidPlane || bestScore > triangles.Count * 3)
        {
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            for (int i = 0; i < 3; i++)
            {
                Plane candidate = new Plane(axes[i], bounds.center);
                int score = EvaluateSplitPlane(triangles, candidate);
                if (score < bestScore && score >= 0)
                {
                    bestScore = score;
                    bestPlane = candidate;
                    splitTriangle = null;
                    foundValidPlane = true;
                }
            }
        }

        // Strategy 3: Try planes based on bounds extents
        if (!foundValidPlane)
        {
            Vector3 extents = bounds.extents;
            if (extents.x >= extents.y && extents.x >= extents.z)
                bestPlane = new Plane(Vector3.right, bounds.center);
            else if (extents.y >= extents.x && extents.y >= extents.z)
                bestPlane = new Plane(Vector3.up, bounds.center);
            else
                bestPlane = new Plane(Vector3.forward, bounds.center);

            splitTriangle = null;
            foundValidPlane = true;
        }

        return foundValidPlane;
    }

    private int EvaluateSplitPlane(List<Triangle> triangles, Plane plane)
    {
        int frontCount = 0;
        int backCount = 0;
        int splitCount = 0;
        int coplanarCount = 0;

        foreach (var tri in triangles)
        {
            float d0 = plane.GetDistanceToPoint(tri.v0);
            float d1 = plane.GetDistanceToPoint(tri.v1);
            float d2 = plane.GetDistanceToPoint(tri.v2);

            // Check for NaN/infinity
            if (float.IsNaN(d0) || float.IsNaN(d1) || float.IsNaN(d2) ||
                float.IsInfinity(d0) || float.IsInfinity(d1) || float.IsInfinity(d2))
            {
                return int.MaxValue;
            }

            bool front = d0 > EPSILON || d1 > EPSILON || d2 > EPSILON;
            bool back = d0 < -EPSILON || d1 < -EPSILON || d2 < -EPSILON;

            if (front && back)
                splitCount++;
            else if (front)
                frontCount++;
            else if (back)
                backCount++;
            else
                coplanarCount++;
        }

        // Reject planes that would cause too many splits or imbalanced trees
        if (splitCount > triangles.Count / 2)
            return int.MaxValue;

        // Score based on balance and split count
        return Mathf.Abs(frontCount - backCount) + splitCount * 2;
    }

    private void ClassifyTriangle(Triangle tri, Plane plane, List<Triangle> coplanar, List<Triangle> front, List<Triangle> back)
    {
        float d0 = plane.GetDistanceToPoint(tri.v0);
        float d1 = plane.GetDistanceToPoint(tri.v1);
        float d2 = plane.GetDistanceToPoint(tri.v2);

        // Check for numerical issues
        if (float.IsNaN(d0) || float.IsNaN(d1) || float.IsNaN(d2) ||
            float.IsInfinity(d0) || float.IsInfinity(d1) || float.IsInfinity(d2))
        {
            coplanar.Add(tri);
            return;
        }

        bool front0 = d0 > EPSILON;
        bool front1 = d1 > EPSILON;
        bool front2 = d2 > EPSILON;

        bool back0 = d0 < -EPSILON;
        bool back1 = d1 < -EPSILON;
        bool back2 = d2 < -EPSILON;

        int fCount = (front0 ? 1 : 0) + (front1 ? 1 : 0) + (front2 ? 1 : 0);
        int bCount = (back0 ? 1 : 0) + (back1 ? 1 : 0) + (back2 ? 1 : 0);

        if (fCount == 3)
        {
            front.Add(tri);
        }
        else if (bCount == 3)
        {
            back.Add(tri);
        }
        else if (fCount == 0 && bCount == 0)
        {
            coplanar.Add(tri);
        }
        else
        {
            totalSplits++;
            SplitTriangle(tri, plane, front, back);
        }
    }

    private void SplitTriangle(Triangle tri, Plane plane, List<Triangle> front, List<Triangle> back)
    {
        // Get distances
        float d0 = plane.GetDistanceToPoint(tri.v0);
        float d1 = plane.GetDistanceToPoint(tri.v1);
        float d2 = plane.GetDistanceToPoint(tri.v2);

        // Classify points
        bool[] frontSide = { d0 > EPSILON, d1 > EPSILON, d2 > EPSILON };
        bool[] backSide = { d0 < -EPSILON, d1 < -EPSILON, d2 < -EPSILON };

        // Count how many points are on each side
        int frontCount = (frontSide[0] ? 1 : 0) + (frontSide[1] ? 1 : 0) + (frontSide[2] ? 1 : 0);
        int backCount = (backSide[0] ? 1 : 0) + (backSide[1] ? 1 : 0) + (backSide[2] ? 1 : 0);

        // 2 points on one side, 1 on the other
        if (frontCount == 2 && backCount == 1)
        {
            int loneIndex = backSide[0] ? 0 : (backSide[1] ? 1 : 2);
            SplitTriangle2To1(tri, plane, loneIndex, true, front, back);
        }
        else if (backCount == 2 && frontCount == 1)
        {
            int loneIndex = frontSide[0] ? 0 : (frontSide[1] ? 1 : 2);
            SplitTriangle2To1(tri, plane, loneIndex, false, front, back);
        }
        else
        {
            // Complex case - add to both sides (should be rare)
            front.Add(tri);
            back.Add(tri);
        }
    }

    private void SplitTriangle2To1(Triangle tri, Plane plane, int loneIndex, bool loneIsBack,
                               List<Triangle> front, List<Triangle> back)
    {
        Vector3[] v = { tri.v0, tri.v1, tri.v2 };
        Vector3[] n = { tri.n0, tri.n1, tri.n2 };
        Vector2[] uv = { tri.uv0, tri.uv1, tri.uv2 };

        Vector3 loneVertex = v[loneIndex];
        Vector3 loneNormal = n[loneIndex];
        Vector2 loneUV = uv[loneIndex];

        Vector3 v1 = v[(loneIndex + 1) % 3];
        Vector3 v2 = v[(loneIndex + 2) % 3];
        Vector3 n1 = n[(loneIndex + 1) % 3];
        Vector3 n2 = n[(loneIndex + 2) % 3];
        Vector2 uv1 = uv[(loneIndex + 1) % 3];
        Vector2 uv2 = uv[(loneIndex + 2) % 3];

        Vector3 i1 = PlaneIntersection(plane, loneVertex, v1);
        float t1 = CalculateInterpolationFactor(plane, loneVertex, v1);
        Vector3 n_i1 = Vector3.Lerp(loneNormal, n1, t1).normalized;
        Vector2 uv_i1 = Vector2.Lerp(loneUV, uv1, t1);

        Vector3 i2 = PlaneIntersection(plane, loneVertex, v2);
        float t2 = CalculateInterpolationFactor(plane, loneVertex, v2);
        Vector3 n_i2 = Vector3.Lerp(loneNormal, n2, t2).normalized;
        Vector2 uv_i2 = Vector2.Lerp(loneUV, uv2, t2);

        // Desired normal: prefer triangle's plane normal, fallback to geometric normal
        Vector3 desired = tri.plane.normal;
        if (desired.sqrMagnitude < 1e-4f)
            desired = Vector3.Cross(tri.v1 - tri.v0, tri.v2 - tri.v0).normalized;
        if (desired.sqrMagnitude < 1e-4f)
            desired = Vector3.up;

        // Helper: decide and swap b/c if necessary, then add triangle
        void AddTriClockwise(List<Triangle> list,
                     Vector3 a, Vector3 b, Vector3 c,
                     Vector3 na, Vector3 nb, Vector3 nc,
                     Vector2 ua, Vector2 ub, Vector2 uc)
        {
            Vector3 cross = Vector3.Cross(b - a, c - a);
            if (cross.z > 0f) // <-- assumes you're working in PS1 screen space (z forward)
            {
                // swap b <-> c
                var tmpV = b; b = c; c = tmpV;
                var tmpN = nb; nb = nc; nc = tmpN;
                var tmpUv = ub; ub = uc; uc = tmpUv;
            }

            list.Add(new Triangle(a, b, c, na, nb, nc, ua, ub, uc, tri.sourceExporter, tri.materialIndex));
        }

        if (loneIsBack)
        {
            // back: (lone, i1, i2)
            AddTriClockwise(back, loneVertex, i1, i2, loneNormal, n_i1, n_i2, loneUV, uv_i1, uv_i2);

            // front: (v1, i1, i2) and (v1, i2, v2)
            AddTriClockwise(front, v1, i1, i2, n1, n_i1, n_i2, uv1, uv_i1, uv_i2);
            AddTriClockwise(front, v1, i2, v2, n1, n_i2, n2, uv1, uv_i2, uv2);
        }
        else
        {
            // front: (lone, i1, i2)
            AddTriClockwise(front, loneVertex, i1, i2, loneNormal, n_i1, n_i2, loneUV, uv_i1, uv_i2);

            // back: (v1, i1, i2) and (v1, i2, v2)
            AddTriClockwise(back, v1, i1, i2, n1, n_i1, n_i2, uv1, uv_i1, uv_i2);
            AddTriClockwise(back, v1, i2, v2, n1, n_i2, n2, uv1, uv_i2, uv2);
        }
    }


    private Vector3 PlaneIntersection(Plane plane, Vector3 a, Vector3 b)
    {
        Vector3 ba = b - a;
        float denominator = Vector3.Dot(plane.normal, ba);

        // Check for parallel line (shouldn't happen in our case)
        if (Mathf.Abs(denominator) < 1e-4f)
            return a;

        float t = (-plane.distance - Vector3.Dot(plane.normal, a)) / denominator;
        return a + ba * Mathf.Clamp01(t);
    }

    private float CalculateInterpolationFactor(Plane plane, Vector3 a, Vector3 b)
    {
        Vector3 ba = b - a;
        float denominator = Vector3.Dot(plane.normal, ba);

        if (Mathf.Abs(denominator) < 1e-4f)
            return 0.5f;

        float t = (-plane.distance - Vector3.Dot(plane.normal, a)) / denominator;
        return Mathf.Clamp01(t);
    }

    private Bounds CalculateBounds(List<Triangle> triangles)
    {
        if (triangles == null || triangles.Count == 0)
            return new Bounds();

        Bounds bounds = triangles[0].bounds;
        for (int i = 1; i < triangles.Count; i++)
        {
            bounds.Encapsulate(triangles[i].bounds);
        }

        return bounds;
    }

    // Add a method to create modified meshes after BSP construction
    // Add a method to create modified meshes after BSP construction
    private void CreateModifiedMeshes()
    {
        if (root == null) return;

        // Collect all triangles from the BSP tree
        List<Triangle> allTriangles = new List<Triangle>();
        CollectTrianglesFromNode(root, allTriangles);

        // Group triangles by their source exporter and material index
        Dictionary<PSXObjectExporter, Dictionary<int, List<Triangle>>> exporterTriangles =
            new Dictionary<PSXObjectExporter, Dictionary<int, List<Triangle>>>();

        foreach (var tri in allTriangles)
        {
            if (!exporterTriangles.ContainsKey(tri.sourceExporter))
            {
                exporterTriangles[tri.sourceExporter] = new Dictionary<int, List<Triangle>>();
            }

            var materialDict = exporterTriangles[tri.sourceExporter];
            if (!materialDict.ContainsKey(tri.materialIndex))
            {
                materialDict[tri.materialIndex] = new List<Triangle>();
            }

            materialDict[tri.materialIndex].Add(tri);
        }

        // Create modified meshes for each exporter
        foreach (var kvp in exporterTriangles)
        {
            PSXObjectExporter exporter = kvp.Key;
            Dictionary<int, List<Triangle>> materialTriangles = kvp.Value;

            Mesh originalMesh = exporter.GetComponent<MeshFilter>().sharedMesh;
            Renderer renderer = exporter.GetComponent<Renderer>();

            Mesh modifiedMesh = new Mesh();
            modifiedMesh.name = originalMesh.name + "_BSP";

            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector4> tangents = new List<Vector4>();
            List<Color> colors = new List<Color>();

            // Create a list for each material's triangles
            List<List<int>> materialIndices = new List<List<int>>();
            for (int i = 0; i < renderer.sharedMaterials.Length; i++)
            {
                materialIndices.Add(new List<int>());
            }

            // Get the inverse transform to convert from world space back to object space
            Matrix4x4 worldToLocal = exporter.transform.worldToLocalMatrix;

            // Process each material
            foreach (var materialKvp in materialTriangles)
            {
                int materialIndex = materialKvp.Key;
                List<Triangle> triangles = materialKvp.Value;

                // Add vertices, normals, and uvs for this material
                for (int i = 0; i < triangles.Count; i++)
                {
                    Triangle tri = triangles[i];

                    // Transform vertices from world space back to object space
                    Vector3 v0 = worldToLocal.MultiplyPoint3x4(tri.v0);
                    Vector3 v1 = worldToLocal.MultiplyPoint3x4(tri.v1);
                    Vector3 v2 = worldToLocal.MultiplyPoint3x4(tri.v2);

                    int vertexIndex = vertices.Count;
                    vertices.Add(v0);
                    vertices.Add(v1);
                    vertices.Add(v2);

                    // Transform normals from world space back to object space
                    Vector3 n0 = worldToLocal.MultiplyVector(tri.n0).normalized;
                    Vector3 n1 = worldToLocal.MultiplyVector(tri.n1).normalized;
                    Vector3 n2 = worldToLocal.MultiplyVector(tri.n2).normalized;

                    normals.Add(n0);
                    normals.Add(n1);
                    normals.Add(n2);

                    uvs.Add(tri.uv0);
                    uvs.Add(tri.uv1);
                    uvs.Add(tri.uv2);

                    // Add default tangents and colors (will be recalculated later)
                    tangents.Add(new Vector4(1, 0, 0, 1));
                    tangents.Add(new Vector4(1, 0, 0, 1));
                    tangents.Add(new Vector4(1, 0, 0, 1));

                    colors.Add(Color.white);
                    colors.Add(Color.white);
                    colors.Add(Color.white);

                    // Add indices for this material
                    materialIndices[materialIndex].Add(vertexIndex);
                    materialIndices[materialIndex].Add(vertexIndex + 1);
                    materialIndices[materialIndex].Add(vertexIndex + 2);
                }
            }

            // Assign data to the mesh
            modifiedMesh.vertices = vertices.ToArray();
            modifiedMesh.normals = normals.ToArray();
            modifiedMesh.uv = uvs.ToArray();
            modifiedMesh.tangents = tangents.ToArray();
            modifiedMesh.colors = colors.ToArray();

            // Set up submeshes based on materials
            modifiedMesh.subMeshCount = materialIndices.Count;
            for (int i = 0; i < materialIndices.Count; i++)
            {
                modifiedMesh.SetTriangles(materialIndices[i].ToArray(), i);
            }

            // Recalculate important mesh properties
            modifiedMesh.RecalculateBounds();
            modifiedMesh.RecalculateTangents();

            // Assign the modified mesh to the exporter
            exporter.ModifiedMesh = modifiedMesh;
        }
    }
    // Helper method to collect all triangles from the BSP tree
    private void CollectTrianglesFromNode(Node node, List<Triangle> triangles)
    {
        if (node == null) return;

        if (node.isLeaf)
        {
            triangles.AddRange(node.triangles);
        }
        else
        {
            CollectTrianglesFromNode(node.front, triangles);
            CollectTrianglesFromNode(node.back, triangles);
        }
    }

    public void DrawGizmos(int maxDepth)
    {
        if (root == null) return;

        DrawNodeGizmos(root, 0, maxDepth);
    }

    private void DrawNodeGizmos(Node node, int depth, int maxDepth)
    {
        if (node == null) return;
        if (depth > maxDepth) return;

        Color nodeColor = Color.HSVToRGB((depth * 0.1f) % 1f, 0.8f, 0.8f);
        Gizmos.color = nodeColor;

        if (node.isLeaf)
        {
            foreach (var tri in node.triangles)
            {
                DrawTriangleGizmo(tri);
            }
        }
        else
        {
            DrawPlaneGizmo(node.plane, node.bounds);

            // Draw the triangle that was used for the split plane if available
            if (splitPlaneTriangles.ContainsKey(node))
            {
                Gizmos.color = Color.magenta;
                DrawTriangleGizmo(splitPlaneTriangles[node]);
                Gizmos.color = nodeColor;
            }

            DrawNodeGizmos(node.front, depth + 1, maxDepth);
            DrawNodeGizmos(node.back, depth + 1, maxDepth);
        }
    }

    private void DrawTriangleGizmo(Triangle tri)
    {
        Gizmos.DrawLine(tri.v0, tri.v1);
        Gizmos.DrawLine(tri.v1, tri.v2);
        Gizmos.DrawLine(tri.v2, tri.v0);
    }

    private void DrawPlaneGizmo(Plane plane, Bounds bounds)
    {
        Vector3 center = bounds.center;
        Vector3 normal = plane.normal;

        Vector3 tangent = Vector3.Cross(normal, Vector3.up);
        if (tangent.magnitude < 0.1f) tangent = Vector3.Cross(normal, Vector3.right);
        tangent = tangent.normalized;

        Vector3 bitangent = Vector3.Cross(normal, tangent).normalized;

        float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) * 0.5f;
        tangent *= size;
        bitangent *= size;

        Vector3 p0 = center - tangent - bitangent;
        Vector3 p1 = center + tangent - bitangent;
        Vector3 p2 = center + tangent + bitangent;
        Vector3 p3 = center - tangent + bitangent;

        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p0);

        Gizmos.color = Color.red;
        Gizmos.DrawLine(center, center + normal * size * 0.5f);
    }
}