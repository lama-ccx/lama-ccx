using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Llama.Core.Application;
using Llama.Core.InputDeck;
using Llama.Core.Materials;
using Llama.Core.Model;
using Llama.Core.Model.Elements;
using Llama.Core.Model.Sections;
using Llama.Core.Model.Steps;
using Llama.Core.PostProcessing;

namespace Llama.Core.Optimization
{
    /// <summary>
    /// Stiffness-based BESO topology optimization using CalculiX as the FEA solver.
    /// </summary>
    public sealed class BesoOptimizer
    {
        private readonly StructuralModel _model;
        private readonly BesoSettings _settings;
        private readonly string _ccxPath;
        private readonly StringBuilder _log = new StringBuilder();

        public BesoOptimizer(StructuralModel model, BesoSettings settings, string ccxExecutablePath)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ccxPath = ccxExecutablePath ?? throw new ArgumentNullException(nameof(ccxExecutablePath));
            _settings.Validate();
        }

        /// <summary>
        /// Progress callback: (iterationNumber, volumeFraction, compliance, logLine).
        /// </summary>
        public Action<int, double, double, string> OnProgress { get; set; }

        /// <summary>
        /// Runs the BESO optimization loop.
        /// </summary>
        public BesoResult Run()
        {
            _model.Validate();
            _model.EnsureHasAnalysisSteps();

            var workDir = ResolveWorkingDirectory();
            Directory.CreateDirectory(workDir);

            // Build element data: volumes, centroids, neighbour lists
            var nodeMap = _model.Nodes.ToDictionary(n => n.Id);
            var elements = _model.Elements.ToList();
            var elementIds = elements.Select(e => e.Id).ToList();
            var volumes = ComputeElementVolumes(elements, nodeMap);
            var centroids = ComputeElementCentroids(elements, nodeMap);
            var neighbours = _settings.FilterRadius > 0
                ? BuildNeighbourMap(elementIds, centroids, _settings.FilterRadius)
                : null;

            double totalVolume = volumes.Values.Sum();

            // Initialise: all elements are solid (state = true)
            var states = elementIds.ToDictionary(id => id, _ => true);
            var sensitivitiesOld = new Dictionary<int, double>();
            var history = new List<BesoIterationResult>();

            // Find the original material's Young's modulus for computing the void material
            double originalE = GetReferenceYoungModulus();
            if (originalE <= 0)
            {
                Log("ERROR: Could not determine a positive Young's modulus from the model materials.");
                return BuildResult(states, new Dictionary<int, double>(), history, false);
            }

            double currentVolumeFraction = 1.0;

            for (int iter = 0; iter < _settings.MaxIterations; iter++)
            {
                // 1. Write the modified inp file
                string iterInpPath = Path.Combine(workDir, $"beso_iter{iter:D3}.inp");
                WriteIterationInpFile(iterInpPath, states, originalE);

                // 2. Run CalculiX
                Log($"Iteration {iter}: running CalculiX...");
                var (exitCode, stdOut, stdErr) = CalculixApplication.RunCalculix(_ccxPath, iterInpPath, workDir);
                if (exitCode != 0)
                {
                    Log($"ERROR: CalculiX exited with code {exitCode}. StdErr: {stdErr}");
                    return BuildResult(states, sensitivitiesOld, history, false);
                }

                // 3. Parse energy density from .dat
                string datPath = Path.ChangeExtension(iterInpPath, ".dat");
                if (!File.Exists(datPath))
                {
                    Log("ERROR: .dat file not found after CalculiX run.");
                    return BuildResult(states, sensitivitiesOld, history, false);
                }

                var energyDensities = ParseEnergyDensities(datPath, elementIds);
                if (energyDensities.Count == 0)
                {
                    Log("ERROR: No energy density results found in .dat file.");
                    return BuildResult(states, sensitivitiesOld, history, false);
                }

                // 4. Compute sensitivity numbers
                //    For stiffness optimisation: sensitivity = strain energy density
                //    For void elements, scale by penalty to avoid bias
                var sensitivities = new Dictionary<int, double>(elementIds.Count);
                foreach (var id in elementIds)
                {
                    double ener = energyDensities.ContainsKey(id) ? energyDensities[id] : 0.0;
                    sensitivities[id] = ener;
                }

                // 5. Apply spatial filter (distance-weighted averaging)
                if (neighbours != null)
                    sensitivities = ApplyFilter(sensitivities, neighbours, centroids);

                // 6. Average with previous iteration for stability
                if (iter > 0 && sensitivitiesOld.Count > 0)
                {
                    foreach (var id in elementIds)
                    {
                        if (sensitivitiesOld.ContainsKey(id))
                            sensitivities[id] = (sensitivities[id] + sensitivitiesOld[id]) / 2.0;
                    }
                }
                sensitivitiesOld = new Dictionary<int, double>(sensitivities);

                // 7. Compute compliance = sum of element strain energy = sum(sensitivity * volume)
                double compliance = 0;
                foreach (var id in elementIds)
                {
                    if (states[id])
                        compliance += sensitivities[id] * volumes[id];
                }

                // 8. Determine target volume for this iteration
                double targetVf = Math.Max(
                    _settings.TargetVolumeFraction,
                    currentVolumeFraction - _settings.EvolutionaryRatio);

                // 9. Switch element states using BESO hard-kill approach
                states = SwitchStates(elementIds, sensitivities, volumes, totalVolume, targetVf);

                currentVolumeFraction = elementIds.Where(id => states[id]).Sum(id => volumes[id]) / totalVolume;

                var iterResult = new BesoIterationResult
                {
                    Iteration = iter,
                    VolumeFraction = currentVolumeFraction,
                    Compliance = compliance
                };
                history.Add(iterResult);

                string msg = $"Iteration {iter}: VF={currentVolumeFraction:F4}, Compliance={compliance:E4}";
                Log(msg);
                OnProgress?.Invoke(iter, currentVolumeFraction, compliance, msg);

                // 10. Check convergence: relative change in compliance over last 5 iterations
                if (history.Count >= 6 && currentVolumeFraction <= _settings.TargetVolumeFraction + 0.001)
                {
                    double maxDiff = 0;
                    for (int k = 1; k <= 5; k++)
                    {
                        double c1 = history[history.Count - 1].Compliance;
                        double c2 = history[history.Count - 1 - k].Compliance;
                        if (Math.Abs(c1) > 1e-30)
                            maxDiff = Math.Max(maxDiff, Math.Abs(c1 - c2) / Math.Abs(c1));
                    }

                    if (maxDiff < _settings.ConvergenceTolerance)
                    {
                        Log($"Converged at iteration {iter} (maxDiff={maxDiff:E4}).");
                        return BuildResult(states, sensitivities, history, true);
                    }
                }
            }

            Log("Reached maximum iterations without convergence.");
            return BuildResult(states, sensitivitiesOld, history, false);
        }

