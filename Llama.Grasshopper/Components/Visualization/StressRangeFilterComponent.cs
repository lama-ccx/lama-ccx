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
    /// Filters elements by stress range, hiding those outside the threshold.
    /// Internal faces exposed by filtering become visible, revealing interior stress.
    /// </summary>
    public class StressRangeFilterComponent : GH_Component
    {
        public StressRangeFilterComponent()
            : base(
                "Stress Range Filter",
                "StressFilter",
                "Hide elements outside a stress range to reveal interior stress hotspots. " +
                "Internal faces exposed by filtering become visible.",
                "Llama",
                "Visualization")
        {
            Message = Name + "\nLlama";
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Model", "M", "StructuralModel with solved results.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Stress Min", "Smin", "Minimum stress threshold. Elements below this are hidden.", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("Stress Max", "Smax", "Maximum stress threshold. Elements above this are hidden.", GH_ParamAccess.item, 100.0);
            pManager.AddNumberParameter("Disp Scale", "Sc", "Displacement scale factor (1 = true scale). Set 0 for undeformed.", GH_ParamAccess.item, 0.0);
            pManager.AddTextParameter("Set Name", "S", "Optional CalculiX set name to filter results.", GH_ParamAccess.item);
            pManager[4].Optional = true;
            pManager.AddIntegerParameter("Stress Component", "C",
                "Stress component: 0=VonMises, 1=Sxx, 2=Syy, 3=Szz, 4=Sxy, 5=Sxz, 6=Syz.",
                GH_ParamAccess.item, 0);
            pManager.AddColourParameter("Colors", "Col", "Gradient colors for stress mapping.", GH_ParamAccess.list);
            pManager[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "M", "Filtered mesh colored by stress.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Visible Count", "N", "Number of visible elements.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object modelObj = null;
            double stressMin = 0, stressMax = 100;
            double dispScale = 0;
            string setName = null;
            int stressCompIdx = 0;
            var colors = new List<Color>();

            if (!DA.GetData(0, ref modelObj)) return;
            DA.GetData(1, ref stressMin);
            DA.GetData(2, ref stressMax);
            DA.GetData(3, ref dispScale);
            DA.GetData(4, ref setName);
            DA.GetData(5, ref stressCompIdx);
            DA.GetDataList(6, colors);

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

            // Compute per-node stress for interpolated filtering and coloring.
            var nodeStress = StressNodeAverager.ComputePerNodeStress(stresses, model.Elements, component);

            // Build node position map (original or deformed).
            var positions = new Dictionary<int, (double X, double Y, double Z)>(model.Nodes.Count);
            foreach (var n in model.Nodes)
                positions[n.Id] = (n.X, n.Y, n.Z);

            if (Math.Abs(dispScale) > 1e-12 && displacements != null && displacements.Count > 0)
            {
                var meshData = DeformedMeshBuilder.Build(model, displacements, dispScale);
                foreach (var kvp in meshData.DeformedPositions)
                    positions[kvp.Key] = kvp.Value;
            }

            // Interpolated filter: clips partial elements at stress thresholds.
            var interpFaces = InterpolatedStressRangeFilter.Filter(
                model, nodeStress, stressMin, stressMax, positions);

            if (interpFaces.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No elements within the specified range.");
                return;
            }

            var gradient = colors.Count >= 2 ? colors : DefaultGradient();

            // Build mesh from interpolated triangular faces.
            var mesh = new Mesh();
            foreach (var face in interpFaces)
            {
                var baseIdx = mesh.Vertices.Count;
                for (var i = 0; i < face.Vertices.Length; i++)
                {
                    var v = face.Vertices[i];
                    mesh.Vertices.Add(v.X, v.Y, v.Z);
                    mesh.VertexColors.Add(InterpolateGradient(gradient, face.StressValues[i], stressMin, stressMax));
                }

                mesh.Faces.AddFace(baseIdx, baseIdx + 1, baseIdx + 2);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();

            // Count visible elements.
            var visibleCount = new HashSet<int>(interpFaces.Select(f => f.ElementId)).Count;

            DA.SetData(0, mesh);
            DA.SetData(1, visibleCount);
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
        public override Guid ComponentGuid => new Guid("c4e5f6a7-2b3c-4d5e-9f0a-b2c3d4e5f6a7");
    }
}
