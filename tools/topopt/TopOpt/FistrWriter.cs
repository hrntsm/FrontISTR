// FistrWriter.cs
// Writes FrontISTR input files for topology optimization.
//
// Each optimization iteration requires two file writes:
//   1. WriteCnt()      - rewrites the .cnt file with SIMP-scaled material constants
//   2. WriteHecmwCtrl() - writes hecmw_ctrl.dat so FrontISTR finds the right files

using System.Globalization;
using System.Text;

namespace TopOpt;

public static class FistrWriter
{
    /// <summary>
    /// Writes the .cnt file for the current iteration.
    /// Prepends per-element !MATERIAL/!ELASTIC definitions (SIMP-scaled E) to the
    /// base template that contains boundary conditions and solver settings.
    /// </summary>
    /// <param name="cntTemplatePath">Base .cnt file without material definitions.</param>
    /// <param name="outputCntPath">Destination .cnt file (overwritten each iteration).</param>
    /// <param name="elements">Element list (order matches rho[]).</param>
    /// <param name="rho">Current density array (one value per element).</param>
    /// <param name="E0">Base Young's modulus (fully-solid material).</param>
    /// <param name="Emin">Minimum Young's modulus (void material, prevents singularity).</param>
    /// <param name="nu">Poisson's ratio (constant throughout).</param>
    /// <param name="penal">SIMP penalization exponent p.</param>
    public static void WriteCnt(
        string cntTemplatePath,
        string outputCntPath,
        IReadOnlyList<ElementInfo> elements,
        double[] rho,
        double E0,
        double Emin,
        double nu,
        double penal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("!! Per-element material definitions (auto-generated, do not edit)");

        for (int i = 0; i < elements.Count; i++)
        {
            double E = Emin + Math.Pow(rho[i], penal) * (E0 - Emin);
            string groupName = $"e_{elements[i].Id}";
            sb.AppendLine($"!MATERIAL, NAME={groupName}");
            sb.AppendLine("!ELASTIC");
            sb.AppendLine(FormattableString.Invariant($" {E:E6}, {nu:F4}"));
        }

        sb.AppendLine();
        sb.Append(File.ReadAllText(cntTemplatePath));

        File.WriteAllText(outputCntPath, sb.ToString());
    }

    /// <summary>
    /// Writes hecmw_ctrl.dat in the working directory so FrontISTR can locate
    /// the mesh, control, and result files.
    /// </summary>
    /// <param name="workDir">Working directory where FrontISTR will be invoked.</param>
    /// <param name="mshBaseName">Mesh file name (without directory), e.g. "cantilever_work.msh".</param>
    /// <param name="cntBaseName">Control file name (without directory), e.g. "cantilever_work.cnt".</param>
    /// <param name="resPrefix">Result file prefix, e.g. "cantilever" → results in "cantilever.res.0.0".</param>
    public static void WriteHecmwCtrl(
        string workDir,
        string mshBaseName,
        string cntBaseName,
        string resPrefix)
    {
        var sb = new StringBuilder();
        sb.AppendLine("!MESH, NAME=fstrMSH, TYPE=HECMW-ENTIRE");
        sb.AppendLine($" {mshBaseName}");
        sb.AppendLine("!CONTROL, NAME=fstrCNT");
        sb.AppendLine($" {cntBaseName}");
        sb.AppendLine("!RESULT, NAME=fstrRES, IO=OUT");
        sb.AppendLine($" {resPrefix}.res");

        string ctrlPath = Path.Combine(workDir, "hecmw_ctrl.dat");
        File.WriteAllText(ctrlPath, sb.ToString());
    }
}
