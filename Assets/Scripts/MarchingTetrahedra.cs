using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Table-free isosurface extraction: each grid cell is split into six tetrahedra along its main
/// diagonal and every tetrahedron is polygonised by counting corners above the iso-level, so there
/// is no 256-case lookup table to get wrong. Triangles are oriented from the field itself (front
/// face toward decreasing values), after transformation into the caller's space, so a reflecting
/// transform such as the ROS-to-Unity axis swap needs no special handling. Vertices on shared
/// edges are welded so <c>Mesh.RecalculateNormals</c> gives smooth shading.
/// </summary>
public static class MarchingTetrahedra
{
    // Cube corner c = x | y << 1 | z << 2. Six tetrahedra, each a monotone path 0 -> 7 along edges.
    private static readonly int[,] Tets =
    {
        { 0, 1, 3, 7 }, { 0, 1, 5, 7 }, { 0, 2, 3, 7 },
        { 0, 2, 6, 7 }, { 0, 4, 5, 7 }, { 0, 4, 6, 7 },
    };

    /// <summary>Edge vertices closer than this fraction of an edge to a grid point snap to that point.</summary>
    private const float WeldFraction = 1e-4f;

    private static readonly int[] CornerDx = { 0, 1, 0, 1, 0, 1, 0, 1 };
    private static readonly int[] CornerDy = { 0, 0, 1, 1, 0, 0, 1, 1 };
    private static readonly int[] CornerDz = { 0, 0, 0, 0, 1, 1, 1, 1 };

    /// <summary>
    /// Polygonises <paramref name="field"/> (dimensions nx*ny*nz, index (z*ny + y)*nx + x) at
    /// <paramref name="iso"/>. Vertices are emitted in the space defined by
    /// <paramref name="gridToLocal"/> applied to grid-index coordinates. The output lists are
    /// cleared first; <paramref name="edgeCache"/> is cleared and reused for welding.
    /// Front faces point toward lower field values (outward for a "reachable = high" field).
    /// </summary>
    public static void Polygonize(float[] field, int nx, int ny, int nz, float iso, Matrix4x4 gridToLocal,
                                  List<Vector3> verts, List<int> tris, Dictionary<long, int> edgeCache)
    {
        verts.Clear();
        tris.Clear();
        edgeCache.Clear();

        var cornerVal = new float[8];
        var cornerId = new int[8];
        var cornerPos = new Vector3[8];
        var tetVal = new float[4];
        var tetId = new int[4];
        var tetPos = new Vector3[4];
        var inside = new int[4];
        var outside = new int[4];
        var quad = new int[4];

        for (int z = 0; z < nz - 1; z++)
        {
            for (int y = 0; y < ny - 1; y++)
            {
                for (int x = 0; x < nx - 1; x++)
                {
                    float min = float.MaxValue, max = float.MinValue;
                    for (int c = 0; c < 8; c++)
                    {
                        int cx = x + CornerDx[c], cy = y + CornerDy[c], cz = z + CornerDz[c];
                        int id = (cz * ny + cy) * nx + cx;
                        float v = field[id];
                        cornerVal[c] = v;
                        cornerId[c] = id;
                        cornerPos[c] = new Vector3(cx, cy, cz);
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    if (max <= iso || min > iso)
                        continue;                       // cell entirely on one side

                    for (int t = 0; t < 6; t++)
                    {
                        int nIn = 0, nOut = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int c = Tets[t, k];
                            tetVal[k] = cornerVal[c];
                            tetId[k] = cornerId[c];
                            tetPos[k] = cornerPos[c];
                            if (tetVal[k] > iso) inside[nIn++] = k;
                            else outside[nOut++] = k;
                        }
                        if (nIn == 0 || nIn == 4)
                            continue;

                        // Outward = from the inside corners toward the outside corners (grid space),
                        // transformed with the same linear map as the vertices.
                        Vector3 cin = Vector3.zero, cout = Vector3.zero;
                        for (int k = 0; k < nIn; k++) cin += tetPos[inside[k]];
                        for (int k = 0; k < nOut; k++) cout += tetPos[outside[k]];
                        Vector3 outward = gridToLocal.MultiplyVector(cout / nOut - cin / nIn);

                        if (nIn == 1 || nIn == 3)
                        {
                            int apex = nIn == 1 ? inside[0] : outside[0];
                            int a = -1, b = -1, c2 = -1;
                            for (int k = 0; k < 4; k++)
                            {
                                if (k == apex) continue;
                                int vi = EdgeVertex(apex, k, tetVal, tetId, tetPos, iso, gridToLocal, verts, edgeCache);
                                if (a < 0) a = vi; else if (b < 0) b = vi; else c2 = vi;
                            }
                            EmitTriangle(a, b, c2, outward, verts, tris);
                        }
                        else
                        {
                            int i0 = inside[0], i1 = inside[1], o0 = outside[0], o1 = outside[1];
                            quad[0] = EdgeVertex(i0, o0, tetVal, tetId, tetPos, iso, gridToLocal, verts, edgeCache);
                            quad[1] = EdgeVertex(i0, o1, tetVal, tetId, tetPos, iso, gridToLocal, verts, edgeCache);
                            quad[2] = EdgeVertex(i1, o1, tetVal, tetId, tetPos, iso, gridToLocal, verts, edgeCache);
                            quad[3] = EdgeVertex(i1, o0, tetVal, tetId, tetPos, iso, gridToLocal, verts, edgeCache);
                            EmitTriangle(quad[0], quad[1], quad[2], outward, verts, tris);
                            EmitTriangle(quad[0], quad[2], quad[3], outward, verts, tris);
                        }
                    }
                }
            }
        }
    }