        // ── Inp file generation ──

        private void WriteIterationInpFile(string outputPath, Dictionary<int, bool> states, double originalE)
        {
            // Build a fresh inp from the model using CalculixInputDeckBuilder
            var builder = new CalculixInputDeckBuilder();
            string baseInp = builder.Build(_model);

            // Find solid and void element IDs
            var solidIds = new List<int>();
            var voidIds = new List<int>();
            foreach (var kv in states)
            {
                if (kv.Value)
                    solidIds.Add(kv.Key);
                else
                    voidIds.Add(kv.Key);
            }

            // Post-process the inp text to:
            //  1. Add ELSET for void elements
            //  2. Add void material (very low E)
            //  3. Add solid section for void elements
            //  4. Add ENER output request
            var sb = new StringBuilder();
            var lines = baseInp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool addedBesoCards = false;
            bool hasEnerOutput = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string upper = line.TrimStart().ToUpperInvariant();

                // Check if ENER is already requested
                if (upper == "ENER" || upper.Contains(",ENER") || upper.StartsWith("ENER,"))
                    hasEnerOutput = true;

                // Insert BESO cards just before the first *STEP keyword
                if (!addedBesoCards && upper.StartsWith("*STEP"))
                {
                    AppendBesoCards(sb, solidIds, voidIds, originalE);
                    addedBesoCards = true;
                }

                // For each *EL PRINT line, check if we need to add ENER
                if (upper.StartsWith("*EL PRINT") || upper.StartsWith("*EL FILE"))
                {
                    sb.AppendLine(line);
                    // Read the next line (variables)
                    if (i + 1 < lines.Length)
                    {
                        i++;
                        string varLine = lines[i];
                        if (!varLine.ToUpperInvariant().Contains("ENER"))
                        {
                            // Add ENER to the variable list
                            sb.AppendLine(varLine.TrimEnd() + (varLine.TrimEnd().EndsWith(",") ? "" : ",") + "ENER");
                        }
                        else
                        {
                            sb.AppendLine(varLine);
                        }
                        hasEnerOutput = true;
                    }
                    continue;
                }

                // Before *END STEP, add ENER output if not already present
                if (upper.StartsWith("*END STEP") && !hasEnerOutput)
                {
                    sb.AppendLine("*EL PRINT,ELSET=EALL");
                    sb.AppendLine("ENER");
                    hasEnerOutput = true;
                }

                sb.AppendLine(line);
            }

