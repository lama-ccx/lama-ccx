using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Llama.Core.Application;
using Llama.Core.Model;
using Llama.Core.Model.Elements;
using Llama.Core.Optimization;
using Rhino.Geometry;

namespace Llama.Gh.Components
{
    public class BesoComponent : GH_Component
    {
        public BesoComponent()
            : base(
                "BESO Optimise",
                "BESO",
                "Bi-directional Evolutionary Structural Optimization. Iteratively removes material to maximise stiffness at a target volume fraction.",
                "Llama",
                "Optimization")
        {
            Message = Name + "\nLlama";
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Model", "M", "StructuralModel with mesh, materials, BCs, loads, and a static step.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Volume Fraction", "VF", "Target volume fraction (0-1). Default 0.5 = keep 50%.", GH_ParamAccess.item, 0.5);
            pManager.AddNumberParameter("Evolutionary Ratio", "ER", "Fraction of volume removed per iteration. Default 0.02.", GH_ParamAccess.item, 0.02);
            pManager.AddNumberParameter("Filter Radius", "R", "Spatial filter radius in model units. 0 disables filtering.", GH_ParamAccess.item, 0.0);
            pManager.AddIntegerParameter("Max Iterations", "N", "Maximum BESO iterations. Default 50.", GH_ParamAccess.item, 50);
            pManager.AddTextParameter("Executable", "Exe", "Path to CalculiX executable (optional, auto-detects if not provided).", GH_ParamAccess.item);
            pManager[5].Optional = true;
            pManager.AddBooleanParameter("Run", "Run", "Set to true to start the optimization.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Remaining Mesh", "Mesh", "Mesh of elements that remain after optimization (solid state).", GH_ParamAccess.list);
            pManager.AddMeshParameter("Removed Mesh", "Void", "Mesh of elements that were removed (void state).", GH_ParamAccess.list);
            pManager.AddNumberParameter("Volume History", "VH", "Volume fraction at each iteration.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Compliance History", "CH", "Compliance (total strain energy) at each iteration.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Solid Elements", "SE", "Element IDs that remain solid.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Void Elements", "VE", "Element IDs that were removed.", GH_ParamAccess.list);
            pManager.AddTextParameter("Log", "Log", "Optimization log.", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Converged", "C", "True if the optimization converged.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object modelObj = null;
            double volumeFraction = 0.5;
            double evolutionaryRatio = 0.02;
            double filterRadius = 0.0;
            int maxIterations = 50;
            string exePath = null;
            bool run = false;

            if (!DA.GetData(0, ref modelObj)) return;
            DA.GetData(1, ref volumeFraction);
            DA.GetData(2, ref evolutionaryRatio);
            DA.GetData(3, ref filterRadius);
            DA.GetData(4, ref maxIterations);
            DA.GetData(5, ref exePath);
            DA.GetData(6, ref run);

            if (!TryUnwrapStructuralModel(modelObj, out var model))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Model input must be a StructuralModel.");
                return;
            }

            if (!run)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Set Run to true to start BESO optimization.");
                DA.SetData(7, false);
                return;
            }