    private static int EdgeVertex(int ka, int kb, float[] val, int[] id, Vector3[] pos, float iso,
                                  Matrix4x4 gridToLocal, List<Vector3> verts, Dictionary<long, int> cache)
    {
        int ia = id[ka], ib = id[kb];
        float va = val[ka], vb = val[kb];
        float t = Mathf.Abs(vb - va) < 1e-9f ? 0.5f : Mathf.Clamp01((iso - va) / (vb - va));
        // A corner that sits exactly on the iso-level is hit by every edge touching it; key such a
        // vertex by the grid point itself so all those edges weld to one vertex (keeps the mesh closed).
        // (Welding within WeldFraction of a corner also turns would-be sliver triangles into
        // index-degenerate ones, which EmitTriangle drops without opening a hole.)
        if (t <= WeldFraction) { ib = ia; t = 0f; }
        else if (t >= 1f - WeldFraction) { ia = ib; t = 1f; }
        long key = ia < ib ? ((long)ia << 32) | (uint)ib : ((long)ib << 32) | (uint)ia;
        if (cache.TryGetValue(key, out int existing))
            return existing;

        Vector3 p = Vector3.LerpUnclamped(pos[ka], pos[kb], t);
        int index = verts.Count;
        verts.Add(gridToLocal.MultiplyPoint3x4(p));
        cache[key] = index;
        return index;
    }

    private static void EmitTriangle(int a, int b, int c, Vector3 outward, List<Vector3> verts, List<int> tris)
    {
        if (a == b || b == c || a == c)
            return;                                         // index-degenerate (welded onto one corner)
        // Thin slivers are kept: dropping a topologically valid triangle by area opens a hole.
        Vector3 pa = verts[a], pb = verts[b], pc = verts[c];
        Vector3 n = Vector3.Cross(pb - pa, pc - pa);        // Unity front-face normal for (a, b, c)
        if (Vector3.Dot(n, outward) >= 0f)
        {
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
        else
        {
            tris.Add(a); tris.Add(c); tris.Add(b);
        }
    }
}
