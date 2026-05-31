using System;
using System.Collections.Generic;
using Random = System.Random;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

// TrackLayoutGenerator.cs

namespace EvolutionGames.RacingTrack
{
    public class TrackLayoutGenerator : MonoBehaviour
    {
        [Header("Generation")]
        public int   seed           = 0;
        [Range(0f, 1f)]
        public float layoutVariance = 0.25f;
        public bool  openEnded      = false;

        [Header("Dimensions")]
        public float trackLength    = 800f;

        // Removed [Header("Speed")] and designSpeed field
        // Removed [Header("Elevation")] and elevationVariance field

        [Header("Seed Pre-screening")]
        public bool autoPreScan     = true;

        // ── Outputs for TrackGenerator ────────────────────────────────────────
        [HideInInspector] public float          maxBankAngle;
        [HideInInspector] public bool           closedTrack;
        [HideInInspector] public AnimationCurve bankingCurve =
                                     AnimationCurve.Constant(0f, 1f, 0f);

        // ── Seed pool ─────────────────────────────────────────────────────────
        [HideInInspector] public int goodSeedCount;
        [HideInInspector] public int badSeedCount;

        Queue<int>   _goodSeeds = new Queue<int>();
        HashSet<int> _badSeeds  = new HashSet<int>();

        const int PRESCAN_BATCH    = 200;
        const int GOOD_SEED_REFILL = 20;

        // ── Internal ──────────────────────────────────────────────────────────
        float _roadWidth;
        float _minTurnRadius;
        float _minClearance;
        float _maxGradePercent;

        float _lastTrackLength = -1f;
        float _lastVariance    = -1f;
        bool  _lastOpenEnded   = false;
        float _lastRoadWidth   = -1f;

        const int   MAX_ATTEMPTS     = 20;
        const int   VALIDATE_SAMPLES = 120;
        const float CLOSING_RESERVE  = 0.25f;
        const int   HOMING_SEGMENTS  = 4;
        const float MIN_CLOSED_LENGTH = 700f;

        // ─────────────────────────────────────────────────────────────────────
        // Road primitives
        // ─────────────────────────────────────────────────────────────────────

        enum Primitive
        {
            Straight,
            GentleArcLeft,    GentleArcRight,
            MediumArcLeft,    MediumArcRight,
            HairpinLeft,      HairpinRight,
            ChicaneLeftRight, ChicaneRightLeft,
            Switchback,
            ClothoidLeft,     ClothoidRight
        }

        // ─────────────────────────────────────────────────────────────────────
        // Entry point
        // ─────────────────────────────────────────────────────────────────────

