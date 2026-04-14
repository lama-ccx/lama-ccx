using System.Collections.Generic;

namespace Llama.Core.Optimization
{
    /// <summary>
    /// Result of a single BESO iteration.
    /// </summary>
    public sealed class BesoIterationResult
    {
        public int Iteration { get; set; }
        public double VolumeFraction { get; set; }
        public double Compliance { get; set; }
    }

    /// <summary>
    /// Full result of the BESO optimization run.
    /// </summary>
    public sealed class BesoResult
    {
        /// <summary>Element states: true = solid (keep), false = void (removed).</summary>
        public IReadOnlyDictionary<int, bool> ElementStates { get; set; }

        /// <summary>Sensitivity number per element from the final iteration.</summary>
        public IReadOnlyDictionary<int, double> Sensitivities { get; set; }

        /// <summary>History of each iteration.</summary>
        public IReadOnlyList<BesoIterationResult> History { get; set; }

        /// <summary>Whether the optimization converged within tolerance.</summary>
        public bool Converged { get; set; }

        /// <summary>Log messages generated during the optimization.</summary>
        public string Log { get; set; } = string.Empty;
    }
}
