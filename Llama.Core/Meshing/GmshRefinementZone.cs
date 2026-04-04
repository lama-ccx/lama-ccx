namespace Llama.Core.Meshing
{
    /// <summary>
    /// Defines a spherical mesh refinement zone for Gmsh.
    /// Gmsh will use a Distance + Threshold field pair to smoothly transition
    /// element size from <see cref="SizeMin"/> (within <see cref="DistMin"/>)
    /// to <see cref="SizeMax"/> (beyond <see cref="DistMax"/>).
    /// </summary>
    public class GmshRefinementZone
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double SizeMin { get; set; } = 0.5;
        public double SizeMax { get; set; } = 5.0;
        public double DistMin { get; set; }
        public double DistMax { get; set; } = 10.0;
    }
}
