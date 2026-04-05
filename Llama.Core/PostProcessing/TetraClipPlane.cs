using System;
using System.Collections.Generic;
using System.Linq;
using Llama.Core.Model;
using Llama.Core.Model.Elements;

namespace Llama.Core.PostProcessing
{
    /// <summary>
    /// Cuts tetrahedral elements with a plane and produces triangular cross-section faces
    /// with interpolated per-node stress values.
    /// </summary>
    public static class TetraClipPlane
    {
        /// <summary>
        /// A triangular face produced by clipping a tetrahedron with a plane.
        /// Vertices are interpolated positions; stress values are interpolated from corner nodes.
        /// </summary>
        public sealed class ClipFace
        {
            public (double X, double Y, double Z)[] Vertices { get; }
            public double[] StressValues { get; }
            public int ElementId { get; }

            public ClipFace((double, double, double)[] vertices, double[] stressValues, int elementId)
            {
                Vertices = vertices;
                StressValues = stressValues;
                ElementId = elementId;
            }
        }

        /// <summary>
        /// Clip all tetrahedral elements with the given plane.
        /// </summary>
        /// <param name="model">Structural model.</param>
        /// <param name="planeOrigin">A point on the clipping plane.</param>
        /// <param name="planeNormal">Normal of the clipping plane (unit vector).</param>
        /// <param name="nodeStress">Per-node stress values (from StressNodeAverager).</param>
        /// <param name="deformedPositions">Optional deformed positions; falls back to original node coords.</param>
        /// <returns>List of triangular clip faces.</returns>
        public static List<ClipFace> Clip(
            StructuralModel model,
            (double X, double Y, double Z) planeOrigin,
            (double X, double Y, double Z) planeNormal,
            IReadOnlyDictionary<int, double> nodeStress,
            IReadOnlyDictionary<int, (double X, double Y, double Z)> deformedPositions = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            // Build node position lookup.
            var positions = new Dictionary<int, (double X, double Y, double Z)>(model.Nodes.Count);
            foreach (var n in model.Nodes)
                positions[n.Id] = (n.X, n.Y, n.Z);

            if (deformedPositions != null)
            {
                foreach (var kvp in deformedPositions)
                    positions[kvp.Key] = kvp.Value;
            }

            var result = new List<ClipFace>();

            foreach (var element in model.Elements)
            {
                var cornerCount = GetCornerNodeCount(element.ElementType);
                if (cornerCount != 4) continue; // Only tetrahedra

                if (element.NodeIds.Count < 4) continue;

                var cornerIds = new int[4];
                for (var i = 0; i < 4; i++)
                    cornerIds[i] = element.NodeIds[i];

                // Get positions and signed distances for corners.
                var pos = new (double X, double Y, double Z)[4];
                var dist = new double[4];
                var stress = new double[4];

                for (var i = 0; i < 4; i++)
                {
                    if (!positions.TryGetValue(cornerIds[i], out pos[i]))
                        goto NextElement;

                    var dx = pos[i].X - planeOrigin.X;
                    var dy = pos[i].Y - planeOrigin.Y;
                    var dz = pos[i].Z - planeOrigin.Z;
                    dist[i] = dx * planeNormal.X + dy * planeNormal.Y + dz * planeNormal.Z;

                    if (nodeStress != null && nodeStress.TryGetValue(cornerIds[i], out var s))
                        stress[i] = s;
                }

                // Classify nodes as positive/negative side of plane.
                var positive = new List<int>();
                var negative = new List<int>();
                for (var i = 0; i < 4; i++)
                {
                    if (dist[i] >= 0)
                        positive.Add(i);
                    else
                        negative.Add(i);
                }

                // No intersection if all on one side.
                if (positive.Count == 0 || negative.Count == 0)
                    continue;

                // Compute intersection points on edges that cross the plane.
                var clipVerts = new List<(double X, double Y, double Z)>();
                var clipStress = new List<double>();

                if (positive.Count == 1 || negative.Count == 1)
                {
                    // One node on one side, three on the other → triangle cross-section.
                    var singleSide = positive.Count == 1 ? positive : negative;
                    var multiSide = positive.Count == 1 ? negative : positive;
                    var si = singleSide[0];

                    for (var j = 0; j < 3; j++)
                    {
                        var mi = multiSide[j];
                        var t = dist[si] / (dist[si] - dist[mi]);
                        clipVerts.Add(Lerp(pos[si], pos[mi], t));
                        clipStress.Add(stress[si] + t * (stress[mi] - stress[si]));
                    }

                    result.Add(new ClipFace(clipVerts.ToArray(), clipStress.ToArray(), element.Id));
                }
                else
                {
                    // Two nodes on each side → quadrilateral cross-section → split into 2 triangles.
                    var edgeVerts = new List<(double X, double Y, double Z)>();
                    var edgeStress = new List<double>();

                    foreach (var pi in positive)
                    {
                        foreach (var ni in negative)
                        {
                            var t = dist[pi] / (dist[pi] - dist[ni]);
                            edgeVerts.Add(Lerp(pos[pi], pos[ni], t));
                            edgeStress.Add(stress[pi] + t * (stress[ni] - stress[pi]));
                        }
                    }

                    if (edgeVerts.Count == 4)
                    {
                        // Order the quad properly: we have edges p0-n0, p0-n1, p1-n0, p1-n1
                        // Rearrange to form a proper quad: p0-n0, p0-n1, p1-n1, p1-n0
                        // But since our iteration is p0-n0(0), p0-n1(1), p1-n0(2), p1-n1(3),
                        // the correct order is 0, 1, 3, 2.
                        result.Add(new ClipFace(
                            new[] { edgeVerts[0], edgeVerts[1], edgeVerts[3] },
                            new[] { edgeStress[0], edgeStress[1], edgeStress[3] },
                            element.Id));
                        result.Add(new ClipFace(
                            new[] { edgeVerts[0], edgeVerts[3], edgeVerts[2] },
                            new[] { edgeStress[0], edgeStress[3], edgeStress[2] },
                            element.Id));
                    }
                }

                NextElement:;
            }

            return result;
        }

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
                case CalculixElementType.C3D10:
                    return 4;
                case CalculixElementType.C3D20R:
                    return 8;
                default:
                    return 0;
            }
        }
    }
}
