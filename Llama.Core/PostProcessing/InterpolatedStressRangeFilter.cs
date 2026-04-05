using System;
using System.Collections.Generic;
using System.Linq;
using Llama.Core.Model;
using Llama.Core.Model.Elements;

namespace Llama.Core.PostProcessing
{
    /// <summary>
    /// Filters a tetrahedral model by stress range with smooth interpolation.
    /// Instead of removing entire elements, tetrahedra straddling thresholds are
    /// clipped: boundary faces are trimmed and iso-surface cap faces are added
    /// at the min/max stress boundaries.
    /// </summary>
    public static class InterpolatedStressRangeFilter
    {
        /// <summary>
        /// A triangular face with interpolated vertex positions and stress values.
        /// </summary>
        public sealed class InterpolatedFace
        {
            public (double X, double Y, double Z)[] Vertices { get; }
            public double[] StressValues { get; }
            public int ElementId { get; }

            public InterpolatedFace(
                (double, double, double)[] vertices,
                double[] stressValues,
                int elementId)
            {
                Vertices = vertices;
                StressValues = stressValues;
                ElementId = elementId;
            }
        }

        private enum ElemClass { FullyIn, FullyOut, Partial }

        /// <summary>
        /// Filter elements by interpolated stress, clipping partial elements at the
        /// threshold boundaries. Returns triangular faces with interpolated positions
        /// and stress values.
        /// </summary>
        public static List<InterpolatedFace> Filter(
            StructuralModel model,
            IReadOnlyDictionary<int, double> nodeStress,
            double minStress,
            double maxStress,
            IReadOnlyDictionary<int, (double X, double Y, double Z)> positions)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nodeStress == null) throw new ArgumentNullException(nameof(nodeStress));
            if (positions == null) throw new ArgumentNullException(nameof(positions));

            var result = new List<InterpolatedFace>();

            // 1. Classify each element.
            var elemClass = ClassifyElements(model, nodeStress, minStress, maxStress);

            // 2. Build face map: face -> list of (elementId, nodeIds).
            var faceMap = BuildFaceMap(model);

            // 3. Process faces (boundary + newly-exposed internal).
            foreach (var entries in faceMap.Values)
            {
                if (entries.Count == 1)
                {
                    // Boundary face.
                    var (elemId, nodeIds) = entries[0];
                    if (!elemClass.TryGetValue(elemId, out var cls) || cls == ElemClass.FullyOut)
                        continue;

                    EmitFace(result, elemId, nodeIds, nodeStress, positions,
                        minStress, maxStress, cls == ElemClass.Partial);
                }
                else if (entries.Count == 2)
                {
                    // Internal face.
                    var (idA, nidsA) = entries[0];
                    var (idB, nidsB) = entries[1];
                    elemClass.TryGetValue(idA, out var clsA);
                    elemClass.TryGetValue(idB, out var clsB);

                    // Both have in-range material -> stays internal.
                    if (clsA != ElemClass.FullyOut && clsB != ElemClass.FullyOut)
                        continue;

                    // Both fully out -> not visible.
                    if (clsA == ElemClass.FullyOut && clsB == ElemClass.FullyOut)
                        continue;

                    // One side out, the other in or partial -> exposed.
                    var inId = clsA != ElemClass.FullyOut ? idA : idB;
                    var inNodeIds = clsA != ElemClass.FullyOut ? nidsA : nidsB;
                    var inCls = clsA != ElemClass.FullyOut ? clsA : clsB;

                    EmitFace(result, inId, inNodeIds, nodeStress, positions,
                        minStress, maxStress, inCls == ElemClass.Partial);
                }
            }

            // 4. Cap faces at thresholds (iso-surfaces within partial elements).
            EmitCapFaces(result, model, elemClass, nodeStress, positions, minStress, maxStress);

