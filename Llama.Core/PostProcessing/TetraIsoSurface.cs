using System;
using System.Collections.Generic;
using Llama.Core.Model;
using Llama.Core.Model.Elements;

namespace Llama.Core.PostProcessing
{
    /// <summary>
    /// Extracts iso-surfaces from tetrahedral meshes using the Marching Tetrahedra algorithm.
    /// An iso-surface is the locus of points where a scalar field equals a given value.
    /// </summary>
    public static class TetraIsoSurface
    {
        /// <summary>
        /// A triangular face on the iso-surface.
        /// </summary>
        public sealed class IsoFace
        {
            public (double X, double Y, double Z)[] Vertices { get; }
            public int ElementId { get; }

            public IsoFace((double, double, double)[] vertices, int elementId)
            {
                Vertices = vertices;
                ElementId = elementId;
            }
        }

        /// <summary>
        /// Extract the iso-surface at the given threshold value from all tetrahedral elements.
        /// </summary>
        /// <param name="model">Structural model.</param>
        /// <param name="nodeValues">Per-node scalar field (e.g., stress).</param>
        /// <param name="isoValue">The threshold value to extract.</param>
        /// <param name="deformedPositions">Optional deformed positions.</param>
        /// <returns>List of triangular iso-surface faces.</returns>
        public static List<IsoFace> Extract(
            StructuralModel model,
            IReadOnlyDictionary<int, double> nodeValues,
            double isoValue,
            IReadOnlyDictionary<int, (double X, double Y, double Z)> deformedPositions = null)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (nodeValues == null) throw new ArgumentNullException(nameof(nodeValues));

            var positions = new Dictionary<int, (double X, double Y, double Z)>(model.Nodes.Count);
            foreach (var n in model.Nodes)
                positions[n.Id] = (n.X, n.Y, n.Z);

            if (deformedPositions != null)
            {
                foreach (var kvp in deformedPositions)
                    positions[kvp.Key] = kvp.Value;
            }

            var result = new List<IsoFace>();

            // Six edges of a tetrahedron, defined by corner indices.
            var edges = new[]
            {
                (0, 1), (0, 2), (0, 3),
                (1, 2), (1, 3), (2, 3)
            };

            foreach (var element in model.Elements)
            {
                var cornerCount = GetCornerNodeCount(element.ElementType);
                if (cornerCount != 4 || element.NodeIds.Count < 4) continue;

                var ids = new int[4];
                var pos = new (double X, double Y, double Z)[4];
                var val = new double[4];
                var valid = true;

                for (var i = 0; i < 4; i++)
                {
                    ids[i] = element.NodeIds[i];
                    if (!positions.TryGetValue(ids[i], out pos[i]) || !nodeValues.TryGetValue(ids[i], out val[i]))
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid) continue;

                // Compute intersection points on edges that cross the iso-value.
                var crossings = new List<(double X, double Y, double Z)>();

                for (var e = 0; e < 6; e++)
                {
                    var (i0, i1) = edges[e];
                    var v0 = val[i0] - isoValue;
                    var v1 = val[i1] - isoValue;

                    // Edge crosses the iso-surface if signs differ (one positive, one negative).
                    if ((v0 > 0 && v1 < 0) || (v0 < 0 && v1 > 0))
                    {
                        var t = v0 / (v0 - v1);
                        crossings.Add(Lerp(pos[i0], pos[i1], t));
                    }
                }

                // Marching tetrahedra produces either 1 or 2 triangles.
                if (crossings.Count == 3)
                {
                    result.Add(new IsoFace(crossings.ToArray(), element.Id));
                }
                else if (crossings.Count == 4)
                {
                    // Quadrilateral → 2 triangles.
                    // Crossings are collected in edge-iteration order. Indices 1 and 2
                    // sit on opposing edges of the quad, so the correct winding is
                    // [0,1,3,2] — swap the last two to avoid a self-intersecting bowtie.
                    result.Add(new IsoFace(
                        new[] { crossings[0], crossings[1], crossings[3] },
                        element.Id));
                    result.Add(new IsoFace(
                        new[] { crossings[0], crossings[3], crossings[2] },
                        element.Id));
                }
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
                default:
                    return 0;
            }
        }
    }
}
