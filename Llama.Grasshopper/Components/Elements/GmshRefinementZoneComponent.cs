using System;
using System.Drawing;
using Grasshopper.Kernel;
using Llama.Core.Meshing;
using Llama.Gh.Widgets;
using Rhino.Geometry;

namespace Llama.Gh.Components
{
    public class GmshRefinementZoneComponent : GH_ExtendableComponent
    {
        public GmshRefinementZoneComponent()
            : base(
                "Gmsh Refinement Zone",
                "GmshRef",
                "Define a spherical mesh refinement zone (point + radii + size) for Gmsh. " +
                "The zone refines element size between DistMin and DistMax; " +
                "outside DistMax the global mesher size applies. " +
                "Connect to the Gmsh Tetra Mesh component's R input.",
                "Llama",
                "Elements")
        {
        }

        protected override void Setup(GH_ExtendableComponentAttributes attr)
        {
            // Reserved for future widget options (e.g. sigmoid vs linear transition)
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddPointParameter("Point", "P", "Center of the refinement zone.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Size", "S",
                "Target element size inside the zone.", GH_ParamAccess.item, 0.5);
            pManager.AddNumberParameter("Dist Min", "Dmin",
                "Inner radius — elements within this distance use the target Size.", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("Dist Max", "Dmax",
                "Outer radius — beyond this distance the global mesh size applies. " +
                "Between DistMin and DistMax the size transitions smoothly.", GH_ParamAccess.item, 10.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Refinement Zone", "R",
                "Gmsh refinement zone definition. Connect to the Gmsh Tetra Mesh component.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var point = Point3d.Unset;
            double size = 0.5;
            double distMin = 0.0;
            double distMax = 10.0;

            if (!DA.GetData(0, ref point)) return;
            DA.GetData(1, ref size);
            DA.GetData(2, ref distMin);
            DA.GetData(3, ref distMax);

            if (size <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Size must be positive.");
                return;
            }
            if (distMax <= distMin)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Dist Max must be greater than Dist Min.");
                return;
            }

            var zone = new GmshRefinementZone
            {
                X = point.X,
                Y = point.Y,
                Z = point.Z,
                SizeMin = size,
                SizeMax = -1,
                DistMin = distMin,
                DistMax = distMax
            };

            DA.SetData(0, zone);
        }

        protected override Bitmap Icon => Llama.Gh.Properties.Resources.Llama_24x24;
        public override GH_Exposure Exposure => GH_Exposure.secondary;
        public override Guid ComponentGuid => new Guid("b2f19a7e-c4d6-4e83-9a15-d7e3f6b81c24");
    }
}
