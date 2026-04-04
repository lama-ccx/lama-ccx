using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Llama.Core.Meshing;
using Llama.Gh.Widgets;
using Rhino.Geometry;

namespace Llama.Gh.Components
{
    /// <summary>
    /// Grasshopper component that tetrahedralizes a Brep or Mesh using Gmsh.
    /// For Brep/Mesh inputs the geometry is exported as STL.
    /// Alternatively, a path to a STEP/IGES file can be provided for higher-fidelity
    /// CAD-based meshing via the OpenCASCADE kernel.
    /// Outputs tetrahedral Rhino meshes (V:4, F:4) and the Gmsh log.
    /// </summary>
    public class GmshTetraMeshComponent : GH_ExtendableComponent
    {
        // --- Widget fields ---
        private MenuSlider _sliderMinSize;
        private MenuSlider _sliderMaxSize;
        private MenuSlider _sliderOptThreshold;
        private MenuSlider _sliderSmoothing;
        private MenuSlider _sliderAnisoMax;
        private MenuDropDown _ddAlgorithm3D;
        private MenuDropDown _ddElementOrder;
        private MenuDropDown _ddQualityType;
        private MenuDropDown _ddHighOrderOpt;
        private MenuCheckBox _cbOptimize;
        private MenuCheckBox _cbOptNetgen;

        public GmshTetraMeshComponent()
            : base(
                "Gmsh Tetra Mesh",
                "GmshTet",
                "Generate a tetrahedral volume mesh from a Brep, Mesh, or CAD file (STEP/IGES) " +
                "using the Gmsh mesher.",
                "Llama",
                "Elements")
        {
        }

