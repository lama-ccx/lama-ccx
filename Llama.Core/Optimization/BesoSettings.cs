using System;

namespace Llama.Core.Optimization
{
    /// <summary>
    /// Parameters controlling the BESO (Bi-directional Evolutionary Structural Optimization) algorithm.
    /// </summary>
    public sealed class BesoSettings
    {
        /// <summary>Target volume fraction (0..1). E.g., 0.5 means retain 50% of the original volume.</summary>
        public double TargetVolumeFraction { get; set; } = 0.5;

        /// <summary>Evolutionary ratio: fraction of volume removed per iteration.</summary>
        public double EvolutionaryRatio { get; set; } = 0.02;

        /// <summary>Filter radius for sensitivity smoothing, in model units. 0 disables filtering.</summary>
        public double FilterRadius { get; set; } = 0.0;

        /// <summary>Penalty factor for void elements. Young's modulus is multiplied by this value (e.g. 1e-3).</summary>
        public double PenaltyFactor { get; set; } = 1e-3;

        /// <summary>Maximum number of BESO iterations.</summary>
        public int MaxIterations { get; set; } = 50;

        /// <summary>Convergence tolerance on the relative change in compliance over the last 5 iterations.</summary>
        public double ConvergenceTolerance { get; set; } = 1e-3;

        public void Validate()
        {
            if (TargetVolumeFraction <= 0.0 || TargetVolumeFraction >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(TargetVolumeFraction), "Must be between 0 and 1 (exclusive).");
            if (EvolutionaryRatio <= 0.0 || EvolutionaryRatio >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(EvolutionaryRatio), "Must be between 0 and 1 (exclusive).");
            if (PenaltyFactor <= 0.0 || PenaltyFactor >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(PenaltyFactor), "Must be between 0 and 1 (exclusive).");
            if (MaxIterations < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxIterations), "Must be at least 1.");
            if (ConvergenceTolerance <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(ConvergenceTolerance), "Must be positive.");
        }
    }
}
