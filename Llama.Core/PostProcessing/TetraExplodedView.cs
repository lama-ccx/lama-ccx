using System;
using System.Collections.Generic;
using Llama.Core.Model;
using Llama.Core.Model.Elements;

namespace Llama.Core.PostProcessing
{
    /// <summary>
    /// Shrinks each tetrahedral element toward its centroid, creating gaps
    /// between elements to reveal interior stress coloring.
    /// </summary>
    public static class TetraExplodedView
    {
        /// <summary>
        /// A single shrunk tetrahedron represented by its 4 faces (triangles).
        /// </summary>
        public sealed class ExplodedTetra
        {
            /// <summary>Corner positions after shrinking (4 vertices).</summary>
            public (double X, double Y, double Z)[] Vertices { get; }

            /// <summary>Stress value per corner (4 values, same order as Vertices).</summary>
            public double[] StressValues { get; }

            public int ElementId { get; }

            public ExplodedTetra((double, double, double)[] vertices, double[] stressValues, int elementId)
            {
                Vertices = vertices;
                StressValues = stressValues;
                ElementId = elementId;
            }
        }

        /// <summary>
        /// Shrink each tetrahedron toward its centroid by the given factor.
        /// </summary>
        /// <param name="model">Structural model.</param>
        /// <param name="shrinkFactor">0.0 = collapsed to centroid, 1.0 = no shrink. Typical: 0.7–0.9.</param>
        /// <param name="nodeStress">Per-node stress values.</param>
        /// <param name="deformedPositions">Optional deformed positions.</param>
        /// <returns>List of exploded tetrahedra.</returns>
        public static List<ExplodedTetra> Build(
            StructuralModel model,
            double shrinkFactor,
            IReadOnlyDictionary<int, double> nodeStress,
            IReadOnlyDictionary<int, (double X, double Y, double Z)> deformedPositions = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            shrinkFactor = Math.Max(0.0, Math.Min(1.0, shrinkFactor));

            var positions = new Dictionary<int, (double X, double Y, double Z)>(model.Nodes.Count);
            foreach (var n in model.Nodes)
                positions[n.Id] = (n.X, n.Y, n.Z);

            if (deformedPositions != null)
            {
                foreach (var kvp in deformedPositions)
                    positions[kvp.Key] = kvp.Value;
            }

            var result = new List<ExplodedTetra>();

            foreach (var element in model.Elements)
            {
                var cornerCount = GetCornerNodeCount(element.ElementType);
                if (cornerCount != 4 || element.NodeIds.Count < 4)
                    continue;

                var pos = new (double X, double Y, double Z)[4];
                var stress = new double[4];
                var valid = true;

                for (var i = 0; i < 4; i++)
                {
                    var nid = element.NodeIds[i];
                    if (!positions.TryGetValue(nid, out pos[i]))
                    {
                        valid = false;
                        break;
                    }

                    if (nodeStress != null && nodeStress.TryGetValue(nid, out var s))
                        stress[i] = s;
                }

                if (!valid) continue;

                // Compute centroid.
                var cx = (pos[0].X + pos[1].X + pos[2].X + pos[3].X) * 0.25;
                var cy = (pos[0].Y + pos[1].Y + pos[2].Y + pos[3].Y) * 0.25;
                var cz = (pos[0].Z + pos[1].Z + pos[2].Z + pos[3].Z) * 0.25;

                // Shrink each vertex toward centroid.
                var shrunk = new (double X, double Y, double Z)[4];
                for (var i = 0; i < 4; i++)
                {
                    shrunk[i] = (
                        cx + shrinkFactor * (pos[i].X - cx),
                        cy + shrinkFactor * (pos[i].Y - cy),
                        cz + shrinkFactor * (pos[i].Z - cz));
                }

                result.Add(new ExplodedTetra(shrunk, stress, element.Id));
            }

            return result;
        }

        private static int GetCornerNodeCount(CalculixElementType type)
        {
            switch (type)
            {
                case CalculixElementType.C3D4:
                case CalculixElementType.C3D10:
                    return 4;
                default:
                    return 0;
            }
        }
    }
}
