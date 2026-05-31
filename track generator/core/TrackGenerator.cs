using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

// TrackGenerator.cs

namespace EvolutionGames.RacingTrack
{
    [RequireComponent(typeof(SplineContainer))]
    [RequireComponent(typeof(TrackLayoutGenerator))]
    public class TrackGenerator : MonoBehaviour
    {
        public TrackDefinition definition;

        // ─── CAR SLOT ────────────────────────────────────────────
        public GameObject demoCar;   // Drag your "bent" root here
        // ─────────────────────────────────────────────────────────

        [Header("Car Start Offset")]
        public float startOffsetDistance = 5f;   // meters forward from first node

        SplineContainer      _splineContainer;
        TrackLayoutGenerator _layout;

        GameObject _roadObj;
        GameObject _skirtObj;
        GameObject _barrierLeftObj;
        GameObject _barrierRightObj;

        void Awake()
        {
            _splineContainer = GetComponent<SplineContainer>();
            _layout          = GetComponent<TrackLayoutGenerator>();
        }

        public void Generate()
        {
            if (definition == null)
            {
                Debug.LogError("[TrackGenerator] No TrackDefinition assigned.");
                return;
            }

            if (_splineContainer == null) _splineContainer = GetComponent<SplineContainer>();
            if (_layout          == null) _layout          = GetComponent<TrackLayoutGenerator>();

            _layout.Generate(_splineContainer.Spline, definition);

            if (_splineContainer.Spline.Count < 2)
            {
                Debug.LogError("[TrackGenerator] Generation failed — fewer than 2 knots.");
                return;
            }

            // roll a new random seed after each generation
            _layout.seed = System.Environment.TickCount ^ System.Guid.NewGuid().GetHashCode();

            Build();

            // ─── MOVE CAR TO START (with offset) ───────────────────────────────
            MoveCarToStart();
            // ────────────────────────────────────────────────────────────────────
        }

        public void ClearTrack()
        {
            var names = new[] { "Track_Road", "Track_Skirt",
                                 "Track_BarrierLeft", "Track_BarrierRight" };
            foreach (var n in names)
            {
                var child = transform.Find(n);
                if (child != null) DestroyImmediate(child.gameObject);
            }

            _roadObj         = null;
            _skirtObj        = null;
            _barrierLeftObj  = null;
            _barrierRightObj = null;
        }