            return result;
        }

        // -------------------------------------------------------------------
        // Element classification
        // -------------------------------------------------------------------

        private static Dictionary<int, ElemClass> ClassifyElements(
            StructuralModel model,
            IReadOnlyDictionary<int, double> nodeStress,
            double minStress,
            double maxStress)
        {
            var classes = new Dictionary<int, ElemClass>();

            foreach (var elem in model.Elements)
            {
                var cc = GetCornerNodeCount(elem.ElementType);
                if (cc == 0) { classes[elem.Id] = ElemClass.FullyOut; continue; }

                bool allBelowMin = true, allAboveMax = true, allInRange = true;
                var count = Math.Min(cc, elem.NodeIds.Count);

                for (var i = 0; i < count; i++)
                {
                    nodeStress.TryGetValue(elem.NodeIds[i], out var s);
                    if (s >= minStress) allBelowMin = false;
                    if (s <= maxStress) allAboveMax = false;
                    if (s < minStress || s > maxStress) allInRange = false;
                }

                if (allBelowMin || allAboveMax)
                    classes[elem.Id] = ElemClass.FullyOut;
                else if (allInRange)
                    classes[elem.Id] = ElemClass.FullyIn;
                else
                    classes[elem.Id] = ElemClass.Partial;
            }

            return classes;
        }

        // -------------------------------------------------------------------
        // Face map
        // -------------------------------------------------------------------

        private static Dictionary<FaceKey, List<(int ElemId, int[] NodeIds)>> BuildFaceMap(
            StructuralModel model)
        {
            var map = new Dictionary<FaceKey, List<(int, int[])>>();

            foreach (var elem in model.Elements)
            {
                var faceCorners = GetFaceCornerTable(elem.ElementType);
                if (faceCorners == null) continue;

                foreach (var corners in faceCorners)
                {
                    var nodeIds = new int[corners.Length];
                    for (var i = 0; i < corners.Length; i++)
                        nodeIds[i] = elem.NodeIds[corners[i]];

                    var key = FaceKey.Create(nodeIds);
                    if (!map.TryGetValue(key, out var list))
                    {
                        list = new List<(int, int[])>();
                        map[key] = list;
                    }
                    list.Add((elem.Id, nodeIds));
                }
            }

            return map;
        }

        // -------------------------------------------------------------------
        // Face emission (with optional clipping)
        // -------------------------------------------------------------------

        private static void EmitFace(
            List<InterpolatedFace> result,
            int elementId,
            int[] nodeIds,
            IReadOnlyDictionary<int, double> nodeStress,
            IReadOnlyDictionary<int, (double X, double Y, double Z)> positions,
            double minStress,
            double maxStress,
            bool needsClipping)
        {
            var n = nodeIds.Length;
            var verts = new (double X, double Y, double Z)[n];
            var vals = new double[n];

            for (var i = 0; i < n; i++)
            {
                positions.TryGetValue(nodeIds[i], out verts[i]);
                nodeStress.TryGetValue(nodeIds[i], out vals[i]);
            }

            // Split into triangles (quads -> 2 tris).
            var tris = new List<((double X, double Y, double Z)[] V, double[] S)>();
            if (n == 3)
            {
                tris.Add((verts, vals));
            }
            else if (n == 4)
            {
                tris.Add((
                    new[] { verts[0], verts[1], verts[2] },
                    new[] { vals[0], vals[1], vals[2] }));
                tris.Add((
                    new[] { verts[0], verts[2], verts[3] },
                    new[] { vals[0], vals[2], vals[3] }));
            }

            if (!needsClipping)
            {
                foreach (var (v, s) in tris)
                    result.Add(new InterpolatedFace(v, s, elementId));
                return;
            }

            // Clip against minStress (keep >= min), then maxStress (keep <= max).
            foreach (var (v, s) in tris)
            {
                var afterMin = ClipTriangle(v, s, minStress, keepAbove: true);
                foreach (var (cv, cs) in afterMin)
                {
                    var afterMax = ClipTriangle(cv, cs, maxStress, keepAbove: false);
                    foreach (var (fv, fs) in afterMax)
                        result.Add(new InterpolatedFace(fv, fs, elementId));
                }
            }
        }

        // -------------------------------------------------------------------
        // Cap faces at min/max thresholds
        // -------------------------------------------------------------------

        private static void EmitCapFaces(
            List<InterpolatedFace> result,
            StructuralModel model,
            Dictionary<int, ElemClass> elemClass,
            IReadOnlyDictionary<int, double> nodeStress,
            IReadOnlyDictionary<int, (double X, double Y, double Z)> positions,
            double minStress,
            double maxStress)
        {
            var edges = new[] { (0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3) };

            foreach (var elem in model.Elements)
            {
                if (!elemClass.TryGetValue(elem.Id, out var cls) || cls != ElemClass.Partial)
                    continue;

                var cc = GetCornerNodeCount(elem.ElementType);
                if (cc != 4 || elem.NodeIds.Count < 4) continue;

                var pos = new (double X, double Y, double Z)[4];
                var stress = new double[4];
                var valid = true;

                for (var i = 0; i < 4; i++)
                {
                    if (!positions.TryGetValue(elem.NodeIds[i], out pos[i]) ||
                        !nodeStress.TryGetValue(elem.NodeIds[i], out stress[i]))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid) continue;

                bool hasBelow = false, hasAbove = false;
                for (var i = 0; i < 4; i++)
                {
                    if (stress[i] < minStress) hasBelow = true;
                    if (stress[i] > maxStress) hasAbove = true;
                }

                if (hasBelow)
                    EmitIso(result, edges, pos, stress, minStress, elem.Id);
                if (hasAbove)
                    EmitIso(result, edges, pos, stress, maxStress, elem.Id);
            }
        }

        private static void EmitIso(
            List<InterpolatedFace> result,
            (int, int)[] edges,
            (double X, double Y, double Z)[] pos,
            double[] stress,
            double isoValue,
            int elementId)
        {
            var pts = new List<(double X, double Y, double Z)>();

            foreach (var (i0, i1) in edges)
            {
                var v0 = stress[i0] - isoValue;
                var v1 = stress[i1] - isoValue;

                if ((v0 > 0 && v1 < 0) || (v0 < 0 && v1 > 0))
                {
                    var t = v0 / (v0 - v1);
                    pts.Add(Lerp(pos[i0], pos[i1], t));
                }
            }

            if (pts.Count == 3)
            {
                result.Add(new InterpolatedFace(
                    pts.ToArray(),
                    new[] { isoValue, isoValue, isoValue },
                    elementId));
            }
            else if (pts.Count == 4)
            {
                // Same winding fix as TetraIsoSurface: [0,1,3] + [0,3,2].
                result.Add(new InterpolatedFace(
                    new[] { pts[0], pts[1], pts[3] },
                    new[] { isoValue, isoValue, isoValue },
                    elementId));
                result.Add(new InterpolatedFace(
                    new[] { pts[0], pts[3], pts[2] },
                    new[] { isoValue, isoValue, isoValue },
                    elementId));
            }
        }

        // -------------------------------------------------------------------
        // Triangle clipping against a scalar threshold
        // -------------------------------------------------------------------

        private static List<((double X, double Y, double Z)[] V, double[] S)> ClipTriangle(
            (double X, double Y, double Z)[] verts,
            double[] vals,
            double threshold,
            bool keepAbove)
        {
            var result = new List<((double X, double Y, double Z)[], double[])>();

            var inList = new List<int>(3);
            var outList = new List<int>(3);

            for (var i = 0; i < 3; i++)
            {
                var isIn = keepAbove ? vals[i] >= threshold : vals[i] <= threshold;
                if (isIn) inList.Add(i);
                else outList.Add(i);
            }

            if (inList.Count == 3)
            {
                result.Add((
                    new[] { verts[0], verts[1], verts[2] },
                    new[] { vals[0], vals[1], vals[2] }));
            }
            else if (inList.Count == 1)
            {
                // One vertex in -> small triangle.
                var a = inList[0];
                var b = outList[0];
                var c = outList[1];

                var dAB = vals[b] - vals[a];
                var dAC = vals[c] - vals[a];
                if (Math.Abs(dAB) < 1e-30 || Math.Abs(dAC) < 1e-30) return result;

                var tAB = (threshold - vals[a]) / dAB;
                var tAC = (threshold - vals[a]) / dAC;

                result.Add((
                    new[] { verts[a], Lerp(verts[a], verts[b], tAB), Lerp(verts[a], verts[c], tAC) },
                    new[] { vals[a], threshold, threshold }));
            }
            else if (inList.Count == 2)
            {
                // Two vertices in -> trapezoid -> 2 triangles.
                var a = inList[0];
                var b = inList[1];
                var c = outList[0];

                var dAC = vals[c] - vals[a];
                var dBC = vals[c] - vals[b];
                if (Math.Abs(dAC) < 1e-30 || Math.Abs(dBC) < 1e-30) return result;

                var tAC = (threshold - vals[a]) / dAC;
                var tBC = (threshold - vals[b]) / dBC;

                var pAC = Lerp(verts[a], verts[c], tAC);
                var pBC = Lerp(verts[b], verts[c], tBC);

                result.Add((
                    new[] { verts[a], verts[b], pBC },
                    new[] { vals[a], vals[b], threshold }));
                result.Add((
                    new[] { verts[a], pBC, pAC },
                    new[] { vals[a], threshold, threshold }));
            }

            return result;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static (double X, double Y, double Z) Lerp(
            (double X, double Y, double Z) a,
            (double X, double Y, double Z) b,
            double t)
        {
            return (
                a.X + t * (b.X - a.X),
                a.Y + t * (b.Y - a.Y),
                a.Z + t * (b.Z - a.Z));
        }

        private static int GetCornerNodeCount(CalculixElementType type)
        {
            switch (type)
            {
                case CalculixElementType.C3D4:
                case CalculixElementType.C3D10: return 4;
                case CalculixElementType.C3D20R: return 8;
                default: return 0;
            }
        }

        private static readonly int[][] TetraFaceCorners =
        {
            new[] { 0, 1, 2 },
            new[] { 0, 3, 1 },
            new[] { 1, 3, 2 },
            new[] { 0, 2, 3 }
        };

        private static readonly int[][] HexFaceCorners =
        {
            new[] { 0, 1, 2, 3 },
            new[] { 4, 7, 6, 5 },
            new[] { 0, 4, 5, 1 },
            new[] { 2, 6, 7, 3 },
            new[] { 0, 3, 7, 4 },
            new[] { 1, 5, 6, 2 }
        };

        private static int[][] GetFaceCornerTable(CalculixElementType type)
        {
            switch (type)
            {
                case CalculixElementType.C3D4:
                case CalculixElementType.C3D10: return TetraFaceCorners;
                case CalculixElementType.C3D20R: return HexFaceCorners;
                default: return null;
            }
        }

        private readonly struct FaceKey : IEquatable<FaceKey>
        {
            private readonly int _a, _b, _c, _d;

            private FaceKey(int a, int b, int c, int d) { _a = a; _b = b; _c = c; _d = d; }

            public static FaceKey Create(int[] nodeIds)
            {
                var sorted = (int[])nodeIds.Clone();
                Array.Sort(sorted);
                return sorted.Length >= 4
                    ? new FaceKey(sorted[0], sorted[1], sorted[2], sorted[3])
                    : new FaceKey(sorted[0], sorted[1], sorted[2], 0);
            }

            public bool Equals(FaceKey o) => _a == o._a && _b == o._b && _c == o._c && _d == o._d;
            public override bool Equals(object obj) => obj is FaceKey o && Equals(o);
            public override int GetHashCode()
            {
                unchecked
                {
                    var h = _a;
                    h = (h * 397) ^ _b;
                    h = (h * 397) ^ _c;
                    h = (h * 397) ^ _d;
                    return h;
                }
            }
        }
    }
}
