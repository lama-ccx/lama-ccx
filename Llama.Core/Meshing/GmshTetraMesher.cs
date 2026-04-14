using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Llama.Core.Model;

namespace Llama.Core.Meshing
{
    /// <summary>
    /// Generates a Gmsh <c>.geo</c> script, runs the Gmsh executable,
    /// and parses the resulting <c>.msh</c> file into a <see cref="StructuralModel"/>.
    /// Supports STEP/IGES inputs (via OpenCASCADE) and STL inputs (built-in kernel).
    /// </summary>
    public static class GmshTetraMesher
    {
        private static readonly string[] MacPaths =
        {
            "/opt/homebrew/bin/gmsh",
            "/usr/local/bin/gmsh",
            "/Applications/Gmsh.app/Contents/MacOS/gmsh"
        };

        private static readonly string[] WindowsPaths =
        {
            @"C:\Program Files\Gmsh\gmsh.exe",
            @"C:\Program Files (x86)\Gmsh\gmsh.exe"
        };

        /// <summary>
        /// Attempts to locate the Gmsh executable on the system.
        /// </summary>
        public static string FindGmshExecutable()
        {
            string[] paths;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                paths = MacPaths;
            else
                paths = WindowsPaths;

            foreach (var path in paths)
            {
                if (File.Exists(path))
                    return path;
            }

            // Try searching PATH.
            var findCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "gmsh.exe" : "gmsh";

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = findCmd,
                        Arguments = exeName,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    return output.Trim().Split('\n')[0].Trim();
            }
            catch
            {
                // Swallow — not found via PATH.
            }

            return null;
        }