        void Build()
        {
            if (_splineContainer == null) _splineContainer = GetComponent<SplineContainer>();
            if (_layout          == null) _layout          = GetComponent<TrackLayoutGenerator>();

            var spline = _splineContainer.Spline;
            if (spline == null || spline.Count < 2) return;

            bool closed   = _layout.closedTrack;
            spline.Closed = closed;

            var samples = SampleSpline(spline, closed);
            if (samples.Count < 2) return;

            BuildOrUpdateMesh(ref _roadObj,  "Track_Road",
                TrackMeshBuilder.BuildRoadMesh(samples, definition, closed),
                definition.roadMaterial, withCollider: true);

            BuildOrUpdateMesh(ref _skirtObj, "Track_Skirt",
                TrackMeshBuilder.BuildSkirtMesh(samples, definition, closed),
                definition.skirtMaterial, withCollider: false);

            if (definition.generateBarriers)
            {
                BuildOrUpdateMesh(ref _barrierLeftObj, "Track_BarrierLeft",
                    TrackMeshBuilder.BuildBarrierMesh(samples, definition, false, closed),
                    definition.barrierMaterial, withCollider: false);

                BuildOrUpdateMesh(ref _barrierRightObj, "Track_BarrierRight",
                    TrackMeshBuilder.BuildBarrierMesh(samples, definition, true, closed),
                    definition.barrierMaterial, withCollider: false);
            }
            else
            {
                DestroyImmediate(_barrierLeftObj);
                DestroyImmediate(_barrierRightObj);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Curvature‑adaptive sampling (angle threshold = 15°)
        // ─────────────────────────────────────────────────────────────────────────
        List<TrackSample> SampleSpline(Spline spline, bool closed)
        {
            // Base sample count (uniform)
            int minSamples   = Mathf.CeilToInt(_layout.trackLength / 3f);
            int totalSamples = Mathf.Max(spline.Count * definition.samplesPerSegment,
                                         minSamples);

            // ---- First pass: evaluate base samples and store t, position, tangent ----
            var basePoints = new List<(float t, Vector3 pos, Vector3 tan)>(totalSamples);
            for (int i = 0; i < totalSamples; i++)
            {
                float t;
                if (closed)
                    t = (float)i / totalSamples;               // 0 … (totalSamples‑1)/totalSamples
                else
                    t = (float)i / (totalSamples - 1);         // 0 … 1

                float3 pos3 = spline.EvaluatePosition(t);
                float3 tan3 = spline.EvaluateTangent(t);
                basePoints.Add((t,
                                new Vector3(pos3.x, pos3.y, pos3.z),
                                new Vector3(tan3.x, tan3.y, tan3.z)));
            }

            // ---- Second pass: collect all t values (base + extra) ----
            var allT = new List<float>(totalSamples);
            // Add all base t values first
            for (int i = 0; i < basePoints.Count; i++)
                allT.Add(basePoints[i].t);

            int N = basePoints.Count;
            // Helper to get angle between tangents at index i and j (with wrap)
            for (int i = 0; i < N; i++)
            {
                int j = (i + 1) % N;
                // Skip the wrap segment if open ended
                if (!closed && j == 0) continue;

                float angle = Vector3.Angle(basePoints[i].tan, basePoints[j].tan);
                if (angle > 15f)
                {
                    int extraCount = Mathf.FloorToInt(angle / 15f);
                    extraCount = Mathf.Clamp(extraCount, 1, 8);

                    float t_i = basePoints[i].t;
                    float t_j = basePoints[j].t;

                    // Handle wrap segment (t_j == 0 means we go to t = 1)
                    if (closed && j == 0)
                        t_j = 1f;

                    float step = (t_j - t_i) / (extraCount + 1);
                    for (int k = 1; k <= extraCount; k++)
                    {
                        float t_mid = t_i + step * k;
                        allT.Add(t_mid);
                    }
                }
            }

            // ---- Sort all t values and remove duplicates (if any) ----
            allT.Sort();
            var uniqueT = new List<float>(allT.Count);
            for (int i = 0; i < allT.Count; i++)
            {
                if (i == 0 || Mathf.Abs(allT[i] - allT[i-1]) > 0.00001f)
                    uniqueT.Add(allT[i]);
            }

            // ---- Third pass: evaluate at each t and build final TrackSample list ----
            var samples = new List<TrackSample>(uniqueT.Count);
            float distAccum = 0f;
            Vector3 prevPos = Vector3.zero;

            for (int idx = 0; idx < uniqueT.Count; idx++)
            {
                float t = uniqueT[idx];
                float3 pos3 = spline.EvaluatePosition(t);
                float3 tan3 = spline.EvaluateTangent(t);
                Vector3 pos = new Vector3(pos3.x, pos3.y, pos3.z);
                Vector3 tan = new Vector3(tan3.x, tan3.y, tan3.z);
                float bankDegrees = _layout.bankingCurve.Evaluate(t) * _layout.maxBankAngle;

                if (idx > 0)
                    distAccum += Vector3.Distance(prevPos, pos);
                else
                    distAccum = 0f;

                samples.Add(new TrackSample(pos, tan, bankDegrees, distAccum));
                prevPos = pos;
            }

            return samples;
        }

        void BuildOrUpdateMesh(ref GameObject obj, string objName,
                                Mesh mesh, Material mat, bool withCollider)
        {
            if (mesh == null) return;

            if (obj == null)
            {
                var existing = transform.Find(objName);
                obj = existing != null
                    ? existing.gameObject
                    : new GameObject(objName);
                obj.transform.SetParent(transform, false);
            }

            var mf = obj.GetComponent<MeshFilter>();
            if (mf == null) mf = obj.AddComponent<MeshFilter>();

            var mr = obj.GetComponent<MeshRenderer>();
            if (mr == null) mr = obj.AddComponent<MeshRenderer>();

            mf.sharedMesh     = mesh;
            mr.sharedMaterial = mat;

            if (withCollider)
            {
                var mc = obj.GetComponent<MeshCollider>();
                if (mc == null) mc = obj.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
            }
            else
            {
                var mc = obj.GetComponent<MeshCollider>();
                if (mc != null) DestroyImmediate(mc);
            }
        }

        // ─── MOVE CAR TO START (WITH OFFSET) ─────────────────────────────────────────────
        void MoveCarToStart()
        {
            if (demoCar == null)
            {
                Debug.LogWarning("[TrackGenerator] No demo car assigned. Skipping.");
                return;
            }

            Transform carTransform = demoCar.transform;

            Spline spline = _splineContainer.Spline;
            if (spline.Count < 2) return;

            // first point on the spline (t = 0)
            float3 pos3 = spline.EvaluatePosition(0f);
            float3 tan3 = spline.EvaluateTangent(0f);

            Vector3 startPos = new Vector3(pos3.x, pos3.y, pos3.z);
            Vector3 startDir = new Vector3(tan3.x, tan3.y, tan3.z).normalized;

            // Apply offset forward along the spline direction
            Vector3 offsetPos = startPos + startDir * startOffsetDistance;

            // move car slightly above road to avoid wheel clipping
            carTransform.position = offsetPos + Vector3.up * 0.5f;
            carTransform.rotation = Quaternion.LookRotation(startDir);

            // reset physics
            Rigidbody rb = demoCar.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }
}