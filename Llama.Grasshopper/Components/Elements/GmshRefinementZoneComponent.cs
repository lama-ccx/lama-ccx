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
                "Define a spherical mesh refinement zone (point + radii + sizes) for Gmsh. " +
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
            pManager.AddNumberParameter("Size Min", "Smin",
                "Target element size at the center of the zone.", GH_ParamAccess.item, 0.5);
            pManager.AddNumberParameter("Size Max", "Smax",
                "Element size beyond DistMax. If not provided, the global MaxSize from the mesher is used.",
                GH_ParamAccess.item);
            pManager[2].Optional = true;
            pManager.AddNumberParameter("Dist Min", "Dmin",
                "Inner radius — elements within this distance use SizeMin.", GH_ParamAccess.item, 0.0);
            pManager.AddNumberParameter("Dist Max", "Dmax",
                "Outer radius — beyond this distance, element size equals SizeMax.", GH_ParamAccess.item, 10.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Refinement Zone", "R",
                "Gmsh refinement zone definition. Connect to the Gmsh Tetra Mesh component.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var point = Point3d.Unset;
            double sizeMin = 0.5;
            double sizeMax = -1;
            double distMin = 0.0;
            double distMax = 10.0;

            if (!DA.GetData(0, ref point)) return;
            DA.GetData(1, ref sizeMin);
            DA.GetData(2, ref sizeMax);
            DA.GetData(3, ref distMin);
            DA.GetData(4, ref distMax);

            if (sizeMin <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Size Min must be positive.");
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
                SizeMin = sizeMin,
                SizeMax = sizeMax > 0 ? sizeMax : -1,
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
