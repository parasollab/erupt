using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// A voxel grid of tool-direction reachability bitmasks around a robot link frame, as baked by
/// hri_ws/planning_scene_utils/reachability_map.py ("RMAP" v1). Every voxel holds a uint32 whose
/// bit d is set when MoveIt found an IK solution with the tool +z axis along
/// <see cref="DirectionsRos"/>[d] at the voxel centre. Dexterity = popcount(mask &amp; filter) /
/// popcount(filter). All positions are in ROS axes of <see cref="FrameId"/>; callers convert with
/// the (x, z, y) swap from Conversions.cs. A zero direction vector is the generator's placeholder
/// for a position-dependent "radial outward" tool direction; it counts like any other bit and can
/// never be picked as the down bit.
/// </summary>
public sealed class ReachabilityMap
{
    public const string Magic = "RMAP";
    public const uint Version = 1;
    private const int MaxVoxels = 64 * 1024 * 1024;

    public string FrameId { get; private set; }
    public Vector3 OriginRos { get; private set; }          // centre of voxel (0,0,0)
    public float Resolution { get; private set; }
    public int Nx { get; private set; }
    public int Ny { get; private set; }
    public int Nz { get; private set; }
    public float MaxRadius { get; private set; }
    public Vector3[] DirectionsRos { get; private set; }
    public uint[] Masks { get; private set; }                // index = (iz*Ny + iy)*Nx + ix
    /// <summary>Index of the direction closest to straight down (0,0,-1); not assumed to be bit 0.</summary>
    public int DownBit { get; private set; }

    public int DirectionCount => DirectionsRos.Length;
    public uint AllMask => DirectionCount >= 32 ? uint.MaxValue : (1u << DirectionCount) - 1u;
    public uint DownMask => 1u << DownBit;
    public int VoxelCount => Nx * Ny * Nz;
    public float ZMinRos => OriginRos.z;
    public float ZMaxRos => OriginRos.z + (Nz - 1) * Resolution;
    public Vector3 MaxCornerRos => OriginRos + new Vector3(Nx - 1, Ny - 1, Nz - 1) * Resolution;

    private ReachabilityMap() { }