            // If BESO cards were never added (no *STEP found), add them at the end
            if (!addedBesoCards)
            {
                AppendBesoCards(sb, solidIds, voidIds, originalE);
            }

            File.WriteAllText(outputPath, sb.ToString());
        }

        private void AppendBesoCards(StringBuilder sb, List<int> solidIds, List<int> voidIds, double originalE)
        {
            if (voidIds.Count == 0)
                return;

            // Write ELSET for void elements
            sb.AppendLine("**");
            sb.AppendLine("** BESO void element set");
            sb.AppendLine("*ELSET,ELSET=BESO_VOID");
            WriteIntegerList(sb, voidIds.OrderBy(id => id));

            // Write void material
            double voidE = originalE * _settings.PenaltyFactor;
            double voidNu = GetReferencePoissonRatio();
            double voidRho = GetReferenceDensity() * _settings.PenaltyFactor;

            sb.AppendLine(FormattableString.Invariant($"*MATERIAL,NAME=BESO_VOID_MAT"));
            sb.AppendLine("*ELASTIC");
            sb.AppendLine(FormattableString.Invariant($"{voidE},{voidNu}"));
            sb.AppendLine("*DENSITY");
            sb.AppendLine(FormattableString.Invariant($"{voidRho}"));

            // Write solid section for void elements (overrides the original section assignment)
            sb.AppendLine("*SOLID SECTION,ELSET=BESO_VOID,MATERIAL=BESO_VOID_MAT");
            sb.AppendLine("**");
        }

        // ── Energy density parsing ──

        private static Dictionary<int, double> ParseEnergyDensities(string datPath, List<int> elementIds)
        {
            var tables = CalculixDatParser.ParseFile(datPath);
            var energyTables = CalculixDatParser.FindTablesByHeaderKeyword(tables, "energy");

            var result = new Dictionary<int, double>();

            foreach (var table in energyTables)
            {
                // Group rows by element ID and average the energy density across integration points
                var groups = table.Rows.GroupBy(r => r.EntityId);
                foreach (var group in groups)
                {
                    int elementId = group.Key;
                    // In CalculiX ENER output: columns are (integration point, energy density)
                    // The last value in each row is the energy density
                    double avgEnergy = group.Average(r => r.Values.Count > 0 ? r.Values[r.Values.Count - 1] : 0.0);
                    if (avgEnergy < 0) avgEnergy = 0; // energy density should be non-negative
                    result[elementId] = avgEnergy;
                }
            }

            return result;
        }

        // ── Spatial filter ──

        private static Dictionary<int, double> ApplyFilter(
            Dictionary<int, double> sensitivities,
            Dictionary<int, List<(int neighbourId, double weight)>> neighbours,
            Dictionary<int, double[]> centroids)
        {
            var filtered = new Dictionary<int, double>(sensitivities.Count);

            foreach (var kv in sensitivities)
            {
                int id = kv.Key;
                if (!neighbours.ContainsKey(id) || neighbours[id].Count == 0)
                {
                    filtered[id] = kv.Value;
                    continue;
                }

                double weightedSum = 0;
                double weightSum = 0;
                foreach (var (neighbourId, weight) in neighbours[id])
                {
                    if (sensitivities.ContainsKey(neighbourId))
                    {
                        weightedSum += weight * sensitivities[neighbourId];
                        weightSum += weight;
                    }
                }

                filtered[id] = weightSum > 0 ? weightedSum / weightSum : kv.Value;
            }

            return filtered;
        }

        private static Dictionary<int, List<(int, double)>> BuildNeighbourMap(
            List<int> elementIds,
            Dictionary<int, double[]> centroids,
            double filterRadius)
        {
            var map = new Dictionary<int, List<(int, double)>>();

            foreach (var id in elementIds)
                map[id] = new List<(int, double)>();

            // O(n^2) brute force — acceptable for moderate meshes
            for (int i = 0; i < elementIds.Count; i++)
            {
                int idA = elementIds[i];
                var cgA = centroids[idA];

                for (int j = i + 1; j < elementIds.Count; j++)
                {
                    int idB = elementIds[j];
                    var cgB = centroids[idB];

                    double dist = Math.Sqrt(
                        (cgA[0] - cgB[0]) * (cgA[0] - cgB[0]) +
                        (cgA[1] - cgB[1]) * (cgA[1] - cgB[1]) +
                        (cgA[2] - cgB[2]) * (cgA[2] - cgB[2]));

                    if (dist < filterRadius)
                    {
                        double w = filterRadius - dist;
                        map[idA].Add((idB, w));
                        map[idB].Add((idA, w));
                    }
                }
            }

            return map;
        }

        // ── BESO state switching ──

        private static Dictionary<int, bool> SwitchStates(
            List<int> elementIds,
            Dictionary<int, double> sensitivities,
            Dictionary<int, double> volumes,
            double totalVolume,
            double targetVolumeFraction)
        {
            double targetVolume = targetVolumeFraction * totalVolume;

            // Sort elements by sensitivity (ascending → least sensitive first to be removed)
            var sorted = elementIds
                .OrderBy(id => sensitivities.ContainsKey(id) ? sensitivities[id] : 0.0)
                .ToList();

            // Greedily add elements from highest sensitivity downward until target volume is reached
            var newStates = elementIds.ToDictionary(id => id, _ => false);
            double currentVolume = 0;

            // Walk from highest sensitivity to lowest
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                int id = sorted[i];
                if (currentVolume + volumes[id] <= targetVolume * 1.001) // small tolerance
                {
                    newStates[id] = true;
                    currentVolume += volumes[id];
                }
            }

            return newStates;
        }

        // ── Geometry helpers ──

        private static Dictionary<int, double> ComputeElementVolumes(
            IReadOnlyList<IElement> elements,
            Dictionary<int, Node> nodeMap)
        {
            var volumes = new Dictionary<int, double>();
            foreach (var elem in elements)
            {
                volumes[elem.Id] = ComputeTetVolume(elem, nodeMap);
            }
            return volumes;
        }

        private static double ComputeTetVolume(IElement element, Dictionary<int, Node> nodeMap)
        {
            var ids = element.NodeIds;
            if (ids.Count < 4)
                return 0.0;

            // Use first 4 nodes for tet volume (works for C3D4 and first 4 of C3D10)
            if (!nodeMap.TryGetValue(ids[0], out var n0) ||
                !nodeMap.TryGetValue(ids[1], out var n1) ||
                !nodeMap.TryGetValue(ids[2], out var n2) ||
                !nodeMap.TryGetValue(ids[3], out var n3))
                return 0.0;

            // For hexa elements, split into 6 tets and sum
            if (ids.Count == 8 || ids.Count == 20)
                return ComputeHexVolume(element, nodeMap);

            // Tet volume = |det([n1-n0, n2-n0, n3-n0])| / 6
            double ux = n1.X - n0.X, uy = n1.Y - n0.Y, uz = n1.Z - n0.Z;
            double vx = n2.X - n0.X, vy = n2.Y - n0.Y, vz = n2.Z - n0.Z;
            double wx = n3.X - n0.X, wy = n3.Y - n0.Y, wz = n3.Z - n0.Z;

            double det = ux * (vy * wz - vz * wy)
                       - uy * (vx * wz - vz * wx)
                       + uz * (vx * wy - vy * wx);

            return Math.Abs(det) / 6.0;
        }

        private static double ComputeHexVolume(IElement element, Dictionary<int, Node> nodeMap)
        {
            var ids = element.NodeIds;
            // Split hex into 6 tets
            int[][] tetSplits = new[]
            {
                new[] { 0, 1, 2, 5 },
                new[] { 0, 2, 4, 5 },
                new[] { 2, 4, 5, 6 },
                new[] { 0, 2, 3, 4 },
                new[] { 3, 4, 6, 7 },
                new[] { 2, 3, 4, 6 }
            };

            double total = 0;
            foreach (var split in tetSplits)
            {
                if (!nodeMap.TryGetValue(ids[split[0]], out var a) ||
                    !nodeMap.TryGetValue(ids[split[1]], out var b) ||
                    !nodeMap.TryGetValue(ids[split[2]], out var c) ||
                    !nodeMap.TryGetValue(ids[split[3]], out var d))
                    continue;

                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                double wx = d.X - a.X, wy = d.Y - a.Y, wz = d.Z - a.Z;

                double det = ux * (vy * wz - vz * wy)
                           - uy * (vx * wz - vz * wx)
                           + uz * (vx * wy - vy * wx);
                total += Math.Abs(det) / 6.0;
            }
            return total;
        }

        private static Dictionary<int, double[]> ComputeElementCentroids(
            IReadOnlyList<IElement> elements,
            Dictionary<int, Node> nodeMap)
        {
            var centroids = new Dictionary<int, double[]>();
            foreach (var elem in elements)
            {
                double x = 0, y = 0, z = 0;
                int count = 0;
                foreach (var nid in elem.NodeIds)
                {
                    if (nodeMap.TryGetValue(nid, out var n))
                    {
                        x += n.X; y += n.Y; z += n.Z;
                        count++;
                    }
                }
                if (count > 0)
                    centroids[elem.Id] = new[] { x / count, y / count, z / count };
                else
                    centroids[elem.Id] = new[] { 0.0, 0.0, 0.0 };
            }
            return centroids;
        }

        // ── Material helpers ──

        private double GetReferenceYoungModulus()
        {
            foreach (var mat in _model.Materials)
            {
                if (mat is IsotropicMaterial iso && iso.YoungModulus > 0)
                    return iso.YoungModulus;
            }
            return 0.0;
        }

        private double GetReferencePoissonRatio()
        {
            foreach (var mat in _model.Materials)
            {
                if (mat is IsotropicMaterial iso)
                    return iso.PoissonRatio;
            }
            return 0.3;
        }

        private double GetReferenceDensity()
        {
            foreach (var mat in _model.Materials)
            {
                if (mat.Density > 0)
                    return mat.Density;
            }
            return 1.0;
        }

        // ── Directory / logging ──

        private string ResolveWorkingDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_model.Path))
            {
                var dir = Path.GetDirectoryName(_model.Path);
                if (!string.IsNullOrWhiteSpace(dir))
                    return Path.Combine(dir, "beso_work");
            }
            return Path.Combine(Path.GetTempPath(), "llama_beso");
        }

        private void Log(string message)
        {
            _log.AppendLine(message);
        }

        private static void WriteIntegerList(StringBuilder sb, IEnumerable<int> values, int perLine = 16)
        {
            var buffer = new List<int>(perLine);
            foreach (var value in values)
            {
                buffer.Add(value);
                if (buffer.Count < perLine)
                    continue;
                sb.AppendLine(string.Join(",", buffer) + ",");
                buffer.Clear();
            }
            if (buffer.Count > 0)
                sb.AppendLine(string.Join(",", buffer) + ",");
        }

        private BesoResult BuildResult(
            Dictionary<int, bool> states,
            Dictionary<int, double> sensitivities,
            List<BesoIterationResult> history,
            bool converged)
        {
            return new BesoResult
            {
                ElementStates = states,
                Sensitivities = sensitivities,
                History = history,
                Converged = converged,
                Log = _log.ToString()
            };
        }
    }
}
