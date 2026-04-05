using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Llama.Core.Model;
using Llama.Core.PostProcessing;
using Rhino.Geometry;

namespace Llama.Gh.Components
{
    /// <summary>
    /// Shrinks each tetrahedron toward its centroid to reveal interior stress coloring.
    /// </summary>
    public class ExplodedViewComponent : GH_Component
    {
        public ExplodedViewComponent()
            : base(
                "Exploded View",
                "Explode",
                "Shrink each tetrahedron toward its centroid, creating gaps that reveal interior stress.",
                "Llama",
                "Visualization")
        {
            Message = Name + "\nLlama";
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Model", "M", "StructuralModel with solved results.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Shrink Factor", "F", "0.0 = collapsed to centroid, 1.0 = no shrink. Typical: 0.7–0.9.", GH_ParamAccess.item, 0.8);
            pManager.AddNumberParameter("Disp Scale", "Sc", "Displacement scale factor (1 = true scale). Set 0 for undeformed.", GH_ParamAccess.item, 0.0);
            pManager.AddTextParameter("Set Name", "S", "Optional CalculiX set name to filter results.", GH_ParamAccess.item);
            pManager[3].Optional = true;
            pManager.AddIntegerParameter("Stress Component", "C",
                "Stress component: 0=VonMises, 1=Sxx, 2=Syy, 3=Szz, 4=Sxy, 5=Sxz, 6=Syz.",
                GH_ParamAccess.item, 0);
            pManager.AddColourParameter("Colors", "Col", "Gradient colors for stress mapping.", GH_ParamAccess.list);
            pManager[5].Optional = true;
            pManager.AddNumberParameter("Stress Min", "Smin", "Override minimum stress for color mapping.", GH_ParamAccess.item);
            pManager[6].Optional = true;
            pManager.AddNumberParameter("Stress Max", "Smax", "Override maximum stress for color mapping.", GH_ParamAccess.item);
            pManager[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "M", "Exploded mesh colored by stress.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Stress Min", "Smin", "Effective minimum stress.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Stress Max", "Smax", "Effective maximum stress.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object modelObj = null;
            double shrinkFactor = 0.8;
            double dispScale = 0;
            string setName = null;
            int stressCompIdx = 0;
            var colors = new List<Color>();
            double userMin = double.NaN;
            double userMax = double.NaN;

            if (!DA.GetData(0, ref modelObj)) return;
            DA.GetData(1, ref shrinkFactor);
            DA.GetData(2, ref dispScale);
            DA.GetData(3, ref setName);
            DA.GetData(4, ref stressCompIdx);
            DA.GetDataList(5, colors);
            DA.GetData(6, ref userMin);
            DA.GetData(7, ref userMax);

            if (!TryUnwrapModel(modelObj, out var model)) return;

            var datPath = ResolveDatPath(model);
            if (string.IsNullOrEmpty(datPath) || !File.Exists(datPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "DAT file not found.");
                return;
            }

            var tables = CalculixDatParser.ParseFile(datPath);
            var trimmedSet = string.IsNullOrWhiteSpace(setName) ? null : setName.Trim();

            CalculixDatExtractors.TryGetNodalDisplacements(tables, out var displacements, trimmedSet);
            CalculixDatExtractors.TryGetElementStress(tables, out var stresses, trimmedSet);

            if (stresses == null || stresses.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No stress results found.");
                return;
            }

            var component = MapStressComponent(stressCompIdx);
            var nodeStress = StressNodeAverager.ComputePerNodeStress(stresses, model.Elements, component);
            var range = StressNodeAverager.GetRange(nodeStress);

            // Build deformed positions.
            IReadOnlyDictionary<int, (double, double, double)> deformedPos = null;
            if (Math.Abs(dispScale) > 1e-12 && displacements != null && displacements.Count > 0)
            {
                var meshData = DeformedMeshBuilder.Build(model, displacements, dispScale);
                deformedPos = meshData.DeformedPositions;
            }

            var tetras = TetraExplodedView.Build(model, shrinkFactor, nodeStress, deformedPos);

            if (tetras.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No tetrahedral elements found.");
                return;
            }

            var effectiveMin = double.IsNaN(userMin) ? range.Min : userMin;
            var effectiveMax = double.IsNaN(userMax) ? range.Max : userMax;

            var gradient = colors.Count >= 2 ? colors : DefaultGradient();

            // Tetrahedron face table: 4 triangular faces defined by local vertex indices.
            var faceTable = new[]
            {
                new[] { 0, 2, 1 },
                new[] { 0, 1, 3 },
                new[] { 1, 2, 3 },
                new[] { 0, 3, 2 }
            };

            var mesh = new Mesh();

            foreach (var tet in tetras)
            {
                var baseIdx = mesh.Vertices.Count;

                for (var i = 0; i < 4; i++)
                {
                    var v = tet.Vertices[i];
                    mesh.Vertices.Add(v.X, v.Y, v.Z);
                    mesh.VertexColors.Add(InterpolateGradient(gradient, tet.StressValues[i], effectiveMin, effectiveMax));
                }

                foreach (var face in faceTable)
                    mesh.Faces.AddFace(baseIdx + face[0], baseIdx + face[1], baseIdx + face[2]);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            DA.SetData(0, mesh);
            DA.SetData(1, effectiveMin);
            DA.SetData(2, effectiveMax);
        }

        private static StressNodeAverager.StressComponent MapStressComponent(int idx)
        {
            switch (idx)
            {
                case 0: return StressNodeAverager.StressComponent.SvM;
                case 1: return StressNodeAverager.StressComponent.Sxx;
                case 2: return StressNodeAverager.StressComponent.Syy;
                case 3: return StressNodeAverager.StressComponent.Szz;
                case 4: return StressNodeAverager.StressComponent.Sxy;
                case 5: return StressNodeAverager.StressComponent.Sxz;
                case 6: return StressNodeAverager.StressComponent.Syz;
                default: return StressNodeAverager.StressComponent.SvM;
            }
        }

        private static bool TryUnwrapModel(object input, out StructuralModel model)
        {
            model = input as StructuralModel;
            if (model != null) return true;
            if (input is IGH_Goo goo)
            {
                model = goo.ScriptVariable() as StructuralModel;
                if (model != null) return true;
            }
            var prop = input?.GetType().GetProperty("Value");
            if (prop != null)
            {
                try { model = prop.GetValue(input) as StructuralModel; }
                catch { /* ignored */ }
            }
            return model != null;
        }

        private static string ResolveDatPath(StructuralModel model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Path)) return string.Empty;
            var ext = Path.GetExtension(model.Path);
            if (string.Equals(ext, ".dat", StringComparison.OrdinalIgnoreCase)) return model.Path;
            if (string.Equals(ext, ".inp", StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(model.Path, ".dat");
            return model.Path + ".dat";
        }

        private static Color InterpolateGradient(IReadOnlyList<Color> gradient, double value, double min, double max)
        {
            if (gradient.Count == 0) return Color.Gray;
            if (gradient.Count == 1) return gradient[0];
            var range = max - min;
            var t = range < 1e-15 ? 0.5 : Math.Max(0.0, Math.Min(1.0, (value - min) / range));
            var scaled = t * (gradient.Count - 1);
            var idx = (int)Math.Floor(scaled);
            if (idx >= gradient.Count - 1) return gradient[gradient.Count - 1];
            var frac = scaled - idx;
            var c0 = gradient[idx];
            var c1 = gradient[idx + 1];
            return Color.FromArgb(
                (int)(c0.R + frac * (c1.R - c0.R)),
                (int)(c0.G + frac * (c1.G - c0.G)),
                (int)(c0.B + frac * (c1.B - c0.B)));
        }

        private static List<Color> DefaultGradient()
        {
            return new List<Color>
            {
                Color.FromArgb(48, 18, 59),
                Color.FromArgb(68, 90, 205),
                Color.FromArgb(62, 155, 254),
                Color.FromArgb(24, 214, 203),
                Color.FromArgb(70, 247, 131),
                Color.FromArgb(162, 252, 60),
                Color.FromArgb(225, 220, 55),
                Color.FromArgb(253, 165, 49),
                Color.FromArgb(239, 90, 17),
                Color.FromArgb(196, 37, 2)
            };
        }

        protected override Bitmap Icon => Llama.Gh.Properties.Resources.Llama_24x24;
        public override GH_Exposure Exposure => GH_Exposure.quarternary;
        public override Guid ComponentGuid => new Guid("e6a7b8c9-4d5e-6f7a-b1c2-d4e5f6a7b8c9");
    }
}