        protected override void Setup(GH_ExtendableComponentAttributes attr)
        {
            var menu = new GH_ExtendableMenu(0, "mesh_options") { Name = "Mesh Options" };

            // --- Size sliders ---
            _sliderMinSize = new MenuSlider(0, "min_size", 0.01, 1000.0, 1.0, 2)
                { Header = "Mesh.CharacteristicLengthMin — minimum element edge length." };
            _sliderMaxSize = new MenuSlider(1, "max_size", 0.01, 1000.0, 5.0, 2)
                { Header = "Mesh.CharacteristicLengthMax — maximum element edge length." };

            // --- Element order ---
            _ddElementOrder = new MenuDropDown(2, "elem_order", "Element Order")
                { Header = "Mesh.ElementOrder — 1 = linear C3D4, 2 = quadratic C3D10." };
            _ddElementOrder.AddItem("1", "Linear (C3D4)");
            _ddElementOrder.AddItem("2", "Quadratic (C3D10)");
            _ddElementOrder.Value = 1; // default = quadratic

            // --- Algorithm 3D ---
            _ddAlgorithm3D = new MenuDropDown(3, "algo3d", "Algorithm 3D")
                { Header = "Mesh.Algorithm3D — 3D meshing algorithm.\n1=Delaunay, 4=Frontal, 7=MMG3D, 10=HXT." };
            _ddAlgorithm3D.AddItem("1", "Delaunay");
            _ddAlgorithm3D.AddItem("4", "Frontal");
            _ddAlgorithm3D.AddItem("7", "MMG3D");
            _ddAlgorithm3D.AddItem("10", "HXT");
            _ddAlgorithm3D.Value = 0; // default = Delaunay

            // --- Optimize checkboxes ---
            var rowOpt = new MenuHorizontalPanel(4, "opt_row");
            _cbOptimize = new MenuCheckBox(0, "optimize", "Optimize")
                { Active = true, TagPosition = MenuCheckBox.TagPlacement.Above,
                  Header = "Mesh.Optimize — enable Gmsh's built-in mesh optimizer." };
            _cbOptNetgen = new MenuCheckBox(1, "opt_netgen", "Netgen")
                { Active = true, TagPosition = MenuCheckBox.TagPlacement.Above,
                  Header = "Mesh.OptimizeNetgen — enable Netgen-based optimization." };
            rowOpt.AddControl(_cbOptimize);
            rowOpt.AddControl(_cbOptNetgen);

            // --- Optimize threshold ---
            _sliderOptThreshold = new MenuSlider(5, "opt_threshold", 0.0, 1.0, 0.3, 2)
                { Header = "Mesh.OptimizeThreshold — quality threshold below which elements are optimized." };

            // --- Smoothing ---
            _sliderSmoothing = new MenuSlider(6, "smoothing", 0, 100, 5, 0)
                { Header = "Mesh.Smoothing — number of smoothing passes applied to the mesh." };

            // --- High-order optimization ---
            _ddHighOrderOpt = new MenuDropDown(7, "ho_opt", "HighOrder Optimize")
                { Header = "Mesh.HighOrderOptimize — optimization strategy for high-order nodes.\n0=None, 1=Optimization, 2=Elastic+Opt, 3=Elastic, 4=Fast curving." };
            _ddHighOrderOpt.AddItem("0", "None");
            _ddHighOrderOpt.AddItem("1", "Optimization");
            _ddHighOrderOpt.AddItem("2", "Elastic + Opt");
            _ddHighOrderOpt.AddItem("3", "Elastic");
            _ddHighOrderOpt.AddItem("4", "Fast curving");
            _ddHighOrderOpt.Value = 2; // default = Elastic + Opt

            // --- Quality type ---
            _ddQualityType = new MenuDropDown(8, "quality_type", "Quality Type")
                { Header = "Mesh.QualityType — element quality measure used by Gmsh.\n0=SICN (Scaled Inverse Condition Number), 1=SIGE, 2=Gamma." };
            _ddQualityType.AddItem("0", "SICN");
            _ddQualityType.AddItem("1", "SIGE");
            _ddQualityType.AddItem("2", "Gamma");
            _ddQualityType.Value = 2; // default = Gamma

            // --- AnisoMax ---
            _sliderAnisoMax = new MenuSlider(9, "aniso_max", 1.0, 1e10, 1e10, 0)
                { NumberFormat = "{0:0.##E+0}",
                  Header = "Mesh.AnisoMax — maximum anisotropy ratio for mesh elements." };

            // Wire up ValueChanged
            _sliderMinSize.ValueChanged += OnWidgetChanged;
            _sliderMaxSize.ValueChanged += OnWidgetChanged;
            _sliderOptThreshold.ValueChanged += OnWidgetChanged;
            _sliderSmoothing.ValueChanged += OnWidgetChanged;
            _sliderAnisoMax.ValueChanged += OnWidgetChanged;
            _ddAlgorithm3D.ValueChanged += OnWidgetChanged;
            _ddElementOrder.ValueChanged += OnWidgetChanged;
            _ddQualityType.ValueChanged += OnWidgetChanged;
            _ddHighOrderOpt.ValueChanged += OnWidgetChanged;
            _cbOptimize.ValueChanged += OnWidgetChanged;
            _cbOptNetgen.ValueChanged += OnWidgetChanged;

            // --- Labels + controls inside a MenuPanel (stacks vertically) ---
            var panel = new MenuPanel(10, "opts_panel");
            panel.AddControl(new MenuStaticText { Text = "Min Size" });
            panel.AddControl(_sliderMinSize);
            panel.AddControl(new MenuStaticText { Text = "Max Size" });
            panel.AddControl(_sliderMaxSize);
            panel.AddControl(_ddElementOrder);
            panel.AddControl(_ddAlgorithm3D);
            panel.AddControl(rowOpt);
            panel.AddControl(new MenuStaticText { Text = "Opt Threshold" });
            panel.AddControl(_sliderOptThreshold);
            panel.AddControl(new MenuStaticText { Text = "Smoothing" });
            panel.AddControl(_sliderSmoothing);
            panel.AddControl(_ddHighOrderOpt);
            panel.AddControl(_ddQualityType);
            panel.AddControl(new MenuStaticText { Text = "AnisoMax" });
            panel.AddControl(_sliderAnisoMax);

            menu.AddControl(panel);

            menu.Expand();
            attr.AddMenu(menu);
        }