            // Resolve ccx executable
            if (string.IsNullOrWhiteSpace(exePath))
            {
                exePath = CalculixApplication.FindCalculixExecutable();
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "CalculiX executable not found. Provide the Exe path.");
                    return;
                }
            }

            if (!CalculixApplication.ValidateExecutable(exePath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"CalculiX executable not found at: {exePath}");
                return;
            }

            // Configure BESO
            var settings = new BesoSettings
            {
                TargetVolumeFraction = volumeFraction,
                EvolutionaryRatio = evolutionaryRatio,
                FilterRadius = filterRadius,
                MaxIterations = maxIterations
            };

            try
            {
                settings.Validate();
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            try
            {
                model.EnsureHasAnalysisSteps();
            }
            catch (InvalidOperationException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }

            // Run BESO
            var optimizer = new BesoOptimizer(model, settings, exePath);
            BesoResult result;
            try
            {
                result = optimizer.Run();
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"BESO failed: {ex.Message}");
                return;
            }

            // Build output meshes from element states
            var nodeMap = model.Nodes.ToDictionary(n => n.Id);
            var solidMeshes = new List<Mesh>();
            var voidMeshes = new List<Mesh>();
            var solidIds = new List<int>();
            var voidIds = new List<int>();

            foreach (var element in model.Elements)
            {
                bool isSolid = result.ElementStates.ContainsKey(element.Id) && result.ElementStates[element.Id];
                var mesh = ElementToMesh(element, nodeMap);
                if (mesh == null) continue;

                if (isSolid)
                {
                    solidMeshes.Add(mesh);
                    solidIds.Add(element.Id);
                }
                else
                {
                    voidMeshes.Add(mesh);
                    voidIds.Add(element.Id);
                }
            }

            // Join meshes for cleaner output
            var joinedSolid = JoinMeshes(solidMeshes);
            var joinedVoid = JoinMeshes(voidMeshes);

            DA.SetDataList(0, joinedSolid != null ? new[] { joinedSolid } : new Mesh[0]);
            DA.SetDataList(1, joinedVoid != null ? new[] { joinedVoid } : new Mesh[0]);
            DA.SetDataList(2, result.History.Select(h => h.VolumeFraction));
            DA.SetDataList(3, result.History.Select(h => h.Compliance));
            DA.SetDataList(4, solidIds);
            DA.SetDataList(5, voidIds);
            DA.SetData(6, result.Log);
            DA.SetData(7, result.Converged);

            if (result.Converged)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"BESO converged after {result.History.Count} iterations.");
            else
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"BESO did not converge after {result.History.Count} iterations.");
        }

        private static Mesh ElementToMesh(IElement element, Dictionary<int, Node> nodeMap)
        {
            var ids = element.NodeIds;

            // Build tet mesh (boundary faces) for 4 or 10 node tets
            if (ids.Count == 4 || ids.Count == 10)
                return TetToMesh(ids, nodeMap);

            // Build hex mesh (boundary faces) for 8 or 20 node hex
            if (ids.Count == 8 || ids.Count == 20)
                return HexToMesh(ids, nodeMap);

            return null;
        }

        private static Mesh TetToMesh(IReadOnlyList<int> ids, Dictionary<int, Node> nodeMap)
        {
            // Use first 4 nodes
            var pts = new Point3d[4];
            for (int i = 0; i < 4; i++)
            {
                if (!nodeMap.TryGetValue(ids[i], out var n)) return null;
                pts[i] = new Point3d(n.X, n.Y, n.Z);
            }

            var mesh = new Mesh();
            mesh.Vertices.AddVertices(pts);
            // 4 triangular faces of the tetrahedron
            mesh.Faces.AddFace(0, 1, 2);
            mesh.Faces.AddFace(0, 1, 3);
            mesh.Faces.AddFace(1, 2, 3);
            mesh.Faces.AddFace(0, 2, 3);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private static Mesh HexToMesh(IReadOnlyList<int> ids, Dictionary<int, Node> nodeMap)
        {
            var pts = new Point3d[8];
            for (int i = 0; i < 8; i++)
            {
                if (!nodeMap.TryGetValue(ids[i], out var n)) return null;
                pts[i] = new Point3d(n.X, n.Y, n.Z);
            }

            var mesh = new Mesh();
            mesh.Vertices.AddVertices(pts);
            // 6 quad faces of the hexahedron
            mesh.Faces.AddFace(0, 1, 2, 3); // bottom
            mesh.Faces.AddFace(4, 5, 6, 7); // top
            mesh.Faces.AddFace(0, 1, 5, 4); // front
            mesh.Faces.AddFace(2, 3, 7, 6); // back
            mesh.Faces.AddFace(0, 3, 7, 4); // left
            mesh.Faces.AddFace(1, 2, 6, 5); // right
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private static Mesh JoinMeshes(List<Mesh> meshes)
        {
            if (meshes.Count == 0) return null;
            if (meshes.Count == 1) return meshes[0];

            var joined = new Mesh();
            foreach (var m in meshes)
                joined.Append(m);
            joined.Normals.ComputeNormals();
            joined.Compact();
            return joined;
        }

        private static bool TryUnwrapStructuralModel(object input, out StructuralModel model)
        {
            model = input as StructuralModel;
            if (model != null) return true;

            if (input is IGH_Goo goo)
            {
                var scriptValue = goo.ScriptVariable();
                model = scriptValue as StructuralModel;
                if (model != null) return true;
            }

            var valueProp = input?.GetType().GetProperty("Value");
            if (valueProp != null && valueProp.GetIndexParameters().Length == 0)
            {
                try
                {
                    var value = valueProp.GetValue(input);
                    model = value as StructuralModel;
                    if (model != null) return true;
                }
                catch { }
            }

            return false;
        }

        protected override Bitmap Icon => Llama.Gh.Properties.Resources.Llama_24x24;

        public override Guid ComponentGuid => new Guid("a7b3c4d5-e6f7-4a8b-9c0d-1e2f3a4b5c6d");
    }
}
