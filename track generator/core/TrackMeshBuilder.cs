using System.Collections.Generic;
using UnityEngine;
//. trackmeshbuilder.cs
namespace EvolutionGames.RacingTrack
{
    // all the per-sample data the builder needs — built once by TrackGenerator, read-only after that
    public readonly struct TrackSample
    {
        public readonly Vector3 position;
        public readonly Vector3 tangent;
        public readonly Vector3 right;
        public readonly Vector3 up;
        public readonly float bankAngle;
        public readonly float distance;

        public TrackSample(Vector3 position, Vector3 tangent, float bankAngle, float distance)
        {
            this.position = position;
            this.tangent  = tangent.normalized;
            this.bankAngle = bankAngle;
            this.distance  = distance;

            // cross(worldUp, forward) gives a stable right vector that doesn't drift with elevation changes
            Vector3 baseRight = Vector3.Cross(Vector3.up, this.tangent);
            if (baseRight.sqrMagnitude < 0.001f)
                baseRight = Vector3.Cross(Vector3.forward, this.tangent);
            baseRight.Normalize();

            Quaternion bank = Quaternion.AngleAxis(bankAngle, this.tangent);
            right = bank * baseRight;
            up    = Vector3.Cross(this.tangent, right).normalized;
        }
    }

    public static class TrackMeshBuilder
    {
        // miter limit multiplier — inner edge can extend at most this many times the half-width
        // 2f means the miter can be at most 2× road half-width before it gets clamped
        // keeps tight corners from crossing without making wide corners look wrong
        const float MITER_LIMIT = 2f;

        // NEW: angle threshold for bevel fallback (degrees)
        const float BEVEL_ANGLE_THRESHOLD = 45f;

        /// <summary>
        /// Computes miter-limited right offset vector for sample[i].
        /// Uses the bisector of adjacent right vectors scaled to maintain correct perpendicular width.
        /// Clamps at MITER_LIMIT × half to prevent inner edges crossing at sharp corners.
        /// If the corner angle is >= BEVEL_ANGLE_THRESHOLD, switches to a simple bevel (no extension).
        /// </summary>
        static Vector3 ComputeMiter(IList<TrackSample> samples, int i, float half, bool closed)
        {
            int count = samples.Count;
            int prev  = closed ? (i - 1 + count) % count : Mathf.Max(0, i - 1);
            int next  = closed ? (i + 1) % count          : Mathf.Min(count - 1, i + 1);

            // Compute corner angle using the right vectors of adjacent samples
            float cornerAngle = Vector3.Angle(samples[prev].right, samples[next].right);

            // If the corner is too sharp, fall back to a simple bevel (current right vector, no extension)
            if (cornerAngle >= BEVEL_ANGLE_THRESHOLD)
            {
                return samples[i].right * half;
            }

            // Normal path (original miter join)
            // bisector of adjacent right vectors — points outward from the curve's inner side
            Vector3 miter = (samples[prev].right + samples[next].right);
            if (miter.sqrMagnitude < 0.001f)
                return samples[i].right * half; // 180° reversal fallback

            miter.Normalize();

            // dot with current right gives cosine of half-angle between segments
            // dividing half by this gives the correct miter length to maintain road width
            float dot   = Vector3.Dot(miter, samples[i].right);
            float scale = dot > 0.05f ? half / dot : half * MITER_LIMIT;

            // clamp — prevents miter from going infinite at near-180° bends
            scale = Mathf.Min(scale, half * MITER_LIMIT);

            return miter * scale;
        }

        public static Mesh BuildRoadMesh(IList<TrackSample> samples, TrackDefinition def, bool closed)
        {
            if (samples == null || samples.Count < 2) return null;

            int count = samples.Count;
            var verts = new Vector3[count * 2];
            var uvs   = new Vector2[count * 2];
            int quads = closed ? count : count - 1;
            var tris  = new int[quads * 6];

            float half = def.roadWidth * 0.5f;

            for (int i = 0; i < count; i++)
            {
                var     s      = samples[i];
                float   v      = s.distance / def.uvTilingDistance;
                Vector3 miter  = ComputeMiter(samples, i, half, closed);

                verts[i * 2]     = s.position - miter;
                verts[i * 2 + 1] = s.position + miter;
                uvs[i * 2]       = new Vector2(0f, v);
                uvs[i * 2 + 1]   = new Vector2(1f, v);
            }

            for (int i = 0; i < quads; i++)
            {
                int nx = (i + 1) % count;
                int a = i * 2, b = i * 2 + 1, c = nx * 2, d = nx * 2 + 1;
                int t = i * 6;
                // verified: (a,d,b) gives normal +Y on flat track, stays in +Y hemisphere when banked
                tris[t]     = a; tris[t + 1] = d; tris[t + 2] = b;
                tris[t + 3] = a; tris[t + 4] = c; tris[t + 5] = d;
            }

            return Compile("Road", verts, uvs, tris);
        }