        private void OnWidgetChanged(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Brep", "B", "Brep solid to tetrahedralize.", GH_ParamAccess.item);
            pManager[0].Optional = true;

            pManager.AddMeshParameter("Mesh", "M", "Surface mesh to tetrahedralize.", GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddTextParameter("CAD File", "F",
                "Optional path to a STEP (.step/.stp), IGES (.iges/.igs), or STL file. " +
                "When provided, Brep/Mesh inputs are ignored.",
                GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddGenericParameter("Refinement", "R",
                "Refinement zones (from Gmsh Refinement Zone component).", GH_ParamAccess.list);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Tetra Meshes", "T", "Tetrahedral meshes (one per element, V:4 F:4).", GH_ParamAccess.list);
            pManager.AddPointParameter("Nodes", "N", "Unique mesh nodes.", GH_ParamAccess.list);
            pManager.AddTextParameter("Log", "L", "Gmsh console output (stdout + stderr).", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- Collect inputs ---------------------------------------------------
            Brep brep = null;
            Mesh rhinoMesh = null;
            string cadFilePath = null;

            DA.GetData(0, ref brep);
            DA.GetData(1, ref rhinoMesh);
            DA.GetData(2, ref cadFilePath);

            // --- Build mesh options from widgets ---------------------------------
            var algoValues = new[] { 1, 4, 7, 10 };
            var hoValues = new[] { 0, 1, 2, 3, 4 };

            var options = new GmshMeshOptions
            {
                MinSize = _sliderMinSize.Value,
                MaxSize = _sliderMaxSize.Value,
                ElementOrder = _ddElementOrder.Value == 0 ? 1 : 2,
                Algorithm3D = algoValues[Math.Min(_ddAlgorithm3D.Value, algoValues.Length - 1)],
                Optimize = _cbOptimize.Active ? 1 : 0,
                OptimizeNetgen = _cbOptNetgen.Active ? 1 : 0,
                OptimizeThreshold = _sliderOptThreshold.Value,
                Smoothing = (int)_sliderSmoothing.Value,
                HighOrderOptimize = hoValues[Math.Min(_ddHighOrderOpt.Value, hoValues.Length - 1)],
                QualityType = Math.Min(_ddQualityType.Value, 2),
                AnisoMax = _sliderAnisoMax.Value
            };

            // --- Collect refinement zones -----------------------------------------
            var zoneInputs = new List<object>();
            DA.GetDataList(3, zoneInputs);
            foreach (var input in zoneInputs)
            {
                GmshRefinementZone zone = null;
                if (input is GmshRefinementZone z)
                    zone = z;
                else if (input is IGH_Goo goo)
                    zone = goo.ScriptVariable() as GmshRefinementZone;

                if (zone != null)
                {
                    if (zone.SizeMax <= 0)
                        zone.SizeMax = options.MaxSize;
                    options.RefinementZones.Add(zone);
                }
            }

            // --- Resolve geometry source ------------------------------------------
            string geometryFilePath;
            string workDir;

            if (!string.IsNullOrWhiteSpace(cadFilePath))
            {
                if (!File.Exists(cadFilePath))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"CAD file not found: {cadFilePath}");
                    return;
                }

                geometryFilePath = cadFilePath;
                workDir = Path.GetDirectoryName(cadFilePath);
            }
            else if (brep != null || rhinoMesh != null)
            {
                workDir = Path.Combine(Path.GetTempPath(), "Llama",
                    "Gmsh_" + InstanceGuid.ToString("N").Substring(0, 8));
                Directory.CreateDirectory(workDir);

                geometryFilePath = Path.Combine(workDir, "model.stl");

                try
                {
                    var surfaceMesh = BuildSurfaceMesh(brep, rhinoMesh);
                    WriteAsciiStl(surfaceMesh, geometryFilePath);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Failed to export geometry to STL: {ex.Message}");
                    return;
                }
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Provide a Brep, a Mesh, or a CAD file path.");
                return;
            }

            // --- Run Gmsh --------------------------------------------------------
            string gmshLog = null;
            try
            {
                var model = GmshTetraMesher.Mesh(
                    geometryFilePath,
                    options,
                    "TETRA",
                    null,
                    out gmshLog);

                var tetraMeshes = BuildTetraMeshes(model);
                var nodes = model.Nodes.Select(n => new Point3d(n.X, n.Y, n.Z)).ToList();

                DA.SetDataList(0, tetraMeshes);
                DA.SetDataList(1, nodes);
                DA.SetData(2, gmshLog);
            }
            catch (Exception ex)
            {
                if (gmshLog != null)
                    DA.SetData(2, gmshLog);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Tetra mesh builder
        // ------------------------------------------------------------------

        private static List<Mesh> BuildTetraMeshes(Core.Model.StructuralModel model)
        {
            var nodeMap = new Dictionary<int, Point3d>(model.Nodes.Count);
            foreach (var n in model.Nodes)
                nodeMap[n.Id] = new Point3d(n.X, n.Y, n.Z);

            var meshes = new List<Mesh>(model.Elements.Count);
            foreach (var elem in model.Elements)
            {
                var nids = elem.NodeIds;
                if (nids.Count < 4) continue;

                var m = new Mesh();
                m.Vertices.Add(nodeMap[nids[0]]);
                m.Vertices.Add(nodeMap[nids[1]]);
                m.Vertices.Add(nodeMap[nids[2]]);
                m.Vertices.Add(nodeMap[nids[3]]);

                // 4 triangular faces of the tetrahedron
                m.Faces.AddFace(0, 2, 1);
                m.Faces.AddFace(0, 1, 3);
                m.Faces.AddFace(1, 2, 3);
                m.Faces.AddFace(0, 3, 2);

                m.Normals.ComputeNormals();
                m.Compact();
                meshes.Add(m);
            }

            return meshes;
        }

        // ------------------------------------------------------------------
        // Geometry helpers
        // ------------------------------------------------------------------

        private Mesh BuildSurfaceMesh(Brep brep, Mesh rhinoMesh)
        {
            if (brep != null)
            {
                if (!brep.IsSolid)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Brep is not a closed solid — volume meshing may fail.");

                var mp = MeshingParameters.Default;
                var pieces = Mesh.CreateFromBrep(brep, mp);
                if (pieces == null || pieces.Length == 0)
                    throw new InvalidOperationException("Rhino failed to mesh the Brep.");

                var joined = new Mesh();
                foreach (var piece in pieces)
                    joined.Append(piece);

                return joined;
            }

            if (rhinoMesh != null)
            {
                if (!rhinoMesh.IsClosed)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Mesh is not closed — volume meshing may fail.");

                return rhinoMesh.DuplicateMesh();
            }

            throw new InvalidOperationException("No geometry provided.");
        }

        private static void WriteAsciiStl(Mesh mesh, string filePath)
        {
            mesh.FaceNormals.ComputeFaceNormals();

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine("solid model");

                for (var i = 0; i < mesh.Faces.Count; i++)
                {
                    var face = mesh.Faces[i];
                    var normal = mesh.FaceNormals[i];

                    WriteFacet(writer, normal,
                        mesh.Vertices[face.A],
                        mesh.Vertices[face.B],
                        mesh.Vertices[face.C]);

                    if (face.IsQuad)
                    {
                        WriteFacet(writer, normal,
                            mesh.Vertices[face.A],
                            mesh.Vertices[face.C],
                            mesh.Vertices[face.D]);
                    }
                }

                writer.WriteLine("endsolid model");
            }
        }

        private static void WriteFacet(StreamWriter writer, Vector3f normal,
            Point3f v1, Point3f v2, Point3f v3)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  facet normal {0} {1} {2}", normal.X, normal.Y, normal.Z));
            writer.WriteLine("    outer loop");
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "      vertex {0} {1} {2}", v1.X, v1.Y, v1.Z));
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "      vertex {0} {1} {2}", v2.X, v2.Y, v2.Z));
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "      vertex {0} {1} {2}", v3.X, v3.Y, v3.Z));
            writer.WriteLine("    endloop");
            writer.WriteLine("  endfacet");
        }

        protected override Bitmap Icon => Llama.Gh.Properties.Resources.Llama_24x24;
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        public override Guid ComponentGuid => new Guid("7a3e5c12-d8b4-4f91-ae72-c1d3f5e79b08");
    }
}