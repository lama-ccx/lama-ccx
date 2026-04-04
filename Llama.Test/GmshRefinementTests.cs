using System.Collections.Generic;
using System.IO;
using Llama.Core.Meshing;
using Xunit;

namespace Llama.Test
{
    public class GmshRefinementTests
    {
        [Fact]
        public void WriteGeoScript_WithRefinementZones_EmitsFieldBlock()
        {
            var opts = new GmshMeshOptions
            {
                MinSize = 1.0,
                MaxSize = 10.0,
                RefinementZones = new List<GmshRefinementZone>
                {
                    new GmshRefinementZone
                    {
                        X = 5, Y = 0, Z = 0,
                        SizeMin = 0.5, SizeMax = 10,
                        DistMin = 0, DistMax = 8
                    },
                    new GmshRefinementZone
                    {
                        X = -3, Y = 2, Z = 1,
                        SizeMin = 0.2, SizeMax = 5,
                        DistMin = 1, DistMax = 6
                    }
                }
            };

            var geoPath = Path.Combine(Path.GetTempPath(), "test_refine.geo");
            try
            {
                GmshTetraMesher.WriteGeoScript(geoPath, "model.stl", opts);
                var geo = File.ReadAllText(geoPath);

                // Two Distance fields
                Assert.Contains("Field[1] = Distance;", geo);
                Assert.Contains("Field[3] = Distance;", geo);

                // Two Threshold fields
                Assert.Contains("Field[2] = Threshold;", geo);
                Assert.Contains("Field[4] = Threshold;", geo);

                // Threshold parameters
                Assert.Contains("Field[2].SizeMin = 0.5;", geo);
                Assert.Contains("Field[2].DistMax = 8;", geo);
                Assert.Contains("Field[4].SizeMin = 0.2;", geo);
                Assert.Contains("Field[4].DistMax = 6;", geo);

                // Min field combining both thresholds
                Assert.Contains("Field[5] = Min;", geo);
                Assert.Contains("Field[5].FieldsList = {2, 4};", geo);
                Assert.Contains("Background Field = 5;", geo);

                // Points
                Assert.Contains("Point(10001)", geo);
                Assert.Contains("Point(10002)", geo);
            }
            finally
            {
                if (File.Exists(geoPath))
                    File.Delete(geoPath);
            }
        }

        [Fact]
        public void WriteGeoScript_NoRefinementZones_NoFieldBlock()
        {
            var opts = new GmshMeshOptions { MinSize = 1, MaxSize = 5 };
            var geoPath = Path.Combine(Path.GetTempPath(), "test_no_refine.geo");
            try
            {
                GmshTetraMesher.WriteGeoScript(geoPath, "model.stl", opts);
                var geo = File.ReadAllText(geoPath);

                Assert.DoesNotContain("Field[", geo);
                Assert.DoesNotContain("Background Field", geo);
            }
            finally
            {
                if (File.Exists(geoPath))
                    File.Delete(geoPath);
            }
        }

        [Fact]
        public void WriteGeoScript_StlInput_EmitsClassifyAndSizeOverrides()
        {
            var opts = new GmshMeshOptions { MinSize = 0.5, MaxSize = 8 };
            var geoPath = Path.Combine(Path.GetTempPath(), "test_stl_classify.geo");
            try
            {
                GmshTetraMesher.WriteGeoScript(geoPath, "model.stl", opts);
                var geo = File.ReadAllText(geoPath);

                Assert.Contains("ClassifySurfaces{Pi/4, 1, 1};", geo);
                Assert.Contains("Mesh.CharacteristicLengthFromPoints = 0;", geo);
                Assert.Contains("Mesh.CharacteristicLengthExtendFromBoundary = 0;", geo);
            }
            finally
            {
                if (File.Exists(geoPath))
                    File.Delete(geoPath);
            }
        }

        [Fact]
        public void WriteGeoScript_SingleZone_MinFieldContainsOneEntry()
        {
            var opts = new GmshMeshOptions
            {
                MinSize = 0.5,
                MaxSize = 8.0,
                RefinementZones = new List<GmshRefinementZone>
                {
                    new GmshRefinementZone
                    {
                        X = 0, Y = 0, Z = 0,
                        SizeMin = 0.1, SizeMax = 8,
                        DistMin = 0, DistMax = 5
                    }
                }
            };

            var geoPath = Path.Combine(Path.GetTempPath(), "test_single_refine.geo");
            try
            {
                GmshTetraMesher.WriteGeoScript(geoPath, "model.stl", opts);
                var geo = File.ReadAllText(geoPath);

                Assert.Contains("Field[3] = Min;", geo);
                Assert.Contains("Field[3].FieldsList = {2};", geo);
                Assert.Contains("Background Field = 3;", geo);
            }
            finally
            {
                if (File.Exists(geoPath))
                    File.Delete(geoPath);
            }
        }
    }
}