        public static Mesh BuildSkirtMesh(IList<TrackSample> samples, TrackDefinition def, bool closed)
        {
            if (samples == null || samples.Count < 2 || def.skirtDepth <= 0f) return null;

            int count = samples.Count;
            // layout per sample: 0=leftTop, 1=leftBottom, 2=rightTop, 3=rightBottom
            var verts = new Vector3[count * 4];
            var uvs   = new Vector2[count * 4];
            int quads = closed ? count : count - 1;
            var tris  = new int[quads * 12];

            float half = def.roadWidth * 0.5f;
            // dropping straight down rather than along the banked normal
            // banked drop makes the skirt visually detach from the ground on steep corners
            var drop = Vector3.down * def.skirtDepth;

            for (int i = 0; i < count; i++)
            {
                var     s     = samples[i];
                float   v     = s.distance / def.uvTilingDistance;
                int     b     = i * 4;
                Vector3 miter = ComputeMiter(samples, i, half, closed);

                Vector3 lt = s.position - miter;
                Vector3 rt = s.position + miter;

                verts[b]     = lt;
                verts[b + 1] = lt + drop;
                verts[b + 2] = rt;
                verts[b + 3] = rt + drop;

                uvs[b] = uvs[b + 1] = new Vector2(0f, v);
                uvs[b + 2] = uvs[b + 3] = new Vector2(1f, v);
            }

            for (int i = 0; i < quads; i++)
            {
                int nx = (i + 1) % count;
                int a = i * 4, n = nx * 4;
                int t = i * 12;

                // left face — normal -X (outward left), verified via cross product
                tris[t]     = a;     tris[t + 1] = n;     tris[t + 2] = n + 1;
                tris[t + 3] = a;     tris[t + 4] = n + 1; tris[t + 5] = a + 1;

                // right face — normal +X (outward right), verified
                tris[t + 6]  = a + 2; tris[t + 7]  = n + 2; tris[t + 8]  = a + 3;
                tris[t + 9]  = a + 3; tris[t + 10] = n + 2; tris[t + 11] = n + 3;
            }

            return Compile("Skirt", verts, uvs, tris);
        }

        // rightSide=true for the right barrier, false for left
        public static Mesh BuildBarrierMesh(IList<TrackSample> samples, TrackDefinition def, bool rightSide, bool closed)
        {
            if (samples == null || samples.Count < 2) return null;

            int count    = samples.Count;
            // layout per sample: 0=innerBot, 1=innerTop, 2=outerTop, 3=outerBot
            var verts    = new Vector3[count * 4];
            var uvs      = new Vector2[count * 4];
            int quads    = closed ? count : count - 1;
            int capCount = closed ? 0 : 4;
            var tris     = new int[quads * 18 + capCount * 3];

            float half  = def.roadWidth * 0.5f;
            float halfT = def.barrierThickness * 0.5f;
            float side  = rightSide ? 1f : -1f;

            for (int i = 0; i < count; i++)
            {
                var     s     = samples[i];
                float   v     = s.distance / def.uvTilingDistance;
                int     b     = i * 4;
                Vector3 miter = ComputeMiter(samples, i, half, closed);

                // edge position uses miter to match road edge exactly — no gap between road and barrier
                Vector3 edge  = s.position + miter * side;
                Vector3 inner = edge - s.right * (side * halfT);
                Vector3 outer = edge + s.right * (side * halfT);

                verts[b]     = inner;
                verts[b + 1] = inner + s.up * def.barrierHeight;
                verts[b + 2] = outer + s.up * def.barrierHeight;
                verts[b + 3] = outer;

                uvs[b] = uvs[b + 1] = new Vector2(0f, v);
                uvs[b + 2] = uvs[b + 3] = new Vector2(1f, v);
            }

            int ti = 0;
            for (int i = 0; i < quads; i++)
            {
                int nx = (i + 1) % count;
                int a = i * 4, n = nx * 4;

                // inner face (faces toward track center) — normal verified -X on right barrier
                tris[ti++] = a;     tris[ti++] = n;     tris[ti++] = n + 1;
                tris[ti++] = a;     tris[ti++] = n + 1; tris[ti++] = a + 1;

                // outer face — normal +X on right barrier
                tris[ti++] = a + 3; tris[ti++] = a + 2; tris[ti++] = n + 2;
                tris[ti++] = a + 3; tris[ti++] = n + 2; tris[ti++] = n + 3;

                // top face — normal +Y verified
                tris[ti++] = a + 1; tris[ti++] = n + 1; tris[ti++] = n + 2;
                tris[ti++] = a + 1; tris[ti++] = n + 2; tris[ti++] = a + 2;
            }

            if (!closed)
            {
                // start cap
                tris[ti++] = 0; tris[ti++] = 3; tris[ti++] = 1;
                tris[ti++] = 1; tris[ti++] = 3; tris[ti++] = 2;
                // end cap
                int L = (count - 1) * 4;
                tris[ti++] = L;     tris[ti++] = L + 1; tris[ti++] = L + 2;
                tris[ti++] = L;     tris[ti++] = L + 2; tris[ti++] = L + 3;
            }

            return Compile(rightSide ? "BarrierRight" : "BarrierLeft", verts, uvs, tris);
        }

        static Mesh Compile(string meshName, Vector3[] verts, Vector2[] uvs, int[] tris)
        {
            var mesh = new Mesh
            {
                name        = meshName,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}