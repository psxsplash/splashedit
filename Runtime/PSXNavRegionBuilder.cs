using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace SplashEdit.RuntimeCode
{
    public enum NavSurfaceType : byte { Flat = 0, Ramp = 1, Stairs = 2 }

    /// <summary>
    /// PS1 nav mesh builder using DotRecast (C# port of Recast).
    /// Runs the full Recast voxelization pipeline on scene geometry,
    /// then extracts convex polygons with accurate heights from the detail mesh.
    /// </summary>
    public class PSXNavRegionBuilder
    {
        // ────────────────────────────────────────────────────────────────
        // Agent parameters
        // ────────────────────────────────────────────────────────────────
        public float AgentHeight = 1.8f;
        public float AgentRadius = 0.3f;
        public float MaxStepHeight = 0.35f;
        public float WalkableSlopeAngle = 46.0f;

        // ────────────────────────────────────────────────────────────────
        // Voxelization parameters
        // ────────────────────────────────────────────────────────────────
        public float CellSize = 0.05f;
        public float CellHeight = 0.025f;

        // ────────────────────────────────────────────────────────────────
        // Region parameters (previously hardcoded)
        // ────────────────────────────────────────────────────────────────
        public int MinRegionArea = 8;
        public int MergeRegionArea = 20;
        public float MaxSimplifyError = 1.3f;
        public float MaxEdgeLength = 12.0f;
        public NavPartitionMethod PartitionMethod = NavPartitionMethod.Watershed;

        // ────────────────────────────────────────────────────────────────
        // Detail mesh parameters (decoupled from CellHeight)
        // ────────────────────────────────────────────────────────────────
        public float DetailSampleDist = 6.0f;   // Multiplier of CellSize
        public float DetailMaxError = 0.025f;    // World units, independent of CellHeight

        // ────────────────────────────────────────────────────────────────
        // Plane fit validation
        // ────────────────────────────────────────────────────────────────
        public float MaxPlaneError = 0.15f;

        public const int MaxVertsPerRegion = 8;

        private List<NavRegionExport> _regions = new();
        private List<NavPortalExport> _portals = new();
        private int _startRegion;

        public int RegionCount => _regions.Count;
        public int PortalCount => _portals.Count;
        public IReadOnlyList<NavRegionExport> Regions => _regions;
        public IReadOnlyList<NavPortalExport> Portals => _portals;
        public VoxelCell[] DebugCells => null;
        public int DebugGridW => 0;
        public int DebugGridH => 0;
        public float DebugOriginX => 0;
        public float DebugOriginZ => 0;
        public float DebugVoxelSize => 0;

        public class NavRegionExport
        {
            public List<Vector2> vertsXZ = new();
            public float planeA, planeB, planeD;
            public int portalStart, portalCount;
            public NavSurfaceType surfaceType;
            public byte roomIndex;
            public byte flags;             // bit 0 = isPlatform
            public byte walkoffEdgeMask;   // bit i = edge i allows walkoff
            public byte boundaryEdgeMask;  // bit i = edge i has no portal neighbor
            public Plane floorPlane;
            public List<Vector3> worldTris = new();
            public List<int> sourceTriIndices = new();
            /// <summary>Maximum deviation of any sample point from the fitted plane (world units).</summary>
            public float maxPlaneDeviation;
        }

        public struct NavPortalExport
        {
            public Vector2 a, b;
            public int neighborRegion;
            public float heightDelta;
        }

        public struct VoxelCell
        {
            public float worldY, slopeAngle;
            public bool occupied, blocked;
            public int regionId;
        }

        /// <summary>PSXRoom volumes for spatial room assignment. Set before Build().</summary>
        public PSXRoom[] PSXRooms { get; set; }

        /// <summary>Exporters tagged as platforms. Regions within their bounds get the platform flag.</summary>
        public PSXObjectExporter[] PlatformExporters { get; set; }

        /// <summary>Walkoff zones. Boundary edges within these volumes allow the player to walk off.</summary>
        public PSXNavWalkoffZone[] WalkoffZones { get; set; }

        public void Build(PSXObjectExporter[] exporters, Vector3 spawn)
        {
            _regions.Clear(); _portals.Clear(); _startRegion = 0;

            // 1. Collect world-space geometry from all exporters
            var allVerts = new List<float>();
            var allTris = new List<int>();
            CollectGeometry(exporters, allVerts, allTris);

            if (allVerts.Count < 9 || allTris.Count < 3)
            {
                Debug.LogWarning("[Nav] No geometry to build navmesh from.");
                return;
            }

            float[] verts = allVerts.ToArray();
            int[] tris = allTris.ToArray();
            int nverts = allVerts.Count / 3;
            int ntris = allTris.Count / 3;

            // 2. Recast parameters (convert to voxel units)
            float cs = CellSize;
            float ch = CellHeight;
            int walkableHeight = (int)Math.Ceiling(AgentHeight / ch);
            int walkableClimb = (int)Math.Floor(MaxStepHeight / ch);
            int walkableRadius = (int)Math.Ceiling(AgentRadius / cs);
            int maxEdgeLen = (int)(MaxEdgeLength / cs);
            float maxSimplificationError = MaxSimplifyError;
            int minRegionArea = MinRegionArea;
            int mergeRegionArea = MergeRegionArea;
            int maxVertsPerPoly = MaxVertsPerRegion;      // Match PS1 runtime struct (8)
            float detailSampleDist = cs * DetailSampleDist;
            float detailSampleMaxError = DetailMaxError;  // Decoupled from CellHeight

            // 3. Compute bounds with border padding
            float bminX = float.MaxValue, bminY = float.MaxValue, bminZ = float.MaxValue;
            float bmaxX = float.MinValue, bmaxY = float.MinValue, bmaxZ = float.MinValue;
            for (int i = 0; i < verts.Length; i += 3)
            {
                bminX = Math.Min(bminX, verts[i]);   bmaxX = Math.Max(bmaxX, verts[i]);
                bminY = Math.Min(bminY, verts[i+1]); bmaxY = Math.Max(bmaxY, verts[i+1]);
                bminZ = Math.Min(bminZ, verts[i+2]); bmaxZ = Math.Max(bmaxZ, verts[i+2]);
            }

            float borderPad = walkableRadius * cs;
            bminX -= borderPad; bminZ -= borderPad;
            bmaxX += borderPad; bmaxZ += borderPad;

            var bmin = new RcVec3f(bminX, bminY, bminZ);
            var bmax = new RcVec3f(bmaxX, bmaxY, bmaxZ);

            int gw = (int)((bmaxX - bminX) / cs + 0.5f);
            int gh = (int)((bmaxZ - bminZ) / cs + 0.5f);

            // 4. Run Recast pipeline
            var ctx = new RcContext();

            // Create heightfield
            var solid = new RcHeightfield(gw, gh, bmin, bmax, cs, ch, 0);

            // Mark walkable triangles
            int[] areas = RcRecast.MarkWalkableTriangles(ctx, WalkableSlopeAngle, verts, tris, ntris,
                new RcAreaModification(RcRecast.RC_WALKABLE_AREA));

            // Rasterize
            RcRasterizations.RasterizeTriangles(ctx, verts, tris, areas, ntris, solid, walkableClimb);

            // Filter
            RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb, solid);
            RcFilters.FilterLedgeSpans(ctx, walkableHeight, walkableClimb, solid);
            RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, solid);

            // Build compact heightfield
            var chf = RcCompacts.BuildCompactHeightfield(ctx, walkableHeight, walkableClimb, solid);

            // Erode walkable area
            RcAreas.ErodeWalkableArea(ctx, walkableRadius, chf);

            // Build distance field and regions using selected partition method
            switch (PartitionMethod)
            {
                case NavPartitionMethod.Monotone:
                    RcRegions.BuildRegionsMonotone(ctx, chf, minRegionArea, mergeRegionArea);
                    break;
                case NavPartitionMethod.Layer:
                    RcRegions.BuildLayerRegions(ctx, chf, minRegionArea);
                    break;
                case NavPartitionMethod.Watershed:
                default:
                    RcRegions.BuildDistanceField(ctx, chf);
                    RcRegions.BuildRegions(ctx, chf, minRegionArea, mergeRegionArea);
                    break;
            }

            // Build contours
            var cset = RcContours.BuildContours(ctx, chf, maxSimplificationError, maxEdgeLen,
                (int)RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);

            // Build polygon mesh
            var pmesh = RcMeshs.BuildPolyMesh(ctx, cset, maxVertsPerPoly);

            // Build detail mesh for accurate heights
            var dmesh = RcMeshDetails.BuildPolyMeshDetail(ctx, pmesh, chf, detailSampleDist, detailSampleMaxError);

            // 5. Extract polygons as NavRegions
            int nvp = pmesh.nvp;
            int RC_MESH_NULL_IDX = 0xffff;

            for (int i = 0; i < pmesh.npolys; i++)
            {
                // Count valid vertices in this polygon
                int nv = 0;
                for (int j = 0; j < nvp; j++)
                {
                    if (pmesh.polys[i * 2 * nvp + j] == RC_MESH_NULL_IDX) break;
                    nv++;
                }
                if (nv < 3) continue;

                var region = new NavRegionExport();
                var pts3d = new List<Vector3>();

                // Track which edges are boundary (no portal neighbor) before winding reversal
                bool[] isBoundary = new bool[nv];
                for (int j = 0; j < nv; j++)
                {
                    int neighbor = pmesh.polys[i * 2 * nvp + nvp + j];
                    isBoundary[j] = (neighbor == RC_MESH_NULL_IDX || (neighbor & 0x8000) != 0);
                }

                for (int j = 0; j < nv; j++)
                {
                    int vi = pmesh.polys[i * 2 * nvp + j];

                    // Get XZ from poly mesh (cell coords -> world)
                    float wx = pmesh.bmin.X + pmesh.verts[vi * 3 + 0] * pmesh.cs;
                    float wz = pmesh.bmin.Z + pmesh.verts[vi * 3 + 2] * pmesh.cs;

                    // Get accurate Y from detail mesh
                    float wy;
                    if (dmesh != null && i < dmesh.nmeshes)
                    {
                        int vbase = dmesh.meshes[i * 4 + 0];
                        // Detail mesh stores polygon verts first, in order
                        wy = dmesh.verts[(vbase + j) * 3 + 1];
                    }
                    else
                    {
                        // Fallback: coarse Y from poly mesh
                        wy = pmesh.bmin.Y + pmesh.verts[vi * 3 + 1] * pmesh.ch;
                    }

                    region.vertsXZ.Add(new Vector2(wx, wz));
                    pts3d.Add(new Vector3(wx, wy, wz));
                }

                // Ensure CCW winding
                float signedArea = 0;
                for (int j = 0; j < region.vertsXZ.Count; j++)
                {
                    var a = region.vertsXZ[j];
                    var b = region.vertsXZ[(j + 1) % region.vertsXZ.Count];
                    signedArea += a.x * b.y - b.x * a.y;
                }
                if (signedArea < 0)
                {
                    region.vertsXZ.Reverse();
                    pts3d.Reverse();
                    // Remap boundary edge flags for reversed vertex order
                    // Original edge j (v_j to v_{j+1}) maps to reversed edge (nv-2-j+nv)%nv
                    bool[] reversed = new bool[nv];
                    for (int j = 0; j < nv; j++)
                        reversed[(nv - 2 - j + nv) % nv] = isBoundary[j];
                    isBoundary = reversed;
                }

                // Build boundary edge bitmask
                byte boundaryMask = 0;
                for (int j = 0; j < nv; j++)
                {
                    if (isBoundary[j])
                        boundaryMask |= (byte)(1 << j);
                }
                region.boundaryEdgeMask = boundaryMask;

                // Include ALL detail mesh vertices (including interior samples) for
                // a much better least-squares plane fit on terrain. The detail mesh
                // stores sub-triangulated height data that captures valleys and ridges
                // that polygon corners alone would miss.
                if (dmesh != null && i < dmesh.nmeshes)
                {
                    int vbase = dmesh.meshes[i * 4 + 0];
                    int vcount = dmesh.meshes[i * 4 + 1];
                    // Detail mesh stores polygon boundary verts first (already added above as pts3d),
                    // followed by interior verts. Only add interior verts to avoid duplicates.
                    for (int dv = nv; dv < vcount; dv++)
                    {
                        float dx = dmesh.verts[(vbase + dv) * 3 + 0];
                        float dy = dmesh.verts[(vbase + dv) * 3 + 1];
                        float dz = dmesh.verts[(vbase + dv) * 3 + 2];
                        pts3d.Add(new Vector3(dx, dy, dz));
                    }
                }

                FitPlane(region, pts3d);
                _regions.Add(region);
            }

            // 6. Build portals from Recast neighbor connectivity
            var perRegion = new Dictionary<int, List<NavPortalExport>>();
            for (int i = 0; i < _regions.Count; i++)
                perRegion[i] = new List<NavPortalExport>();

            // Build mapping: pmesh poly index -> region index
            // (some polys may be skipped if nv < 3, so we need this mapping)
            var polyToRegion = new Dictionary<int, int>();
            int regionIdx = 0;
            for (int i = 0; i < pmesh.npolys; i++)
            {
                int nv = 0;
                for (int j = 0; j < nvp; j++)
                {
                    if (pmesh.polys[i * 2 * nvp + j] == RC_MESH_NULL_IDX) break;
                    nv++;
                }
                if (nv < 3) continue;
                polyToRegion[i] = regionIdx++;
            }

            for (int i = 0; i < pmesh.npolys; i++)
            {
                if (!polyToRegion.TryGetValue(i, out int srcRegion)) continue;

                int nv = 0;
                for (int j = 0; j < nvp; j++)
                {
                    if (pmesh.polys[i * 2 * nvp + j] == RC_MESH_NULL_IDX) break;
                    nv++;
                }

                for (int j = 0; j < nv; j++)
                {
                    int neighbor = pmesh.polys[i * 2 * nvp + nvp + j];
                    if (neighbor == RC_MESH_NULL_IDX || (neighbor & 0x8000) != 0) continue;
                    if (!polyToRegion.TryGetValue(neighbor, out int dstRegion)) continue;

                    // Portal edge vertices from pmesh directly (NOT from region,
                    // which may have been reversed for CCW winding)
                    int vi0 = pmesh.polys[i * 2 * nvp + j];
                    int vi1 = pmesh.polys[i * 2 * nvp + (j + 1) % nv];

                    float ax = pmesh.bmin.X + pmesh.verts[vi0 * 3 + 0] * pmesh.cs;
                    float az = pmesh.bmin.Z + pmesh.verts[vi0 * 3 + 2] * pmesh.cs;
                    float bx = pmesh.bmin.X + pmesh.verts[vi1 * 3 + 0] * pmesh.cs;
                    float bz = pmesh.bmin.Z + pmesh.verts[vi1 * 3 + 2] * pmesh.cs;

                    var a2 = new Vector2(ax, az);
                    var b2 = new Vector2(bx, bz);

                    // Height delta at midpoint of portal edge
                    var mid = new Vector2((ax + bx) / 2, (az + bz) / 2);
                    float yHere = EvalY(_regions[srcRegion], mid);
                    float yThere = EvalY(_regions[dstRegion], mid);

                    perRegion[srcRegion].Add(new NavPortalExport
                    {
                        a = a2,
                        b = b2,
                        neighborRegion = dstRegion,
                        heightDelta = yThere - yHere
                    });
                }
            }

            // Assign portals
            foreach (var kvp in perRegion)
            {
                _regions[kvp.Key].portalStart = _portals.Count;
                _regions[kvp.Key].portalCount = kvp.Value.Count;
                _portals.AddRange(kvp.Value);
            }

            // 7. Assign rooms: spatial containment if PSXRooms provided, BFS fallback
            if (PSXRooms != null && PSXRooms.Length > 0)
                AssignRoomsFromPSXRooms(PSXRooms);
            else
                AssignRoomsByBFS();

            // 8. Apply platform flags and walkoff edge zones
            ApplyPlatformFlags(exporters);
            ApplyWalkoffZones();

            // 9. Find start region closest to spawn
            _startRegion = FindClosestRegion(spawn);
        }

        void CollectGeometry(PSXObjectExporter[] exporters, List<float> outVerts, List<int> outTris)
        {
            foreach (var exporter in exporters)
            {
                if (exporter.CollisionType == PSXCollisionType.Dynamic || exporter.CollisionType == PSXCollisionType.None)
                    continue;

                MeshFilter mf = exporter.GetComponent<MeshFilter>();
                Mesh mesh = mf?.sharedMesh;
                if (mesh == null) continue;

                Matrix4x4 worldMatrix = exporter.transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices;
                int[] indices = mesh.triangles;

                int baseVert = outVerts.Count / 3;
                foreach (var v in vertices)
                {
                    Vector3 w = worldMatrix.MultiplyPoint3x4(v);
                    outVerts.Add(w.x);
                    outVerts.Add(w.y);
                    outVerts.Add(w.z);
                }

                // Filter triangles: reject downward-facing normals  
                // (ceilings, roofs, undersides) which should never be walkable.
                for (int i = 0; i < indices.Length; i += 3)
                {
                    Vector3 v0 = worldMatrix.MultiplyPoint3x4(vertices[indices[i]]);
                    Vector3 v1 = worldMatrix.MultiplyPoint3x4(vertices[indices[i + 1]]);
                    Vector3 v2 = worldMatrix.MultiplyPoint3x4(vertices[indices[i + 2]]);

                    Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
                    // Skip triangles whose world-space normal points downward (y < 0)
                    // This eliminates ceilings/roofs that Recast might incorrectly voxelize
                    if (normal.y < 0f) continue;

                    outTris.Add(indices[i] + baseVert);
                    outTris.Add(indices[i + 1] + baseVert);
                    outTris.Add(indices[i + 2] + baseVert);
                }
            }
        }

        int FindClosestRegion(Vector3 spawn)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _regions.Count; i++)
            {
                var r = _regions[i];
                // Compute centroid
                float cx = 0, cz = 0;
                foreach (var v in r.vertsXZ) { cx += v.x; cz += v.y; }
                cx /= r.vertsXZ.Count; cz /= r.vertsXZ.Count;
                float cy = EvalY(r, new Vector2(cx, cz));

                float dx = spawn.x - cx, dy = spawn.y - cy, dz = spawn.z - cz;
                float dist = dx * dx + dy * dy + dz * dz;
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        float EvalY(NavRegionExport r, Vector2 xz) => r.planeA * xz.x + r.planeB * xz.y + r.planeD;

        void FitPlane(NavRegionExport r, List<Vector3> pts)
        {
            int n = pts.Count;
            if (n < 3) { r.planeA = 0; r.planeB = 0; r.planeD = n > 0 ? pts[0].y : 0; r.surfaceType = NavSurfaceType.Flat; r.maxPlaneDeviation = 0; return; }

            if (n == 3)
            {
                // Exact 3-point solve: Y = A*X + B*Z + D
                double x0 = pts[0].x, z0 = pts[0].z, y0 = pts[0].y;
                double x1 = pts[1].x, z1 = pts[1].z, y1 = pts[1].y;
                double x2 = pts[2].x, z2 = pts[2].z, y2 = pts[2].y;
                double det = (x0 - x2) * (z1 - z2) - (x1 - x2) * (z0 - z2);
                if (Math.Abs(det) < 1e-12) { r.planeA = 0; r.planeB = 0; r.planeD = (float)((y0 + y1 + y2) / 3); }
                else
                {
                    double inv = 1.0 / det;
                    r.planeA = (float)(((y0 - y2) * (z1 - z2) - (y1 - y2) * (z0 - z2)) * inv);
                    r.planeB = (float)(((x0 - x2) * (y1 - y2) - (x1 - x2) * (y0 - y2)) * inv);
                    r.planeD = (float)(y0 - r.planeA * x0 - r.planeB * z0);
                }
            }
            else
            {
                // Least-squares: Y = A*X + B*Z + D
                double sX = 0, sZ = 0, sY = 0, sXX = 0, sXZ = 0, sZZ = 0, sXY = 0, sZY = 0;
                foreach (var p in pts) { sX += p.x; sZ += p.z; sY += p.y; sXX += p.x * p.x; sXZ += p.x * p.z; sZZ += p.z * p.z; sXY += p.x * p.y; sZY += p.z * p.y; }
                double det = sXX * (sZZ * n - sZ * sZ) - sXZ * (sXZ * n - sZ * sX) + sX * (sXZ * sZ - sZZ * sX);
                if (Math.Abs(det) < 1e-12) { r.planeA = 0; r.planeB = 0; r.planeD = (float)(sY / n); }
                else
                {
                    double inv = 1.0 / det;
                    r.planeA = (float)((sXY * (sZZ * n - sZ * sZ) - sXZ * (sZY * n - sZ * sY) + sX * (sZY * sZ - sZZ * sY)) * inv);
                    r.planeB = (float)((sXX * (sZY * n - sZ * sY) - sXY * (sXZ * n - sZ * sX) + sX * (sXZ * sY - sZY * sX)) * inv);
                    r.planeD = (float)((sXX * (sZZ * sY - sZ * sZY) - sXZ * (sXZ * sY - sZY * sX) + sXY * (sXZ * sZ - sZZ * sX)) * inv);
                }
            }

            // Compute max deviation of any sample point from the fitted plane
            float maxDev = 0;
            foreach (var p in pts)
            {
                float predicted = r.planeA * p.x + r.planeB * p.z + r.planeD;
                float dev = Mathf.Abs(p.y - predicted);
                if (dev > maxDev) maxDev = dev;
            }
            r.maxPlaneDeviation = maxDev;

            float slope = Mathf.Atan(Mathf.Sqrt(r.planeA * r.planeA + r.planeB * r.planeB)) * Mathf.Rad2Deg;
            r.surfaceType = slope < 3f ? NavSurfaceType.Flat : slope < 25f ? NavSurfaceType.Ramp : NavSurfaceType.Stairs;
        }

        /// <summary>
        /// Assign room indices to nav regions using PSXRoom spatial containment.
        /// Each region's centroid is tested against all PSXRoom volumes. The smallest
        /// containing room wins (most specific). Regions outside all rooms get 0xFF.
        /// This ensures nav region room indices match the PSXRoomBuilder room indices
        /// used by the rendering portal system.
        /// </summary>
        void AssignRoomsFromPSXRooms(PSXRoom[] psxRooms)
        {
            const float MARGIN = 0.5f;
            Bounds[] roomBounds = new Bounds[psxRooms.Length];
            for (int r = 0; r < psxRooms.Length; r++)
            {
                roomBounds[r] = psxRooms[r].GetWorldBounds();
                roomBounds[r].Expand(MARGIN * 2f);
            }

            for (int i = 0; i < _regions.Count; i++)
            {
                var reg = _regions[i];
                // Compute region centroid from polygon vertices
                float cx = 0, cz = 0;
                foreach (var v in reg.vertsXZ) { cx += v.x; cz += v.y; }
                cx /= reg.vertsXZ.Count; cz /= reg.vertsXZ.Count;
                float cy = EvalY(reg, new Vector2(cx, cz));
                Vector3 centroid = new Vector3(cx, cy, cz);

                byte bestRoom = 0xFF;
                float bestVolume = float.MaxValue;
                for (int r = 0; r < psxRooms.Length; r++)
                {
                    if (roomBounds[r].Contains(centroid))
                    {
                        float vol = roomBounds[r].size.x * roomBounds[r].size.y * roomBounds[r].size.z;
                        if (vol < bestVolume)
                        {
                            bestVolume = vol;
                            bestRoom = (byte)r;
                        }
                    }
                }
                reg.roomIndex = bestRoom;
            }
        }

        /// <summary>
        /// Fallback room assignment via BFS over nav portal connectivity.
        /// Used when no PSXRoom volumes exist (exterior scenes).
        /// </summary>
        void AssignRoomsByBFS()
        {
            byte room = 0;
            var vis = new bool[_regions.Count];
            for (int i = 0; i < _regions.Count; i++)
            {
                if (vis[i]) continue;
                byte rm = room++;
                var q = new Queue<int>(); q.Enqueue(i); vis[i] = true;
                while (q.Count > 0)
                {
                    int ri = q.Dequeue(); _regions[ri].roomIndex = rm;
                    for (int p = _regions[ri].portalStart; p < _regions[ri].portalStart + _regions[ri].portalCount; p++)
                    {
                        int nb = _portals[p].neighborRegion;
                        if (nb >= 0 && nb < _regions.Count && !vis[nb]) { vis[nb] = true; q.Enqueue(nb); }
                    }
                }
            }
        }

        /// <summary>
        /// Flag nav regions whose centroid lies within the world-space AABB of
        /// a platform PSXObjectExporter. All boundary edges of platform regions
        /// get the walkoff bit set.
        /// </summary>
        void ApplyPlatformFlags(PSXObjectExporter[] allExporters)
        {
            if (PlatformExporters == null || PlatformExporters.Length == 0) return;

            // Build world-space AABB for each platform exporter
            var platformBounds = new List<Bounds>();
            foreach (var exp in PlatformExporters)
            {
                MeshFilter mf = exp.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds local = mf.sharedMesh.bounds;
                Bounds world = TransformBounds(local, exp.transform);
                world.Expand(0.1f);
                platformBounds.Add(world);
            }

            for (int i = 0; i < _regions.Count; i++)
            {
                var reg = _regions[i];
                float cx = 0, cz = 0;
                foreach (var v in reg.vertsXZ) { cx += v.x; cz += v.y; }
                cx /= reg.vertsXZ.Count; cz /= reg.vertsXZ.Count;
                // The -0.01f below is to compensate for floating point errors
                float cy = EvalY(reg, new Vector2(cx, cz)) - 0.01f; 
                Vector3 centroid = new Vector3(cx, cy, cz);

                foreach (var bounds in platformBounds)
                {
                    if (bounds.Contains(centroid))
                    {
                        reg.flags |= 0x01; // isPlatform
                        reg.walkoffEdgeMask |= reg.boundaryEdgeMask;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// For each nav region boundary edge, check if the edge midpoint lies
        /// inside any PSXNavWalkoffZone volume. If so, set the walkoff bit for
        /// that edge so the player can leave through it.
        /// </summary>
        void ApplyWalkoffZones()
        {
            if (WalkoffZones == null || WalkoffZones.Length == 0) return;

            var zoneBounds = new Bounds[WalkoffZones.Length];
            for (int z = 0; z < WalkoffZones.Length; z++)
                zoneBounds[z] = WalkoffZones[z].GetWorldBounds();

            for (int i = 0; i < _regions.Count; i++)
            {
                var reg = _regions[i];
                int nv = reg.vertsXZ.Count;
                for (int j = 0; j < nv; j++)
                {
                    // Only boundary edges can be walkoff
                    if ((reg.boundaryEdgeMask & (1 << j)) == 0) continue;

                    int next = (j + 1) % nv;
                    Vector2 mid2D = (reg.vertsXZ[j] + reg.vertsXZ[next]) * 0.5f;
                    float midY = EvalY(reg, mid2D);
                    Vector3 mid3D = new Vector3(mid2D.x, midY, mid2D.y);

                    foreach (var zb in zoneBounds)
                    {
                        if (zb.Contains(mid3D))
                        {
                            reg.walkoffEdgeMask |= (byte)(1 << j);
                            break;
                        }
                    }
                }
            }
        }

        static Bounds TransformBounds(Bounds local, Transform t)
        {
            Vector3 ext = local.extents;
            Vector3 center = local.center;
            Vector3 wMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 wMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + new Vector3(
                    (i & 1) != 0 ? ext.x : -ext.x,
                    (i & 2) != 0 ? ext.y : -ext.y,
                    (i & 4) != 0 ? ext.z : -ext.z
                );
                Vector3 w = t.TransformPoint(corner);
                wMin = Vector3.Min(wMin, w);
                wMax = Vector3.Max(wMax, w);
            }

            Bounds b = new Bounds();
            b.SetMinMax(wMin, wMax);
            return b;
        }

        public void WriteToBinary(BinaryWriter writer, float gteScaling)
        {
            writer.Write((ushort)_regions.Count);
            writer.Write((ushort)_portals.Count);
            writer.Write((ushort)_startRegion);
            writer.Write((ushort)0);
            foreach (var r in _regions)
            {
                for (int v = 0; v < MaxVertsPerRegion; v++)
                    writer.Write(v < r.vertsXZ.Count ? PSXTrig.ConvertWorldToFixed12(r.vertsXZ[v].x / gteScaling) : 0);
                for (int v = 0; v < MaxVertsPerRegion; v++)
                    writer.Write(v < r.vertsXZ.Count ? PSXTrig.ConvertWorldToFixed12(r.vertsXZ[v].y / gteScaling) : 0);
                writer.Write(PSXTrig.ConvertWorldToFixed12(-r.planeA));
                writer.Write(PSXTrig.ConvertWorldToFixed12(-r.planeB));
                writer.Write(PSXTrig.ConvertWorldToFixed12(-r.planeD / gteScaling));
                writer.Write((ushort)r.portalStart);
                writer.Write((byte)r.portalCount);
                writer.Write((byte)r.vertsXZ.Count);
                writer.Write((byte)r.surfaceType);
                writer.Write(r.roomIndex);
                writer.Write(r.flags);
                writer.Write(r.walkoffEdgeMask);
            }
            foreach (var p in _portals)
            {
                writer.Write(PSXTrig.ConvertWorldToFixed12(p.a.x / gteScaling));
                writer.Write(PSXTrig.ConvertWorldToFixed12(p.a.y / gteScaling));
                writer.Write(PSXTrig.ConvertWorldToFixed12(p.b.x / gteScaling));
                writer.Write(PSXTrig.ConvertWorldToFixed12(p.b.y / gteScaling));
                writer.Write((ushort)p.neighborRegion);
                writer.Write((short)PSXTrig.ConvertToFixed12(p.heightDelta / gteScaling));
            }
        }

        public int GetBinarySize() => 8 + _regions.Count * 84 + _portals.Count * 20;
    }
}