        public void Generate(Spline spline, TrackDefinition definition)
        {
            closedTrack = !openEnded;

            // hard minimum for closed circuit only
            if (!openEnded && trackLength < MIN_CLOSED_LENGTH)
            {
                Debug.LogError($"[TrackLayoutGenerator] Closed circuit requires at least " +
                               $"{MIN_CLOSED_LENGTH}m. Current value: {trackLength:F0}m. " +
                               $"For shorter tracks use Open Ended mode.");
                return;
            }

            ComputeConstraints(definition);

            // feasibility warning — not a hard stop, but informs the user
            if (!openEnded)
            {
                float minViable = _minTurnRadius * 50f;
                if (trackLength < minViable)
                    Debug.LogWarning($"[TrackLayoutGenerator] trackLength:{trackLength:F0}m " +
                                     $"may be tight for roadWidth:{_roadWidth:F0}m. " +
                                     $"Recommended minimum: {minViable:F0}m.");
            }

            // invalidate pool if parameters changed
            if (ParametersChanged(definition))
            {
                ClearPool();
                if (autoPreScan) PreScanSeeds(definition);
            }

            // refill if pool running low
            if (_goodSeeds.Count < GOOD_SEED_REFILL && autoPreScan)
                PreScanSeeds(definition);

            bool success = false;

            for (int attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
            {
                int trySeed = _goodSeeds.Count > 0
                    ? _goodSeeds.Dequeue()
                    : NextSeed(seed);

                seed = trySeed;
                spline.Clear();

                if (openEnded)
                    TryGenerateOpenEnded(spline, definition);
                else
                    TryGenerateFullCircuit(spline, definition);

                if (spline.Count < 2)
                {
                    _badSeeds.Add(trySeed);
                    UpdatePoolDisplay();
                    seed = NextSeed(seed);
                    continue;
                }

                string failReason = ValidateResult(spline);
                if (failReason == null)
                {
                    string mode = openEnded ? "open" : "closed";
                    Debug.Log($"[TrackLayoutGenerator] Valid — mode:{mode} " +
                              $"seed:{seed} knots:{spline.Count} " +
                              $"attempt:{attempt + 1} " +
                              $"pool:{_goodSeeds.Count} good");
                    success = true;
                    break;
                }

                _badSeeds.Add(trySeed);
                UpdatePoolDisplay();
                seed = NextSeed(seed);
            }

            if (!success)
                Debug.LogError($"[TrackLayoutGenerator] Failed after {MAX_ATTEMPTS} " +
                               $"attempts — trackLength:{trackLength:F0}m " +
                               $"roadWidth:{_roadWidth:F0}m.");

            UpdatePoolDisplay();
            bankingCurve = DeriveBankingCurve(spline);
            seed = NextSeed(seed);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Seed pre-screening
        // ─────────────────────────────────────────────────────────────────────

        public void PreScanSeeds(TrackDefinition definition)
        {
            if (definition == null) return;
            ComputeConstraints(definition);

            int scanSeed = System.Environment.TickCount;

            for (int i = 0; i < PRESCAN_BATCH; i++)
            {
                scanSeed = NextSeed(scanSeed);
                if (_badSeeds.Contains(scanSeed)) continue;

                bool feasible = openEnded
                    ? FeasibleOpenEnded(scanSeed)
                    : FeasibleFullCircuit(scanSeed);

                if (feasible) _goodSeeds.Enqueue(scanSeed);
                else          _badSeeds.Add(scanSeed);
            }

            CacheParameters(definition);
            UpdatePoolDisplay();
        }

        // ── Lightweight feasibility — open ended ──────────────────────────────

        bool FeasibleOpenEnded(int testSeed)
        {
            float segLen   = ComputeSegLen();
            float budget   = trackLength;
            float used     = 0f;
            int   placed   = 0;
            var   rng      = new Random(testSeed);
            var   pool     = OpenEndedPool();
            Primitive last = Primitive.Straight;

            for (int i = 0; i < 20; i++)
            {
                var prim = pool[rng.Next(pool.Count)];
                if (prim == last) continue;
                float arc = GetPrimitiveArcLength(prim, segLen);
                if (arc > budget - used) break;
                used  += arc;
                last   = prim;
                placed++;
                if (placed >= 2) return true;
            }
            return placed >= 2;
        }

        // ── Lightweight feasibility — full circuit ────────────────────────────

        bool FeasibleFullCircuit(int testSeed)
        {
            var   rng         = new Random(testSeed);
            float segLen      = ComputeSegLen();
            int   workingSegs = Mathf.Clamp(
                                    Mathf.FloorToInt(trackLength / segLen), 3, 12);
            segLen            = trackLength / workingSegs;

            float spendable  = trackLength * (1f - CLOSING_RESERVE);
            float closing    = trackLength * CLOSING_RESERVE;
            float budgetUsed = 0f;

            var currentPos   = Vector2.zero;
            var currentDir   = Vector2.right;
            var startPos     = Vector2.zero;
            Primitive last   = Primitive.Straight;

            var fullPool  = FullPool();
            var lightPool = LightPool();

            for (int seg = 0; seg < workingSegs; seg++)
            {
                float remaining  = spendable - budgetUsed;
                if (remaining < _minTurnRadius * 2f) break;

                int  segsFromEnd = workingSegs - 1 - seg;
                bool isHoming    = segsFromEnd < HOMING_SEGMENTS;

                List<Primitive> pool;

                if (isHoming)
                {
                    Vector2 toStart = (startPos - currentPos).normalized;
                    float curAng = Mathf.Atan2(currentDir.y, currentDir.x) * Mathf.Rad2Deg;
                    float tgtAng = Mathf.Atan2(toStart.y,    toStart.x)    * Mathf.Rad2Deg;
                    float delta  = Mathf.DeltaAngle(curAng, tgtAng);
                    float agg    = 1f - (float)segsFromEnd / HOMING_SEGMENTS;

                    pool = Mathf.Abs(delta) < 25f
                        ? (agg > 0.6f
                            ? new List<Primitive> { Primitive.Straight }
                            : new List<Primitive>
                              { Primitive.Straight,
                                Primitive.GentleArcLeft, Primitive.GentleArcRight })
                        : delta > 0f
                            ? (agg > 0.6f
                                ? new List<Primitive>
                                  { Primitive.MediumArcLeft, Primitive.HairpinLeft }
                                : new List<Primitive>
                                  { Primitive.GentleArcLeft,  Primitive.MediumArcLeft,
                                    Primitive.ClothoidLeft,   Primitive.HairpinLeft })
                            : (agg > 0.6f
                                ? new List<Primitive>
                                  { Primitive.MediumArcRight, Primitive.HairpinRight }
                                : new List<Primitive>
                                  { Primitive.GentleArcRight, Primitive.MediumArcRight,
                                    Primitive.ClothoidRight,  Primitive.HairpinRight });
                }
                else
                    pool = remaining < spendable * 0.4f ? lightPool : fullPool;

                Primitive pick  = Primitive.Straight;
                bool      found = false;

                for (int t = 0; t < 15; t++)
                {
                    var candidate = pool[rng.Next(pool.Count)];
                    if (candidate == last && t < 8) continue;
                    if (GetPrimitiveArcLength(candidate, segLen) <= remaining)
                    {
                        pick = candidate; found = true; break;
                    }
                }

                if (!found) break;

                float arc     = GetPrimitiveArcLength(pick, segLen);
                float heading = GetPrimitiveHeadingChange(pick, segLen);
                float rad     = heading * Mathf.Deg2Rad;
                float newDirX = currentDir.x * Mathf.Cos(rad)
                              - currentDir.y * Mathf.Sin(rad);
                float newDirY = currentDir.x * Mathf.Sin(rad)
                              + currentDir.y * Mathf.Cos(rad);

                currentPos += currentDir * (arc * 0.7f);
                currentDir  = new Vector2(newDirX, newDirY).normalized;
                budgetUsed += arc;
                last        = pick;
            }

            float gapDist      = Vector2.Distance(currentPos, startPos);
            float minArcNeeded = gapDist * (Mathf.PI / 2f);
            return minArcNeeded <= closing;
        }

        float GetPrimitiveHeadingChange(Primitive prim, float segLen)
        {
            float gentleR  = Mathf.Max(segLen * 0.6f,  _minTurnRadius * 4f);
            float mediumR  = Mathf.Max(segLen * 0.35f, _minTurnRadius * 2.5f);
            float gentleAng = Mathf.Clamp(
                (segLen / (2f * Mathf.PI * gentleR)) * 360f, 15f, 170f);
            float mediumAng = Mathf.Clamp(
                (segLen / (2f * Mathf.PI * mediumR)) * 360f, 15f, 170f);

            return prim switch
            {
                Primitive.Straight          =>   0f,
                Primitive.GentleArcLeft     => + gentleAng,
                Primitive.GentleArcRight    => - gentleAng,
                Primitive.MediumArcLeft     => + mediumAng,
                Primitive.MediumArcRight    => - mediumAng,
                Primitive.HairpinLeft       => +180f,
                Primitive.HairpinRight      => -180f,
                Primitive.ChicaneLeftRight  =>   0f,
                Primitive.ChicaneRightLeft  =>   0f,
                Primitive.Switchback        => +360f,
                Primitive.ClothoidLeft      => + mediumAng * 0.7f,
                Primitive.ClothoidRight     => - mediumAng * 0.7f,
                _                           =>   0f
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Primitive pools
        // ─────────────────────────────────────────────────────────────────────

        static List<Primitive> FullPool() => new()
        {
            Primitive.Straight,         Primitive.Straight,
            Primitive.GentleArcLeft,    Primitive.GentleArcRight,
            Primitive.MediumArcLeft,    Primitive.MediumArcRight,
            Primitive.HairpinLeft,      Primitive.HairpinRight,
            Primitive.ChicaneLeftRight, Primitive.ChicaneRightLeft,
            Primitive.ClothoidLeft,     Primitive.ClothoidRight,
            Primitive.Switchback,
        };

        static List<Primitive> LightPool() => new()
        {
            Primitive.Straight,
            Primitive.GentleArcLeft,  Primitive.GentleArcRight,
            Primitive.MediumArcLeft,  Primitive.MediumArcRight,
            Primitive.ClothoidLeft,   Primitive.ClothoidRight,
        };

        static List<Primitive> OpenEndedPool() => new()
        {
            Primitive.Straight,         Primitive.Straight,
            Primitive.GentleArcLeft,    Primitive.GentleArcRight,
            Primitive.MediumArcLeft,    Primitive.MediumArcRight,
            Primitive.HairpinLeft,      Primitive.HairpinRight,
            Primitive.ChicaneLeftRight, Primitive.ChicaneRightLeft,
            Primitive.ClothoidLeft,     Primitive.ClothoidRight,
        };

        // ─────────────────────────────────────────────────────────────────────
        // PATH 1 — Open ended
        // ─────────────────────────────────────────────────────────────────────

        void TryGenerateOpenEnded(Spline spline, TrackDefinition definition)
        {
            var   rng     = new Random(seed);
            float segLen  = ComputeSegLen();
            int   maxSegs = Mathf.Clamp(
                                Mathf.FloorToInt(trackLength / segLen), 3, 16);

            var controlPoints = new List<Vector2>();
            var currentPos    = Vector2.zero;
            var currentDir    = Vector2.right;
            float budgetUsed  = 0f;
            Primitive lastPrim = Primitive.Straight;
            var pool           = OpenEndedPool();

            for (int seg = 0; seg < maxSegs; seg++)
            {
                float remaining = trackLength - budgetUsed;
                if (remaining < _minTurnRadius * 2f) break;

                Primitive pick  = Primitive.Straight;
                bool      found = false;

                for (int t = 0; t < 20; t++)
                {
                    var candidate = pool[rng.Next(pool.Count)];
                    if (candidate == lastPrim && t < 10) continue;
                    if (GetPrimitiveArcLength(candidate, segLen) <= remaining)
                    {
                        pick = candidate; found = true; break;
                    }
                }

                if (!found)
                {
                    float sLen = Mathf.Min(segLen, remaining);
                    if (sLen < _minTurnRadius * 2f) break;
                    pick = Primitive.Straight;
                }

                var local    = GetPrimitivePoints(pick, segLen);
                if (local.Count < 2) continue;
                var world    = TransformSnippet(local, currentPos, currentDir);
                int startIdx = controlPoints.Count == 0 ? 0 : 1;
                for (int i = startIdx; i < world.Count; i++)
                    controlPoints.Add(world[i]);

                budgetUsed += GetPrimitiveArcLength(pick, segLen);
                lastPrim    = pick;
                currentPos  = world[world.Count - 1];
                currentDir  = (world[world.Count - 1] -
                               world[world.Count - 2]).normalized;
            }

            if (controlPoints.Count < 2) return;

            CenterPoints(controlPoints);
            ApplyVariance(controlPoints, segLen, rng);
            EnforceClearance(controlPoints, _minClearance, true);

            var sampled = SampleCentripetalOpen(controlPoints);
            EnforceMinTurnRadius(sampled, _minTurnRadius, true);
            AssignElevation(sampled, rng);
            EnforceGrade(sampled, true);

            var knotPts = Downsample(sampled, Mathf.Max(controlPoints.Count * 2, 6));
            WriteSpline(spline, knotPts, false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // PATH 2 — Full closed circuit
        // ─────────────────────────────────────────────────────────────────────

        void TryGenerateFullCircuit(Spline spline, TrackDefinition definition)
        {
            var   rng         = new Random(seed);
            float segLen      = ComputeSegLen();
            int   workingSegs = Mathf.Clamp(
                                    Mathf.FloorToInt(trackLength / segLen), 3, 12);
            segLen            = trackLength / workingSegs;

            var controlPoints = BuildFullCircuitPoints(rng, segLen, workingSegs);
            if (controlPoints.Count < 3) return;

            CenterPoints(controlPoints);
            ApplyVariance(controlPoints, segLen, rng);
            EnforceClearance(controlPoints, _minClearance, false);

            var ctrl3D = controlPoints.ConvertAll(p => new Vector3(p.x, 0f, p.y));
            UncrossSamples(ctrl3D);
            for (int i = 0; i < controlPoints.Count; i++)
                controlPoints[i] = new Vector2(ctrl3D[i].x, ctrl3D[i].z);

            var sampled = SampleCentripetalClosed(controlPoints);
            UncrossSamples(sampled);
            EnforceMinTurnRadius(sampled, _minTurnRadius, false);
            AssignElevation(sampled, rng);
            EnforceGrade(sampled, false);

            int knotTarget = Mathf.Max(workingSegs * 3, 8);
            var knotPts    = Downsample(sampled, knotTarget);
            UncrossSamples(knotPts);
            WriteSpline(spline, knotPts, true);
        }

        List<Vector2> BuildFullCircuitPoints(Random rng, float segLen, int workingSegs)
        {
            var result     = new List<Vector2>();
            var currentPos = Vector2.zero;
            var currentDir = Vector2.right;
            var startPos   = Vector2.zero;

            float spendable  = trackLength * (1f - CLOSING_RESERVE);
            float closing    = trackLength * CLOSING_RESERVE;
            float budgetUsed = 0f;
            Primitive lastPrim = Primitive.Straight;

            var fullPool  = FullPool();
            var lightPool = LightPool();

            for (int seg = 0; seg < workingSegs; seg++)
            {
                float remaining  = spendable - budgetUsed;
                if (remaining < _minTurnRadius * 2f) break;

                int  segsFromEnd = workingSegs - 1 - seg;
                bool isHoming    = segsFromEnd < HOMING_SEGMENTS;

                List<Primitive> candidatePool;

                if (isHoming && result.Count >= 2)
                {
                    Vector2 toStart = (startPos - currentPos).normalized;
                    float curAng = Mathf.Atan2(currentDir.y, currentDir.x) * Mathf.Rad2Deg;
                    float tgtAng = Mathf.Atan2(toStart.y,    toStart.x)    * Mathf.Rad2Deg;
                    float delta  = Mathf.DeltaAngle(curAng, tgtAng);
                    float agg    = 1f - (float)segsFromEnd / HOMING_SEGMENTS;

                    if (Mathf.Abs(delta) < 25f)
                        candidatePool = agg > 0.6f
                            ? new List<Primitive> { Primitive.Straight }
                            : new List<Primitive>
                              { Primitive.Straight,
                                Primitive.GentleArcLeft, Primitive.GentleArcRight };
                    else if (delta > 0f)
                        candidatePool = agg > 0.6f
                            ? new List<Primitive>
                              { Primitive.MediumArcLeft, Primitive.HairpinLeft }
                            : new List<Primitive>
                              { Primitive.GentleArcLeft,  Primitive.MediumArcLeft,
                                Primitive.ClothoidLeft,   Primitive.HairpinLeft };
                    else
                        candidatePool = agg > 0.6f
                            ? new List<Primitive>
                              { Primitive.MediumArcRight, Primitive.HairpinRight }
                            : new List<Primitive>
                              { Primitive.GentleArcRight, Primitive.MediumArcRight,
                                Primitive.ClothoidRight,  Primitive.HairpinRight };
                }
                else
                    candidatePool = remaining < spendable * 0.4f
                        ? lightPool : fullPool;

                Primitive pick  = Primitive.Straight;
                bool      found = false;

                for (int t = 0; t < 20; t++)
                {
                    var candidate = candidatePool[rng.Next(candidatePool.Count)];
                    if (candidate == lastPrim && t < 10) continue;
                    if (candidate == Primitive.HairpinLeft &&
                        lastPrim  == Primitive.HairpinLeft)
                        candidate = Primitive.HairpinRight;
                    if (candidate == Primitive.HairpinRight &&
                        lastPrim  == Primitive.HairpinRight)
                        candidate = Primitive.HairpinLeft;
                    if (GetPrimitiveArcLength(candidate, segLen) <= remaining)
                    {
                        pick = candidate; found = true; break;
                    }
                }

                if (!found)
                {
                    float sLen = Mathf.Min(segLen, remaining);
                    if (sLen < _minTurnRadius * 2f) break;
                    pick = Primitive.Straight;
                }

                var local    = GetPrimitivePoints(pick, segLen);
                if (local.Count < 2) continue;
                var world    = TransformSnippet(local, currentPos, currentDir);
                int startIdx = result.Count == 0 ? 0 : 1;
                for (int i = startIdx; i < world.Count; i++)
                    result.Add(world[i]);

                budgetUsed += GetPrimitiveArcLength(pick, segLen);
                lastPrim    = pick;
                currentPos  = world[world.Count - 1];
                currentDir  = (world[world.Count - 1] -
                               world[world.Count - 2]).normalized;
            }

            if (result.Count >= 2)
            {
                float gapDist      = Vector2.Distance(currentPos, startPos);
                float minArcNeeded = gapDist * (Mathf.PI / 2f);

                if (minArcNeeded <= closing)
                {
                    int bridges = Mathf.Clamp(
                        Mathf.CeilToInt(gapDist / (_minTurnRadius * 2f)), 1, 5);
                    for (int b = 1; b <= bridges; b++)
                    {
                        float   t       = (float)b / (bridges + 1);
                        Vector2 toStart = (startPos - currentPos).normalized;
                        Vector2 blend   = Vector2.Lerp(currentDir, toStart, t).normalized;
                        result.Add(currentPos + blend * (gapDist / (bridges + 1) * b));
                    }
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation
        // ─────────────────────────────────────────────────────────────────────

        string ValidateResult(Spline spline)
        {
            var pts = SampleSplinePositions(spline, VALIDATE_SAMPLES);

            // spike check applies to both open and closed
            // no two adjacent sampled points may be more than roadWidth * 15 apart
            // catches closing bridge spikes and bad primitive transitions before
            // they reach the mesh builder and cause 500-unit triangle warnings
            float maxSegDist = _roadWidth * 15f;
            if (HasSpike(pts, maxSegDist))
                return $"spike detected — adjacent points > {maxSegDist:F1}m apart";

            if (openEnded)
            {
                // open ended: check self-intersection and clearance
                // skip length bounds — path stops when budget runs out, always shorter
                if (HasSelfIntersection(pts, true))
                    return "open path self-intersects";
                if (HasClearanceViolation(pts, _minClearance, true))
                    return $"open path clearance < {_minClearance:F1}m";
                return null;
            }

            // closed circuit
            float actual     = MeasureSplineLength(spline, VALIDATE_SAMPLES);
            float minAllowed = trackLength * 0.5f;
            float maxAllowed = trackLength * 1.3f;

            if (actual < minAllowed)
                return $"too short ({actual:F0}m < {minAllowed:F0}m)";
            if (actual > maxAllowed)
                return $"too long ({actual:F0}m > {maxAllowed:F0}m)";
            if (HasSelfIntersection(pts, false))
                return "self-intersects";
            if (HasClearanceViolation(pts, _minClearance, false))
                return $"clearance < {_minClearance:F1}m";

            return null;
        }

        // checks if any two adjacent sampled points are suspiciously far apart
        // a spike sends one point hundreds of meters off track — this catches it
        static bool HasSpike(List<Vector2> pts, float maxDist)
        {
            float maxSqr = maxDist * maxDist;
            int   n      = pts.Count;
            for (int i = 0; i < n - 1; i++)
            {
                float dx = pts[i+1].x - pts[i].x;
                float dy = pts[i+1].y - pts[i].y;
                if (dx*dx + dy*dy > maxSqr) return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Constraint computation
        // ─────────────────────────────────────────────────────────────────────

        void ComputeConstraints(TrackDefinition definition)
        {
            _roadWidth       = definition.roadWidth;
            _minTurnRadius   = _roadWidth + 2f;
            _minClearance    = _roadWidth + 2f;

            // designSpeed and elevationVariance removed – using fixed fallback values
            // to keep generation functional
            float fixedDesignSpeed = 80f;        // original default
            float fixedElevationVariance = 3f;   // original default

            _maxGradePercent = fixedDesignSpeed < 60f  ? 10f :
                               fixedDesignSpeed < 100f ?  6f :
                               fixedDesignSpeed < 150f ?  4f : 3f;

            float e     = fixedDesignSpeed < 50f  ? 0.03f :
                          fixedDesignSpeed < 100f ? 0.05f :
                          fixedDesignSpeed < 150f ? 0.07f : 0.09f;
            float ang   = Mathf.Atan(e) * Mathf.Rad2Deg;
            float scale = fixedDesignSpeed < 80f  ? 1.5f :
                          fixedDesignSpeed < 150f ? 2.5f : 4f;
            maxBankAngle = Mathf.Clamp(ang * scale, 2f, 45f);

            // Store elevationVariance fallback for use in AssignElevation/EnforceGrade
            // (these methods will need to reference a field; we'll reuse an existing one or add a private)
            // To avoid breaking existing code, we'll declare a private _elevationVarianceFallback
            _elevationVarianceFallback = fixedElevationVariance;
        }
        private float _elevationVarianceFallback = 3f;

        float ComputeSegLen()
        {
            float minPrimLen = Mathf.PI * _minTurnRadius + _roadWidth * 2f;
            float minSegLen  = minPrimLen * 1.2f;
            float maxSegLen  = trackLength * 0.35f;
            return Mathf.Clamp(trackLength / 6f, minSegLen, maxSegLen);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Parameter change detection
        // ─────────────────────────────────────────────────────────────────────

        bool ParametersChanged(TrackDefinition definition)
        {
            if (definition == null) return false;
            return !Mathf.Approximately(_lastTrackLength, trackLength)
                || !Mathf.Approximately(_lastVariance,    layoutVariance)
                || _lastOpenEnded != openEnded
                || !Mathf.Approximately(_lastRoadWidth,   definition.roadWidth);
        }

        void CacheParameters(TrackDefinition definition)
        {
            _lastTrackLength = trackLength;
            _lastVariance    = layoutVariance;
            _lastOpenEnded   = openEnded;
            _lastRoadWidth   = definition != null ? definition.roadWidth : -1f;
        }

        void ClearPool()
        {
            _goodSeeds.Clear();
            _badSeeds.Clear();
            UpdatePoolDisplay();
        }

        void UpdatePoolDisplay()
        {
            goodSeedCount = _goodSeeds.Count;
            badSeedCount  = _badSeeds.Count;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Primitive point definitions — all geometry from _roadWidth
        // ─────────────────────────────────────────────────────────────────────

        List<Vector2> GetPrimitivePoints(Primitive prim, float segLen)
        {
            float gentleR         = Mathf.Max(segLen * 0.6f,  _minTurnRadius * 4f);
            float mediumR         = Mathf.Max(segLen * 0.35f, _minTurnRadius * 2.5f);
            float tightR          = _minTurnRadius;
            float chicaneOffset   = Mathf.Max(_roadWidth * 1.5f, segLen * 0.2f);
            float hairpinApproach = Mathf.Max(_roadWidth * 2f,   segLen * 0.2f);
            float switchbackGap   = Mathf.Max(_roadWidth * 2f + 2f, segLen * 0.15f);

            return prim switch
            {
                Primitive.Straight          => new List<Vector2>
                                               { new(0,0), new(segLen,0) },
                Primitive.GentleArcLeft     => ArcPoints(gentleR, segLen, +1f),
                Primitive.GentleArcRight    => ArcPoints(gentleR, segLen, -1f),
                Primitive.MediumArcLeft     => ArcPoints(mediumR, segLen, +1f),
                Primitive.MediumArcRight    => ArcPoints(mediumR, segLen, -1f),
                Primitive.HairpinLeft       => HairpinPoints(tightR, hairpinApproach, +1f),
                Primitive.HairpinRight      => HairpinPoints(tightR, hairpinApproach, -1f),
                Primitive.ChicaneLeftRight  => ChicanePoints(chicaneOffset, segLen, +1f),
                Primitive.ChicaneRightLeft  => ChicanePoints(chicaneOffset, segLen, -1f),
                Primitive.Switchback        => SwitchbackPoints(tightR, hairpinApproach,
                                                                switchbackGap),
                Primitive.ClothoidLeft      => ClothoidPoints(gentleR, mediumR, segLen, +1f),
                Primitive.ClothoidRight     => ClothoidPoints(gentleR, mediumR, segLen, -1f),
                _                           => new List<Vector2>
                                               { new(0,0), new(segLen,0) }
            };
        }

        static List<Vector2> ArcPoints(float radius, float segLen, float side)
        {
            float arcAngle = Mathf.Clamp(
                (segLen / (2f * Mathf.PI * radius)) * 360f, 15f, 170f);
            float arcRad   = arcAngle * Mathf.Deg2Rad;
            var   centre   = new Vector2(0f, side * radius);
            var   pts      = new List<Vector2>(4);
            for (int i = 0; i <= 3; i++)
            {
                float t     = (float)i / 3f;
                float angle = -Mathf.PI * 0.5f * side + arcRad * t * side;
                pts.Add(centre + new Vector2(Mathf.Cos(angle) * radius,
                                             Mathf.Sin(angle) * radius));
            }
            return pts;
        }

        static List<Vector2> HairpinPoints(float tightR, float approach, float side)
        {
            return new List<Vector2>
            {
                new(0f,                0f),
                new(approach,          0f),
                new(approach + tightR, side * tightR),
                new(approach,          side * tightR * 2f),
                new(0f,                side * tightR * 2f),
            };
        }

        static List<Vector2> ChicanePoints(float offset, float segLen, float side)
        {
            float l = segLen / 4f;
            return new List<Vector2>
            {
                new(0f,      0f),
                new(l,       side * offset),
                new(l * 2f,  0f),
                new(l * 3f, -side * offset),
                new(l * 4f,  0f),
            };
        }

        static List<Vector2> SwitchbackPoints(float tightR, float approach, float gap)
        {
            return new List<Vector2>
            {
                new(0f,                0f),
                new(approach,          0f),
                new(approach + tightR, tightR),
                new(approach,          tightR * 2f),
                new(0f,                tightR * 2f + gap),
            };
        }

        static List<Vector2> ClothoidPoints(float startR, float endR,
                                              float segLen, float side)
        {
            var   pts   = new List<Vector2>(4);
            float angle = 0f;
            var   pos   = Vector2.zero;
            float step  = segLen / 3f;
            for (int i = 0; i <= 3; i++)
            {
                pts.Add(pos);
                float radius = Mathf.Lerp(startR, endR, (float)i / 3f);
                angle += (step / radius) * side;
                pos   += new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * step;
            }
            return pts;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Arc length per primitive
        // ─────────────────────────────────────────────────────────────────────

        float GetPrimitiveArcLength(Primitive prim, float segLen)
        {
            float gentleR  = Mathf.Max(segLen * 0.6f,  _minTurnRadius * 4f);
            float mediumR  = Mathf.Max(segLen * 0.35f, _minTurnRadius * 2.5f);
            float tightR   = _minTurnRadius;
            float approach = Mathf.Max(_roadWidth * 2f, segLen * 0.2f);

            float gAng = Mathf.Clamp(
                (segLen/(2f*Mathf.PI*gentleR))*Mathf.PI*2f, 0.26f, 2.97f);
            float mAng = Mathf.Clamp(
                (segLen/(2f*Mathf.PI*mediumR))*Mathf.PI*2f, 0.26f, 2.97f);

            return prim switch
            {
                Primitive.Straight          => segLen,
                Primitive.GentleArcLeft     => gentleR * gAng,
                Primitive.GentleArcRight    => gentleR * gAng,
                Primitive.MediumArcLeft     => mediumR * mAng,
                Primitive.MediumArcRight    => mediumR * mAng,
                Primitive.HairpinLeft       => approach * 2f + Mathf.PI * tightR,
                Primitive.HairpinRight      => approach * 2f + Mathf.PI * tightR,
                Primitive.ChicaneLeftRight  => segLen * 1.1f,
                Primitive.ChicaneRightLeft  => segLen * 1.1f,
                Primitive.Switchback        => approach * 4f + 2f*Mathf.PI*tightR,
                Primitive.ClothoidLeft      => segLen * 1.05f,
                Primitive.ClothoidRight     => segLen * 1.05f,
                _                           => segLen
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Shared utilities
        // ─────────────────────────────────────────────────────────────────────

        void CenterPoints(List<Vector2> pts)
        {
            var anchor   = new Vector2(transform.position.x, transform.position.z);
            var centroid = Vector2.zero;
            foreach (var p in pts) centroid += p;
            centroid /= pts.Count;
            var off = anchor - centroid;
            for (int i = 0; i < pts.Count; i++) pts[i] += off;
        }

        void ApplyVariance(List<Vector2> pts, float segLen, Random rng)
        {
            float maxPerturb   = Mathf.Max(0f, (segLen * 0.5f - _minClearance) * 0.5f);
            float perturbScale = layoutVariance * maxPerturb;
            if (perturbScale <= 0f) return;
            for (int i = 0; i < pts.Count; i++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                float mag   = (float)(rng.NextDouble() * perturbScale);
                pts[i] += new Vector2(Mathf.Cos(angle)*mag, Mathf.Sin(angle)*mag);
            }
        }

        static List<Vector2> TransformSnippet(List<Vector2> local,
                                               Vector2 worldPos, Vector2 worldDir)
        {
            Vector2 localDir = (local[1] - local[0]).normalized;
            if (localDir.sqrMagnitude < 0.0001f) localDir = Vector2.right;
            float cosA = worldDir.x*localDir.x + worldDir.y*localDir.y;
            float sinA = worldDir.y*localDir.x - worldDir.x*localDir.y;
            var world = new List<Vector2>(local.Count);
            foreach (var p in local)
            {
                Vector2 c = p - local[0];
                world.Add(new Vector2(c.x*cosA-c.y*sinA,
                                      c.x*sinA+c.y*cosA) + worldPos);
            }
            return world;
        }

        static void WriteSpline(Spline spline, List<Vector3> pts, bool closed)
        {
            foreach (var p in pts)
                spline.Add(new BezierKnot(new float3(p.x, p.y, p.z)),
                           TangentMode.AutoSmooth);
            spline.Closed = closed;
            for (int i = 0; i < spline.Count; i++)
                spline.SetTangentMode(i, TangentMode.AutoSmooth);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation helpers
        // ─────────────────────────────────────────────────────────────────────

        static float MeasureSplineLength(Spline spline, int samples)
        {
            float length = 0f;
            for (int i = 1; i < samples; i++)
            {
                float3 a = spline.EvaluatePosition((float)(i-1)/samples);
                float3 b = spline.EvaluatePosition((float)i    /samples);
                length  += math.length(b-a);
            }
            return length;
        }

        static List<Vector2> SampleSplinePositions(Spline spline, int samples)
        {
            var pts = new List<Vector2>(samples);
            for (int i = 0; i < samples; i++)
            {
                float3 p = spline.EvaluatePosition((float)i/samples);
                pts.Add(new Vector2(p.x, p.z));
            }
            return pts;
        }

        static bool HasSelfIntersection(List<Vector2> pts, bool openEnded)
        {
            int n = pts.Count;
            for (int i = 0; i < n-1; i++)
                for (int j = i+2; j < n; j++)
                {
                    if (!openEnded && i==0 && j==n-1) continue;
                    if (SegmentsIntersect(pts[i], pts[(i+1)%n],
                                          pts[j], pts[(j+1)%n]))
                        return true;
                }
            return false;
        }

        static bool HasClearanceViolation(List<Vector2> pts, float minClearance,
                                           bool openEnded)
        {
            int   n      = pts.Count;
            float minSqr = minClearance * minClearance;
            for (int i = 0; i < n; i++)
            {
                int jEnd = openEnded ? n : (i==0 ? n-1 : n);
                for (int j = i+3; j < jEnd; j++)
                {
                    float dx=pts[j].x-pts[i].x, dy=pts[j].y-pts[i].y;
                    if (dx*dx+dy*dy < minSqr) return true;
                }
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Centripetal Catmull-Rom
        // ─────────────────────────────────────────────────────────────────────

        static List<Vector3> SampleCentripetalClosed(List<Vector2> ctrl, int sps=12)
        {
            int n=ctrl.Count; var result=new List<Vector3>(n*sps);
            for (int i=0;i<n;i++)
                AppendCentripetalSeg(result, ctrl[(i-1+n)%n], ctrl[i],
                                     ctrl[(i+1)%n], ctrl[(i+2)%n], sps);
            return result;
        }

        static List<Vector3> SampleCentripetalOpen(List<Vector2> ctrl, int sps=12)
        {
            int n=ctrl.Count; var result=new List<Vector3>((n-1)*sps+1);
            for (int i=0;i<n-1;i++)
                AppendCentripetalSeg(result,
                    ctrl[Mathf.Max(0,i-1)], ctrl[i],
                    ctrl[i+1], ctrl[Mathf.Min(n-1,i+2)], sps);
            result.Add(new Vector3(ctrl[n-1].x, 0f, ctrl[n-1].y));
            return result;
        }

        static void AppendCentripetalSeg(List<Vector3> result,
                                          Vector2 p0, Vector2 p1,
                                          Vector2 p2, Vector2 p3, int sps)
        {
            float t0=0f;
            float t1=t0+Mathf.Pow(Vector2.Distance(p0,p1),0.5f);
            float t2=t1+Mathf.Pow(Vector2.Distance(p1,p2),0.5f);
            float t3=t2+Mathf.Pow(Vector2.Distance(p2,p3),0.5f);
            if (Mathf.Approximately(t1,t2)) return;
            for (int j=0;j<sps;j++)
            {
                float   t  =Mathf.Lerp(t1,t2,(float)j/sps);
                Vector2 pos=EvalCentripetal(p0,p1,p2,p3,t0,t1,t2,t3,t);
                result.Add(new Vector3(pos.x,0f,pos.y));
            }
        }

        static Vector2 EvalCentripetal(Vector2 p0,Vector2 p1,Vector2 p2,Vector2 p3,
                                        float t0,float t1,float t2,float t3,float t)
        {
            Vector2 A1=t1>t0?Lerp2(p0,p1,t0,t1,t):p1;
            Vector2 A2=t2>t1?Lerp2(p1,p2,t1,t2,t):p2;
            Vector2 A3=t3>t2?Lerp2(p2,p3,t2,t3,t):p3;
            Vector2 B1=t2>t0?Lerp2(A1,A2,t0,t2,t):A2;
            Vector2 B2=t3>t1?Lerp2(A2,A3,t1,t3,t):A3;
            return t2>t1?Lerp2(B1,B2,t1,t2,t):B2;
        }

        static Vector2 Lerp2(Vector2 a,Vector2 b,float t0,float t1,float t)
            =>a*((t1-t)/(t1-t0))+b*((t-t0)/(t1-t0));

        // ─────────────────────────────────────────────────────────────────────
        // Clearance and turn radius enforcement
        // ─────────────────────────────────────────────────────────────────────

        static void EnforceClearance(List<Vector2> pts, float minClearance,
                                      bool openEnded)
        {
            int n=pts.Count; float minSqr=minClearance*minClearance;
            for (int pass=0;pass<8;pass++)
            {
                bool any=false;
                for (int i=0;i<n;i++)
                {
                    int jEnd=openEnded?n:(i==0?n-1:n);
                    for (int j=i+2;j<jEnd;j++)
                    {
                        Vector2 delta=pts[j]-pts[i];
                        float distSqr=delta.sqrMagnitude;
                        if (distSqr<minSqr&&distSqr>0.0001f)
                        {
                            float dist=Mathf.Sqrt(distSqr);
                            float push=(minClearance-dist)*0.5f;
                            Vector2 axis=delta/dist;
                            pts[i]-=axis*push; pts[j]+=axis*push; any=true;
                        }
                    }
                }
                if (!any) break;
            }
        }

        static void EnforceMinTurnRadius(List<Vector3> pts, float minRadius,
                                          bool openEnded)
        {
            float maxK=1f/minRadius; int n=pts.Count;
            for (int pass=0;pass<3;pass++)
                for (int i=0;i<n;i++)
                {
                    int prev=openEnded?Mathf.Max(0,i-1)  :(i-1+n)%n;
                    int next=openEnded?Mathf.Min(n-1,i+1):(i+1)%n;
                    Vector3 p0=pts[prev],p1=pts[i],p2=pts[next];
                    var d1=new Vector2(p2.x-p0.x,p2.z-p0.z);
                    var d2=new Vector2(p2.x-2f*p1.x+p0.x,p2.z-2f*p1.z+p0.z);
                    float d1m=d1.magnitude; if (d1m<0.001f) continue;
                    float curv=Mathf.Abs(d1.x*d2.y-d1.y*d2.x)/(d1m*d1m*d1m);
                    if (curv>maxK)
                    {
                        float c=(curv-maxK)/maxK*minRadius*0.5f;
                        Vector2 cp=d2.normalized;
                        pts[i]=new Vector3(p1.x-cp.x*c,p1.y,p1.z-cp.y*c);
                    }
                }
        }

        static void UncrossSamples(List<Vector3> pts)
        {
            bool changed=true; int passes=10;
            while (changed&&passes-->0)
            {
                changed=false; int n=pts.Count;
                for (int i=0;i<n-1&&!changed;i++)
                    for (int j=i+2;j<n&&!changed;j++)
                    {
                        if (i==0&&j==n-1) continue;
                        var a1=new Vector2(pts[i].x,      pts[i].z);
                        var a2=new Vector2(pts[(i+1)%n].x,pts[(i+1)%n].z);
                        var b1=new Vector2(pts[j].x,      pts[j].z);
                        var b2=new Vector2(pts[(j+1)%n].x,pts[(j+1)%n].z);
                        if (!SegmentsIntersect(a1,a2,b1,b2)) continue;
                        int lo=i+1,hi=j;
                        while(lo<hi){(pts[lo],pts[hi])=(pts[hi],pts[lo]);lo++;hi--;}
                        changed=true;
                    }
            }
        }

        static bool SegmentsIntersect(Vector2 p1,Vector2 p2,Vector2 p3,Vector2 p4)
        {
            float d1=Cross2D(p3,p4,p1),d2=Cross2D(p3,p4,p2);
            float d3=Cross2D(p1,p2,p3),d4=Cross2D(p1,p2,p4);
            return ((d1>0f&&d2<0f)||(d1<0f&&d2>0f))&&
                   ((d3>0f&&d4<0f)||(d3<0f&&d4>0f));
        }

        static float Cross2D(Vector2 a,Vector2 b,Vector2 p)
            =>(b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);

        // ─────────────────────────────────────────────────────────────────────
        // Elevation
        // ─────────────────────────────────────────────────────────────────────

        void AssignElevation(List<Vector3> pts, Random rng)
        {
            float elevationVariance = _elevationVarianceFallback;
            if (elevationVariance <= 0f) return;
            int n=pts.Count;
            float f1=(float)(rng.NextDouble()*2.0+1.0);
            float f2=(float)(rng.NextDouble()*3.0+2.0);
            float o1=(float)(rng.NextDouble()*Mathf.PI*2f);
            float o2=(float)(rng.NextDouble()*Mathf.PI*2f);
            for (int i=0;i<n;i++)
            {
                float t=(float)i/n;
                float y=Mathf.Sin(t*Mathf.PI*2f*f1+o1)*elevationVariance*0.6f
                       +Mathf.Sin(t*Mathf.PI*2f*f2+o2)*elevationVariance*0.4f;
                var p=pts[i]; pts[i]=new Vector3(p.x,y,p.z);
            }
        }

        void EnforceGrade(List<Vector3> pts, bool openEnded)
        {
            float elevationVariance = _elevationVarianceFallback;
            if (elevationVariance <= 0f) return;
            float maxGrade=_maxGradePercent/100f; int n=pts.Count;
            for (int pass=0;pass<2;pass++)
                for (int i=0;i<n;i++)
                {
                    int next=openEnded?Mathf.Min(n-1,i+1):(i+1)%n;
                    if (next==i) continue;
                    Vector3 a=pts[i],b=pts[next];
                    float horiz=new Vector2(b.x-a.x,b.z-a.z).magnitude;
                    if (horiz<0.001f) continue;
                    float grade=Mathf.Abs(b.y-a.y)/horiz;
                    if (grade>maxGrade)
                    {
                        float sign=b.y>a.y?1f:-1f;
                        pts[next]=new Vector3(b.x,a.y+sign*horiz*maxGrade,b.z);
                    }
                }
        }

        static List<Vector3> Downsample(List<Vector3> pts, int target)
        {
            if (pts.Count<=target) return pts;
            var result=new List<Vector3>(target);
            float step=(float)pts.Count/target;
            for (int i=0;i<target;i++)
                result.Add(pts[Mathf.RoundToInt(i*step)%pts.Count]);
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Banking curve
        // ─────────────────────────────────────────────────────────────────────

        public AnimationCurve DeriveBankingCurve(Spline spline, int sampleCount=64)
        {
            var keys=new Keyframe[sampleCount];
            for (int i=0;i<sampleCount;i++)
            {
                float  t    =(float)i/sampleCount;
                float  tPrev=(t-0.01f+1f)%1f;
                float  tNext=(t+0.01f)%1f;
                float3 tgP  =math.normalize(spline.EvaluateTangent(tPrev));
                float3 tgN  =math.normalize(spline.EvaluateTangent(tNext));
                float  curv =math.clamp(math.length(tgN-tgP)/0.02f,0f,1f);
                float3 tg   =math.normalize(spline.EvaluateTangent(t));
                float3 cross=math.cross(tg,tgN-tgP);
                float  sign =cross.y>=0f?1f:-1f;
                keys[i]     =new Keyframe(t,sign*curv);
            }
            return new AnimationCurve(keys);
        }

        static int NextSeed(int current)
            =>System.Environment.TickCount^(current*1664525+1013904223);
    }
}