    public static bool TryParse(byte[] bytes, out ReachabilityMap map, out string error)
    {
        map = null;
        error = null;
        if (bytes == null || bytes.Length < 4)
        {
            error = "empty payload";
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var r = new BinaryReader(stream);          // BinaryReader is always little-endian
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != Magic)
            {
                error = "bad magic (not a reachability map)";
                return false;
            }
            uint version = r.ReadUInt32();
            if (version != Version)
            {
                error = $"unsupported version {version} (expected {Version})";
                return false;
            }
            int frameLen = checked((int)r.ReadUInt32());
            if (frameLen < 0 || frameLen > 256)
            {
                error = $"implausible frame_id length {frameLen}";
                return false;
            }
            string frame = Encoding.UTF8.GetString(r.ReadBytes(frameLen));
            var origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float res = r.ReadSingle();
            int nx = checked((int)r.ReadUInt32());
            int ny = checked((int)r.ReadUInt32());
            int nz = checked((int)r.ReadUInt32());
            float maxRadius = r.ReadSingle();
            int nDirs = checked((int)r.ReadUInt32());
            if (res <= 0f || nx <= 0 || ny <= 0 || nz <= 0 || nDirs <= 0 || nDirs > 32)
            {
                error = $"bad header: res={res} dims={nx}x{ny}x{nz} dirs={nDirs}";
                return false;
            }
            long voxels = (long)nx * ny * nz;
            if (voxels > MaxVoxels)
            {
                error = $"grid too large ({voxels} voxels)";
                return false;
            }
            var dirs = new Vector3[nDirs];
            for (int d = 0; d < nDirs; d++)
                dirs[d] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            long expected = stream.Position + voxels * 4;
            if (bytes.Length != expected)
            {
                error = $"payload length {bytes.Length} != expected {expected}";
                return false;
            }
            var masks = new uint[voxels];
            Buffer.BlockCopy(bytes, (int)stream.Position, masks, 0, (int)(voxels * 4));
            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < masks.Length; i++)
                    masks[i] = ReverseBytes(masks[i]);
            }

            int downBit = 0;
            float best = float.NegativeInfinity;
            for (int d = 0; d < nDirs; d++)
            {
                float dot = Vector3.Dot(dirs[d].normalized, new Vector3(0f, 0f, -1f));   // ROS down
                if (dot > best)
                {
                    best = dot;
                    downBit = d;
                }
            }

            map = new ReachabilityMap
            {
                FrameId = frame,
                OriginRos = origin,
                Resolution = res,
                Nx = nx, Ny = ny, Nz = nz,
                MaxRadius = maxRadius,
                DirectionsRos = dirs,
                Masks = masks,
                DownBit = downBit,
            };
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static uint ReverseBytes(uint v) =>
        (v >> 24) | ((v >> 8) & 0xFF00u) | ((v << 8) & 0xFF0000u) | (v << 24);

    public int Index(int ix, int iy, int iz) => (iz * Ny + iy) * Nx + ix;

    public bool InGrid(int ix, int iy, int iz) =>
        ix >= 0 && ix < Nx && iy >= 0 && iy < Ny && iz >= 0 && iz < Nz;

    public uint MaskAt(int ix, int iy, int iz) => InGrid(ix, iy, iz) ? Masks[Index(ix, iy, iz)] : 0u;

    /// <summary>Fraction of the filtered directions solvable at a voxel; 0 outside the grid.</summary>
    public float Dexterity(int ix, int iy, int iz, uint filter)
    {
        int denom = PopCount(filter);
        if (denom == 0 || !InGrid(ix, iy, iz))
            return 0f;
        return PopCount(Masks[Index(ix, iy, iz)] & filter) / (float)denom;
    }

    /// <summary>True when any filtered direction is solvable at the voxel nearest to a ROS point.</summary>
    public bool IsReachableNearest(Vector3 rosPoint, uint filter)
    {
        int ix = Mathf.RoundToInt((rosPoint.x - OriginRos.x) / Resolution);
        int iy = Mathf.RoundToInt((rosPoint.y - OriginRos.y) / Resolution);
        int iz = Mathf.RoundToInt((rosPoint.z - OriginRos.z) / Resolution);
        return (MaskAt(ix, iy, iz) & filter) != 0;
    }

    /// <summary>Trilinearly interpolated dexterity at a ROS point; 0 beyond the outermost voxel centres.</summary>
    public float SampleDexterity(Vector3 rosPoint, uint filter)
    {
        int denom = PopCount(filter);
        if (denom == 0)
            return 0f;
        float gx = (rosPoint.x - OriginRos.x) / Resolution;
        float gy = (rosPoint.y - OriginRos.y) / Resolution;
        float gz = (rosPoint.z - OriginRos.z) / Resolution;
        if (gx < -1f || gy < -1f || gz < -1f || gx > Nx || gy > Ny || gz > Nz)
            return 0f;
        int x0 = Mathf.FloorToInt(gx), y0 = Mathf.FloorToInt(gy), z0 = Mathf.FloorToInt(gz);
        float fx = gx - x0, fy = gy - y0, fz = gz - z0;
        float inv = 1f / denom;

        float Value(int x, int y, int z) => InGrid(x, y, z) ? PopCount(Masks[Index(x, y, z)] & filter) * inv : 0f;

        float c00 = Mathf.Lerp(Value(x0, y0, z0), Value(x0 + 1, y0, z0), fx);
        float c10 = Mathf.Lerp(Value(x0, y0 + 1, z0), Value(x0 + 1, y0 + 1, z0), fx);
        float c01 = Mathf.Lerp(Value(x0, y0, z0 + 1), Value(x0 + 1, y0, z0 + 1), fx);
        float c11 = Mathf.Lerp(Value(x0, y0 + 1, z0 + 1), Value(x0 + 1, y0 + 1, z0 + 1), fx);
        float c0 = Mathf.Lerp(c00, c10, fy);
        float c1 = Mathf.Lerp(c01, c11, fy);
        return Mathf.Lerp(c0, c1, fz);
    }

    /// <summary>Dexterity at (ix, iy) interpolated between the two z layers bracketing a ROS height.</summary>
    public float DexterityAtHeight(int ix, int iy, float zRos, uint filter)
    {
        float gz = (zRos - OriginRos.z) / Resolution;
        if (gz < -1f || gz > Nz)
            return 0f;
        int z0 = Mathf.FloorToInt(gz);
        float fz = gz - z0;
        return Mathf.Lerp(Dexterity(ix, iy, z0, filter), Dexterity(ix, iy, z0 + 1, filter), fz);
    }

    // ---- scalar field for isosurface extraction ------------------------------------------

    public int PaddedNx => Nx + 2;
    public int PaddedNy => Ny + 2;
    public int PaddedNz => Nz + 2;
    public int PaddedLength => PaddedNx * PaddedNy * PaddedNz;

    /// <summary>
    /// Fills a zero-padded scalar field (one empty voxel on every side so the surface closes).
    /// Padded index = (pz * PaddedNy + py) * PaddedNx + px with px = ix + 1 etc.
    /// <paramref name="binary"/>: 1 for any filtered direction solvable, else dexterity in [0, 1].
    /// </summary>
    public void FillScalarField(float[] dst, uint filter, bool binary)
    {
        if (dst == null || dst.Length < PaddedLength)
            throw new ArgumentException($"field buffer must hold {PaddedLength} floats");
        Array.Clear(dst, 0, PaddedLength);
        int denom = PopCount(filter);
        if (denom == 0)
            return;
        float inv = 1f / denom;
        int pnx = PaddedNx, pny = PaddedNy;
        for (int iz = 0; iz < Nz; iz++)
        {
            for (int iy = 0; iy < Ny; iy++)
            {
                int src = (iz * Ny + iy) * Nx;
                int dstRow = ((iz + 1) * pny + (iy + 1)) * pnx + 1;
                for (int ix = 0; ix < Nx; ix++)
                {
                    uint m = Masks[src + ix] & filter;
                    if (m == 0)
                        continue;
                    dst[dstRow + ix] = binary ? 1f : PopCount(m) * inv;
                }
            }
        }
    }

    /// <summary>
    /// One 3x3x3 box-filter pass over the interior of a padded field (padding stays zero so the
    /// isosurface remains closed). <paramref name="scratch"/> must be as long as <paramref name="field"/>.
    /// </summary>
    public static void BoxSmooth(float[] field, float[] scratch, int pnx, int pny, int pnz)
    {
        int len = pnx * pny * pnz;
        Array.Copy(field, scratch, len);
        const float inv27 = 1f / 27f;
        for (int z = 1; z < pnz - 1; z++)
        {
            for (int y = 1; y < pny - 1; y++)
            {
                for (int x = 1; x < pnx - 1; x++)
                {
                    float sum = 0f;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int rowZ = (z + dz) * pny;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int row = (rowZ + y + dy) * pnx + x;
                            sum += scratch[row - 1] + scratch[row] + scratch[row + 1];
                        }
                    }
                    field[(z * pny + y) * pnx + x] = sum * inv27;
                }
            }
        }
    }

    public static int PopCount(uint v)
    {
        v = v - ((v >> 1) & 0x55555555u);
        v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
        v = (v + (v >> 4)) & 0x0F0F0F0Fu;
        return (int)((v * 0x01010101u) >> 24);
    }

    public string Describe()
    {
        int reachable = 0;
        for (int i = 0; i < Masks.Length; i++)
            if (Masks[i] != 0) reachable++;
        return $"frame={FrameId} dims={Nx}x{Ny}x{Nz} res={Resolution:F3} m origin=({OriginRos.x:F2}, {OriginRos.y:F2}, {OriginRos.z:F2}) " +
               $"dirs={DirectionCount} (down bit {DownBit}) reachable voxels={reachable}/{VoxelCount}";
    }
}
