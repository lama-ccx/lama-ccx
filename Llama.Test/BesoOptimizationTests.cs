using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Llama.Core.Application;
using Llama.Core.InputDeck;
using Llama.Core.Materials;
using Llama.Core.Model;
using Llama.Core.Model.Boundary;
using Llama.Core.Model.Elements;
using Llama.Core.Model.Loads;
using Llama.Core.Model.Sections;
using Llama.Core.Model.Steps;
using Llama.Core.Optimization;
using Xunit;
using Xunit.Abstractions;

namespace Llama.Test
{
    public class BesoOptimizationTests
    {
        private readonly ITestOutputHelper _output;

        public BesoOptimizationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void BesoSettings_DefaultValues_ShouldBeValid()
        {
            var settings = new BesoSettings();
            settings.Validate(); // should not throw
            Assert.Equal(0.5, settings.TargetVolumeFraction);
            Assert.Equal(0.02, settings.EvolutionaryRatio);
        }

        [Fact]
        public void BesoSettings_InvalidVolumeFraction_ShouldThrow()
        {
            var settings = new BesoSettings { TargetVolumeFraction = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());

            settings.TargetVolumeFraction = 1.0;
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Fact]
        public void BesoOptimizer_CantileverBeam_ShouldRemoveMaterial()
        {
            var ccxPath = ResolveCalculixExecutable();
            if (string.IsNullOrWhiteSpace(ccxPath) || !File.Exists(ccxPath))
            {
                _output.WriteLine("SKIPPED: CalculiX not found. Set CCX_EXE or install ccx.");
                return;
            }

            // Build a small 4x2x1 cantilever beam from C3D4 tetrahedra
            var model = CreateTetCantileverBeam();

            // Write the base input deck so the model has a valid Path
            var outputDir = CreateOutputDirectory();
            var builder = new CalculixInputDeckBuilder();
            var inpPath = Path.Combine(outputDir, "beso_base.inp");
            builder.WriteToFile(model, inpPath);
            model.Path = inpPath;

            _output.WriteLine($"Working directory: {outputDir}");
            _output.WriteLine($"CalculiX: {ccxPath}");
            _output.WriteLine($"Elements: {model.Elements.Count}");
            _output.WriteLine($"Nodes: {model.Nodes.Count}");

            // First, verify that a plain CalculiX run works on this model
            var (exitCode, stdOut, stdErr) = CalculixApplication.RunCalculix(ccxPath, inpPath, outputDir, 1);
            _output.WriteLine($"Baseline CalculiX exit code: {exitCode}");
            if (exitCode != 0)
            {
                _output.WriteLine($"StdOut: {stdOut}");
                _output.WriteLine($"StdErr: {stdErr}");
            }
            Assert.Equal(0, exitCode);

            // Run BESO optimization
            var settings = new BesoSettings
            {
                TargetVolumeFraction = 0.5,
                EvolutionaryRatio = 0.05,
                MaxIterations = 10,
                FilterRadius = 0.0,
                PenaltyFactor = 1e-3,
                ConvergenceTolerance = 1e-3
            };

            var optimizer = new BesoOptimizer(model, settings, ccxPath);
            optimizer.OnProgress = (iter, vf, compliance, msg) =>
            {
                _output.WriteLine(msg);
            };

            var result = optimizer.Run();

            // Output results
            _output.WriteLine("");
            _output.WriteLine("=== BESO OPTIMIZATION RESULTS ===");
            _output.WriteLine($"Converged: {result.Converged}");
            _output.WriteLine($"Iterations: {result.History.Count}");

            int solidCount = result.ElementStates.Values.Count(s => s);
            int voidCount = result.ElementStates.Values.Count(s => !s);
            int totalCount = result.ElementStates.Count;
            double finalDensity = (double)solidCount / totalCount;

            _output.WriteLine($"Total elements: {totalCount}");
            _output.WriteLine($"Solid (kept) elements: {solidCount}");
            _output.WriteLine($"Void (removed) elements: {voidCount}");
            _output.WriteLine($"Final density: {finalDensity:P1}");
            _output.WriteLine("");

            _output.WriteLine("Iteration history:");
            _output.WriteLine($"{"Iter",5} {"VolFrac",10} {"Compliance",15}");
            foreach (var h in result.History)
            {
                _output.WriteLine($"{h.Iteration,5} {h.VolumeFraction,10:F4} {h.Compliance,15:E4}");
            }

            _output.WriteLine("");
            _output.WriteLine("Element states (ID -> solid/void):");
            var sortedStates = result.ElementStates.OrderBy(kv => kv.Key);
            foreach (var kv in sortedStates)
            {
                _output.WriteLine($"  Element {kv.Key}: {(kv.Value ? "SOLID" : "VOID")}");
            }

            if (result.Sensitivities != null && result.Sensitivities.Count > 0)
            {
                _output.WriteLine("");
                _output.WriteLine("Final sensitivities:");
                var sortedSens = result.Sensitivities.OrderBy(kv => kv.Key);
                foreach (var kv in sortedSens)
                {
                    _output.WriteLine($"  Element {kv.Key}: {kv.Value:E4}");
                }
            }

            _output.WriteLine("");
            _output.WriteLine("=== FULL LOG ===");
            _output.WriteLine(result.Log);

            // Assertions
            Assert.NotEmpty(result.History);
            Assert.True(solidCount > 0, "At least some elements should remain solid.");
            Assert.True(voidCount > 0, "At least some elements should be removed.");
            Assert.True(finalDensity < 0.9, $"Final density {finalDensity:P1} should be below 90% (material should be removed).");
            Assert.True(finalDensity > 0.1, $"Final density {finalDensity:P1} should not go below 10% (something is wrong).");
        }