        /// <summary>
        /// Runs the full meshing pipeline: .geo generation → Gmsh execution → .msh parsing.
        /// </summary>
        /// <param name="geometryFilePath">
        /// Path to the geometry file (.step, .stp, .iges, .igs, .stl).
        /// The file must already exist on disk.
        /// </param>
        /// <param name="minSize">Minimum characteristic element length.</param>
        /// <param name="maxSize">Maximum characteristic element length.</param>
        /// <param name="options">Mesh generation options (sizes, optimization, etc.).</param>
        /// <param name="elementSetName">CalculiX element set name for the generated elements.</param>
        /// <param name="gmshExecutablePath">
        /// Path to the Gmsh executable. If <c>null</c>, the system is searched automatically.
        /// </param>
        /// <param name="gmshLog">Combined Gmsh stdout + stderr output for diagnostics.</param>
        /// <returns>A <see cref="StructuralModel"/> containing the tetrahedral mesh.</returns>
        public static StructuralModel Mesh(
            string geometryFilePath,
            GmshMeshOptions options,
            string elementSetName,
            string gmshExecutablePath,
            out string gmshLog)
        {
            gmshLog = string.Empty;
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(geometryFilePath))
                throw new ArgumentException("Geometry file path cannot be empty.", nameof(geometryFilePath));
            if (!File.Exists(geometryFilePath))
                throw new FileNotFoundException("Geometry file not found.", geometryFilePath);
            if (options.MinSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Min size must be positive.");
            if (options.MaxSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Max size must be positive.");
            if (options.MaxSize < options.MinSize)
                throw new ArgumentException("Max size must be >= min size.");
            if (options.ElementOrder != 1 && options.ElementOrder != 2)
                throw new ArgumentOutOfRangeException(nameof(options), "Element order must be 1 or 2.");

            var gmshPath = gmshExecutablePath ?? FindGmshExecutable();
            if (string.IsNullOrWhiteSpace(gmshPath) || !File.Exists(gmshPath))
                throw new FileNotFoundException(
                    "Gmsh executable not found. Install Gmsh or provide the path explicitly.");

            var workDir = Path.GetDirectoryName(geometryFilePath)
                          ?? throw new ArgumentException("Cannot determine working directory from geometry path.");

            var geometryFileName = Path.GetFileName(geometryFilePath);
            var geoFilePath = Path.Combine(workDir, "llama_mesh.geo");
            var mshFilePath = Path.Combine(workDir, "llama_mesh.msh");

            WriteGeoScript(geoFilePath, geometryFileName, options);

            var (exitCode, stdout, stderr) = RunGmsh(gmshPath, geoFilePath, mshFilePath);

            gmshLog = string.IsNullOrEmpty(stderr)
                ? stdout
                : stdout + "\n--- STDERR ---\n" + stderr;

            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Gmsh exited with code {exitCode}.\nStdout: {stdout}\nStderr: {stderr}");

            if (!File.Exists(mshFilePath))
                throw new FileNotFoundException(
                    $"Gmsh did not produce the expected output file: {mshFilePath}");

            return GmshMshParser.Parse(mshFilePath, elementSetName);
        }

        /// <summary>
        /// Writes a Gmsh <c>.geo</c> script for the given geometry file.
        /// Uses the OpenCASCADE kernel for STEP/IGES files, and the built-in
        /// kernel with <c>CreateTopology</c> for STL files.
        /// </summary>
        internal static void WriteGeoScript(
            string geoFilePath,
            string geometryFileName,
            GmshMeshOptions opts)
        {
            var ext = Path.GetExtension(geometryFileName).ToLowerInvariant();
            var isSTL = ext == ".stl";

            using (var writer = new StreamWriter(geoFilePath))
            {
                writer.WriteLine("// Auto-generated by Llama");

                if (isSTL)
                {
                    // STL produces discrete entities — use built-in kernel.
                    // ClassifySurfaces reparametrizes the surface (3rd arg = 1)
                    // so that Gmsh can remesh it instead of keeping the STL triangulation.
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Merge \"{0}\";", geometryFileName));
                    writer.WriteLine("ClassifySurfaces{Pi/4, 1, 1};");
                    writer.WriteLine("CreateTopology;");
                    writer.WriteLine("Surface Loop(1) = Surface{:};");
                    writer.WriteLine("Volume(1) = {1};");
                    // Prevent inheriting element sizes from STL vertex spacing.
                    writer.WriteLine("Mesh.CharacteristicLengthFromPoints = 0;");
                    writer.WriteLine("Mesh.CharacteristicLengthExtendFromBoundary = 0;");
                }
                else
                {
                    // CAD files (STEP/IGES/BREP) — use OpenCASCADE kernel.
                    writer.WriteLine("SetFactory(\"OpenCASCADE\");");
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Merge \"{0}\";", geometryFileName));
                    if (opts.MeshSizeFromCurvature > 0)
                    {
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Mesh.MeshSizeFromCurvature = {0};", opts.MeshSizeFromCurvature));
                    }
                }

                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.CharacteristicLengthMin = {0};", opts.MinSize));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.CharacteristicLengthMax = {0};", opts.MaxSize));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.ElementOrder = {0};", opts.ElementOrder));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.Algorithm3D = {0};", opts.Algorithm3D));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.Optimize = {0};", opts.Optimize));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.OptimizeNetgen = {0};", opts.OptimizeNetgen));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.OptimizeThreshold = {0};", opts.OptimizeThreshold));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.Smoothing = {0};", opts.Smoothing));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.HighOrderOptimize = {0};",
                    opts.ElementOrder == 1 ? 0 : opts.HighOrderOptimize));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.QualityType = {0};", opts.QualityType));
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Mesh.AnisoMax = {0};", opts.AnisoMax));

                // --- Refinement fields (Distance + Threshold per zone, combined via Min) ---
                if (opts.RefinementZones != null && opts.RefinementZones.Count > 0)
                {
                    var thresholdIds = new List<int>();
                    int fieldId = 1;

                    for (int i = 0; i < opts.RefinementZones.Count; i++)
                    {
                        var zone = opts.RefinementZones[i];
                        int distId = fieldId++;
                        int threshId = fieldId++;

                        var ptVar = string.Format("refPt{0}", i + 1);
                        writer.WriteLine();
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "{0} = newp;", ptVar));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Point({0}) = {{{1}, {2}, {3}}};", ptVar, zone.X, zone.Y, zone.Z));

                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}] = Distance;", distId));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}].PointsList = {{{1}}};", distId, ptVar));

                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}] = Threshold;", threshId));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}].InField = {1};", threshId, distId));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}].SizeMin = {1};", threshId, zone.SizeMin));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}].SizeMax = {1};", threshId, zone.SizeMax));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}].DistMin = {1};", threshId, zone.DistMin));
                        writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "Field[{0}].DistMax = {1};", threshId, zone.DistMax));

                        thresholdIds.Add(threshId);
                    }

                    int minFieldId = fieldId;
                    writer.WriteLine();
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Field[{0}] = Min;", minFieldId));
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Field[{0}].FieldsList = {{{1}}};", minFieldId,
                        string.Join(", ", thresholdIds)));
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "Background Field = {0};", minFieldId));
                }
            }
        }

        private static (int ExitCode, string Stdout, string Stderr) RunGmsh(
            string gmshPath,
            string geoFilePath,
            string mshFilePath)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = gmshPath,
                    Arguments = string.Format(
                        CultureInfo.InvariantCulture,
                        "\"{0}\" -3 -format msh2 -o \"{1}\"",
                        geoFilePath, mshFilePath),
                    WorkingDirectory = Path.GetDirectoryName(geoFilePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();

            var outTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var errTask = Task.Run(() => process.StandardError.ReadToEnd());
            process.WaitForExit();
            Task.WaitAll(outTask, errTask);

            return (process.ExitCode, outTask.Result, errTask.Result);
        }
    }
}
