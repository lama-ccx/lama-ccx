using System;
using System.Collections.Generic;
using System.Linq;
using Llama.Core.Model;
using Llama.Core.Model.Elements;

namespace Llama.Core.PostProcessing
{
    /// <summary>
    /// Filters elements by stress range and rebuilds the visible boundary,
    /// effectively "peeling away" elements outside the specified range.
    /// </summary>
    public static class StressRangeFilter
    {
        /// <summary>
        /// Returns boundary faces only for elements whose average stress falls within [minStress, maxStress].
        /// Internal faces that become exposed by filtering are included.
        /// </summary>
        public static List<BoundaryFace> Filter(
            StructuralModel model,
            IReadOnlyDictionary<int, double> elementStress,
            double minStress,
            double maxStress)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (elementStress == null) throw new ArgumentNullException(nameof(elementStress));

            // Determine which elements are visible (within range).
            var visibleElements = new HashSet<int>();
            foreach (var element in model.Elements)
            {
                if (elementStress.TryGetValue(element.Id, out var s) && s >= minStress && s <= maxStress)
                    visibleElements.Add(element.Id);
            }

            // Count face occurrences among visible elements only.
            var faceCounts = new Dictionary<FaceKey, (BoundaryFace Face, int Count)>();

            foreach (var element in model.Elements)
            {
                if (!visibleElements.Contains(element.Id))
                    continue;

                var faceCorners = GetFaceCornerTable(element.ElementType);
                if (faceCorners == null)
                    continue;

                foreach (var cornerIndices in faceCorners)
                {
                    var nodeIds = new int[cornerIndices.Length];
                    for (var i = 0; i < cornerIndices.Length; i++)
                        nodeIds[i] = element.NodeIds[cornerIndices[i]];

                    var key = FaceKey.Create(nodeIds);
                    if (faceCounts.TryGetValue(key, out var existing))
                        faceCounts[key] = (existing.Face, existing.Count + 1);
                    else
                        faceCounts[key] = (new BoundaryFace(nodeIds, element.Id), 1);
                }
            }

            return faceCounts.Values
                .Where(kv => kv.Count == 1)
                .Select(kv => kv.Face)
                .ToList();
        }

        /// <summary>
        /// Compute per-element average stress from integration-point results.
        /// </summary>
        public static Dictionary<int, double> ComputePerElementStress(
            IReadOnlyList<ElementStressResult> stresses,
            StressNodeAverager.StressComponent component)
        {
            var groups = new Dictionary<int, (double Sum, int Count)>();

            foreach (var stress in stresses)
            {
                var value = ExtractComponent(stress.Components, component);
                if (double.IsNaN(value)) continue;

                if (groups.TryGetValue(stress.ElementId, out var acc))
                    groups[stress.ElementId] = (acc.Sum + value, acc.Count + 1);
                else
                    groups[stress.ElementId] = (value, 1);
            }

            var result = new Dictionary<int, double>(groups.Count);
            foreach (var kvp in groups)
                result[kvp.Key] = kvp.Value.Sum / kvp.Value.Count;
            return result;
        }

        private static double ExtractComponent(IReadOnlyList<double> components, StressNodeAverager.StressComponent component)
        {
            var offset = components.Count >= 7 ? 1 : 0;

            if (component == StressNodeAverager.StressComponent.SvM)
            {
                if (components.Count < offset + 6) return double.NaN;
                var sxx = components[offset];
                var syy = components[offset + 1];
                var szz = components[offset + 2];
                var sxy = components[offset + 3];
                var sxz = components[offset + 4];
                var syz = components[offset + 5];
                var normal = 0.5 * ((sxx - syy) * (sxx - syy) + (syy - szz) * (syy - szz) + (szz - sxx) * (szz - sxx));
                var shear = 3.0 * (sxy * sxy + sxz * sxz + syz * syz);
                return Math.Sqrt(Math.Max(0.0, normal + shear));
            }

            var idx = (int)component;
            if (idx + offset >= components.Count) return double.NaN;
            return components[offset + idx];
        }

        // Face corner tables (same as DeformedMeshBuilder).
        private static readonly int[][] Tetra4FaceCorners =
        {
            new[] { 0, 1, 2 },
            new[] { 0, 3, 1 },
            new[] { 1, 3, 2 },
            new[] { 0, 2, 3 }
        };

        private static readonly int[][] Hexa8FaceCorners =
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
                case CalculixElementType.C3D10:
                    return Tetra4FaceCorners;
                case CalculixElementType.C3D20R:
                    return Hexa8FaceCorners;
                default:
                    return null;
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

            public bool Equals(FaceKey other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;
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
