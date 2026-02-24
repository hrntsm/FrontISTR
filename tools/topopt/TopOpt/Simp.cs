// Simp.cs
// SIMP (Solid Isotropic Material with Penalization) topology optimization core.
//
// For compliance minimization C = F^T u, the sensitivity is self-adjoint:
//   dC/dρ_e = -p * ρ_e^(p-1) * u_e^T * K0_e * u_e
//           = -p * (σ_e:ε_e * V_e / 2) / ρ_e * (1/ρ_e^(p-1))  [using K_e = ρ_e^p * K0_e]
//           = -p * W_e / ρ_e
// where W_e = 0.5 * (σ_e:ε_e) * V_e is the element strain energy from the forward solve.
// No adjoint solve is needed.

namespace TopOpt;

public static class Simp
{
    /// <summary>
    /// Computes sensitivity of compliance with respect to element density.
    /// s_e = -p * W_e / rho_e, where W_e = 0.5 * (σ:ε) * V_e.
    ///
    /// Stress/strain components in Voigt notation (engineering shear strains):
    ///   index 0=XX, 1=YY, 2=ZZ, 3=XY, 4=YZ, 5=ZX
    /// Inner product σ:ε = Σ_c σ_c * ε_c (engineering shear strains already absorbed).
    /// </summary>
    public static double[] ComputeSensitivity(
        double[,] stress,   // [ne, 6]
        double[,] strain,   // [ne, 6]
        double[] volume,    // [ne]
        double[] rho,       // [ne]
        double penal)
    {
        int ne = rho.Length;
        var sensitivity = new double[ne];

        for (int e = 0; e < ne; e++)
        {
            double innerProduct = 0;
            for (int c = 0; c < 6; c++)
                innerProduct += stress[e, c] * strain[e, c];

            double strainEnergy = 0.5 * innerProduct * volume[e]; // W_e
            sensitivity[e] = -penal * strainEnergy / Math.Max(rho[e], 1e-12);
        }

        return sensitivity;
    }

    /// <summary>
    /// Applies a sensitivity filter to reduce checkerboard patterns.
    /// Filtered sensitivity: sf_e = (Σ_f w(e,f)*s_f) / (Σ_f w(e,f))
    /// where w(e,f) = max(0, rMin - dist(centroid_e, centroid_f)).
    ///
    /// O(ne^2) - suitable for small/medium problems.
    /// For large problems, a neighbor search (k-d tree) would be needed.
    /// </summary>
    public static double[] ApplyFilter(
        double[] sensitivity,   // [ne]
        double[,] centroids,    // [ne, 3]
        double rMin)
    {
        int ne = sensitivity.Length;
        var filtered = new double[ne];

        for (int e = 0; e < ne; e++)
        {
            double wSum = 0, wsSum = 0;
            double ex = centroids[e, 0], ey = centroids[e, 1], ez = centroids[e, 2];

            for (int f = 0; f < ne; f++)
            {
                double dx = ex - centroids[f, 0];
                double dy = ey - centroids[f, 1];
                double dz = ez - centroids[f, 2];
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double w = Math.Max(0.0, rMin - dist);
                wSum += w;
                wsSum += w * sensitivity[f];
            }

            filtered[e] = wSum > 0 ? wsSum / wSum : sensitivity[e];
        }

        return filtered;
    }

    /// <summary>
    /// Updates element densities using the Optimality Criteria (OC) method.
    /// Bisection is used to find the Lagrange multiplier λ that satisfies
    /// the volume constraint: Σ rho_e * V_e = volFrac * Σ V_e.
    ///
    /// Update rule: rho_e_new = clip(rho_e * sqrt(-s_e / (λ * V_e)), move)
    /// where clip(x, move) = max(ρmin, min(1, max(ρ_e-move, min(ρ_e+move, x)))).
    /// </summary>
    public static double[] OcUpdate(
        double[] rho,           // [ne] current densities
        double[] sensitivity,   // [ne] filtered sensitivity
        double[] volume,        // [ne]
        double volFrac,         // target volume fraction
        double move = 0.2,      // max density change per iteration
        double eta = 0.5,       // damping exponent
        double rhoMin = 0.001)  // minimum density (void)
    {
        int ne = rho.Length;
        double totalVol = 0;
        for (int e = 0; e < ne; e++) totalVol += volume[e];
        double targetVol = volFrac * totalVol;

        var rhoNew = new double[ne];

        // Bisection on Lagrange multiplier λ
        double lo = 0.0, hi = 1e9;
        while (hi - lo > 1e-9 * (1.0 + lo))
        {
            double mid = 0.5 * (lo + hi);

            double vol = 0;
            for (int e = 0; e < ne; e++)
            {
                // Optimality condition: ρ * B_e^eta where B_e = -s_e / (λ * V_e)
                double bFrac = -sensitivity[e] / (mid * volume[e]);
                bFrac = Math.Max(0.0, bFrac); // guard against positive sensitivity
                double candidate = rho[e] * Math.Pow(bFrac, eta);
                // Apply move limit and density bounds
                rhoNew[e] = Math.Max(rhoMin,
                             Math.Min(1.0,
                             Math.Min(rho[e] + move,
                             Math.Max(rho[e] - move, candidate))));
                vol += rhoNew[e] * volume[e];
            }

            if (vol > targetVol) lo = mid;
            else hi = mid;
        }

        return rhoNew;
    }
}
