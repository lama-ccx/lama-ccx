using System.Collections.Generic;

namespace Llama.Core.Meshing
{
    /// <summary>
    /// Options controlling the Gmsh <c>.geo</c> script generation for FEA-quality tet meshing.
    /// </summary>
    public class GmshMeshOptions
    {
        /// <summary>Minimum characteristic element length.</summary>
        public double MinSize { get; set; } = 1.0;

        /// <summary>Maximum characteristic element length.</summary>
        public double MaxSize { get; set; } = 5.0;

        /// <summary>1 = linear (C3D4), 2 = quadratic (C3D10).</summary>
        public int ElementOrder { get; set; } = 2;

        /// <summary>3D meshing algorithm: 1=Delaunay, 4=Frontal, 7=MMG3D, 10=HXT.</summary>
        public int Algorithm3D { get; set; } = 1;

        /// <summary>Enable general mesh optimization (0=off, 1=on).</summary>
        public int Optimize { get; set; } = 1;

        /// <summary>Enable Netgen optimization (0=off, 1=on).</summary>
        public int OptimizeNetgen { get; set; } = 1;

        /// <summary>Quality threshold below which elements are optimized (0.0–1.0).</summary>
        public double OptimizeThreshold { get; set; } = 0.3;

        /// <summary>Number of Laplacian smoothing iterations.</summary>
        public int Smoothing { get; set; } = 5;

        /// <summary>High-order optimization: 0=none, 1=optimization, 2=elastic+optimization, 3=elastic, 4=fast curving.</summary>
        public int HighOrderOptimize { get; set; } = 2;

        /// <summary>Quality metric: 0=SICN, 1=SIGE, 2=gamma (inscribed/circumscribed).</summary>
        public int QualityType { get; set; } = 2;

        /// <summary>Maximum element anisotropy ratio.</summary>
        public double AnisoMax { get; set; } = 1e10;

        /// <summary>
        /// Number of elements per 2π for automatic curvature-based refinement (0 = disabled).
        /// Useful for STEP/CAD files with fillets, holes, or curved surfaces.
        /// Typical values: 12–36.
        /// </summary>
        public int MeshSizeFromCurvature { get; set; } = 0;

        /// <summary>Optional local refinement zones (Distance + Threshold fields).</summary>
        public List<GmshRefinementZone> RefinementZones { get; set; } = new List<GmshRefinementZone>();
    }
}