        /// <summary>
        /// Creates a simple cantilever beam discretised with C3D4 tetrahedra.
        /// Beam dimensions: 4 x 1 x 1, fixed at x=0, load at free end x=4.
        /// Grid: 5x2x2 nodes = 20 nodes, filled with tets.
        /// </summary>
        private static StructuralModel CreateTetCantileverBeam()
        {
            var model = new StructuralModel { Name = "BesoCantilever" };

            // Generate a regular grid of nodes
            int nx = 5, ny = 3, nz = 3;  // grid divisions + 1
            double lx = 4.0, ly = 1.0, lz = 1.0;
            double dx = lx / (nx - 1), dy = ly / (ny - 1), dz = lz / (nz - 1);

            var nodeGrid = new int[nx, ny, nz]; // maps grid indices to node IDs
            int nodeId = 1;

            for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
            {
                double x = ix * dx;
                double y = iy * dy;
                double z = iz * dz;
                model.Nodes.Add(new Node(nodeId, x, y, z));
                nodeGrid[ix, iy, iz] = nodeId;
                nodeId++;
            }

            // Generate C3D4 tetrahedra by splitting each hex cell into 6 tets
            int elemId = 1;
            for (int ix = 0; ix < nx - 1; ix++)
            for (int iy = 0; iy < ny - 1; iy++)
            for (int iz = 0; iz < nz - 1; iz++)
            {
                // 8 corner nodes of the hex cell
                int n0 = nodeGrid[ix, iy, iz];
                int n1 = nodeGrid[ix + 1, iy, iz];
                int n2 = nodeGrid[ix + 1, iy + 1, iz];
                int n3 = nodeGrid[ix, iy + 1, iz];
                int n4 = nodeGrid[ix, iy, iz + 1];
                int n5 = nodeGrid[ix + 1, iy, iz + 1];
                int n6 = nodeGrid[ix + 1, iy + 1, iz + 1];
                int n7 = nodeGrid[ix, iy + 1, iz + 1];

                // Split hex into 6 tets
                int[][] tets = SplitHexToTets(n0, n1, n2, n3, n4, n5, n6, n7);

                foreach (var tet in tets)
                {
                    model.Elements.Add(new Tetra4Element(elemId, "E_OPT", tet));
                    elemId++;
                }
            }

            // Material
            var steel = new IsotropicMaterial("MAT_STEEL")
            {
                YoungModulus = 210000.0,
                PoissonRatio = 0.3,
                Density = 7.85e-9 // tonnes/mm^3
            };
            model.Materials.Add(steel);
            model.Sections.Add(new SolidSection("E_OPT", steel));

            // Fixed support at x=0 face
            var fixedNodes = new List<int>();
            for (int iy = 0; iy < ny; iy++)
            for (int iz = 0; iz < nz; iz++)
                fixedNodes.Add(nodeGrid[0, iy, iz]);

            model.FixedSupports.Add(new FixedSupport(
                name: "FIX",
                nodeIds: fixedNodes,
                fixUx: true, fixUy: true, fixUz: true,
                fixRx: false, fixRy: false, fixRz: false));

            // Point load at the free end: downward in Z at the middle of the tip face
            int loadNode = nodeGrid[nx - 1, ny / 2, nz / 2];
            var step = new LinearStaticStep("Step-1");
            step.NodalLoads.Add(new NodalLoad(loadNode, StructuralDof.Uz, -1000.0));

            // Request energy density output for BESO
            step.OutputRequests.Add(StepOutputRequest.ElementPrint("E_OPT", ElementOutputVariable.ENER));
            step.OutputRequests.Add(StepOutputRequest.NodeFile(NodalOutputVariable.U));

            model.Steps.Add(step);

            return model;
        }

        /// <summary>
        /// Splits a hex cell into 6 tetrahedra.
        /// Hex nodes: n0-n3 = bottom face (CCW from outside), n4-n7 = top face.
        ///   n3--n2      n7--n6
        ///   |   |       |   |
        ///   n0--n1      n4--n5
        /// 6-tet decomposition that avoids inverted jacobians.
        /// </summary>
        private static int[][] SplitHexToTets(int n0, int n1, int n2, int n3, int n4, int n5, int n6, int n7)
        {
            return new[]
            {
                new[] { n0, n1, n3, n5 },
                new[] { n3, n5, n1, n2 },
                new[] { n2, n3, n5, n6 },
                new[] { n0, n3, n4, n5 },
                new[] { n3, n4, n5, n7 },
                new[] { n3, n5, n6, n7 },
            };
        }

        private static string ResolveCalculixExecutable()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CCX_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment;
            return CalculixApplication.FindCalculixExecutable();
        }

        private static string CreateOutputDirectory()
        {
            var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
            var outputDir = Path.Combine(
                root, "Llama.Test", "generated", "beso",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(outputDir);
            return outputDir;
        }

        private static string FindRepositoryRoot(string startDirectory)
        {
            var current = new DirectoryInfo(startDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Llama.sln")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate repository root containing Llama.sln.");
        }
    }
}
