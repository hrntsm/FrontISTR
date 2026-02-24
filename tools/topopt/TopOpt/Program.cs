// Program.cs
// SIMP topology optimization loop using FrontISTR as the FEM solver.
//
// Usage:
//   TopOpt --mesh <mesh.msh> --cnt <base.cnt> [options]
//   TopOpt --gen-mesh <nx> <ny> [--mesh out.msh] [--cnt out_base.cnt] [options]
//
// Options:
//   --mesh <path>       Preprocessed .msh file (per-element groups)
//   --cnt <path>        Base .cnt template (boundary conditions + solver, no materials)
//   --fistr <path>      Path to FrontISTR executable (default: fistr1)
//   --volfrac <f>       Target volume fraction 0<f<1 (default: 0.5)
//   --penal <p>         SIMP penalization exponent (default: 3.0)
//   --rmin <r>          Filter radius in mesh units (default: 1.5)
//   --maxiter <n>       Maximum optimization iterations (default: 100)
//   --E0 <v>            Base Young's modulus for solid (default: 1.0)
//   --Emin <v>          Minimum Young's modulus for void (default: 1e-9)
//   --nu <v>            Poisson's ratio (default: 0.3)
//   --preprocess        Only preprocess .msh to add per-element groups, then exit
//   --gen-mesh <nx> <ny> Generate a cantilever test mesh and exit (or continue)

using System.Diagnostics;

namespace TopOpt;

class Program
{
    static int Main(string[] args)
    {
        // ── Defaults ──────────────────────────────────────────────────────
        string meshPath = "";
        string cntTemplate = "";
        string fistrExe = "fistr1";
        double volFrac = 0.5, penal = 3.0, rMin = 1.5;
        double E0 = 1.0, Emin = 1e-9, nu = 0.3;
        int maxIter = 100;
        bool genMesh = false, preprocessOnly = false;
        int genNx = 0, genNy = 0;

        // ── Argument parsing ─────────────────────────────────────────────
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mesh":       meshPath    = args[++i]; break;
                case "--cnt":        cntTemplate = args[++i]; break;
                case "--fistr":      fistrExe    = args[++i]; break;
                case "--volfrac":    volFrac     = double.Parse(args[++i]); break;
                case "--penal":      penal       = double.Parse(args[++i]); break;
                case "--rmin":       rMin        = double.Parse(args[++i]); break;
                case "--maxiter":    maxIter     = int.Parse(args[++i]);   break;
                case "--E0":         E0          = double.Parse(args[++i]); break;
                case "--Emin":       Emin        = double.Parse(args[++i]); break;
                case "--nu":         nu          = double.Parse(args[++i]); break;
                case "--preprocess": preprocessOnly = true; break;
                case "--gen-mesh":
                    genMesh = true;
                    genNx = int.Parse(args[++i]);
                    genNy = int.Parse(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return PrintUsage();
            }
        }

        // ── Generate test mesh ────────────────────────────────────────────
        if (genMesh)
        {
            if (genNx <= 0 || genNy <= 0)
            {
                Console.Error.WriteLine("--gen-mesh requires positive nx, ny");
                return 1;
            }
            string outMsh = string.IsNullOrEmpty(meshPath)    ? "cantilever.msh" : meshPath;
            string outCnt = string.IsNullOrEmpty(cntTemplate) ? "cantilever_base.cnt" : cntTemplate;

            Console.WriteLine($"Generating {genNx}x{genNy}x1 hex8 cantilever mesh...");
            var genData = MeshPreprocessor.GenerateCantilever(
                genNx, genNy, genNx, genNy * 0.5, outMsh, outCnt);
            Console.WriteLine($"  {outMsh}: {genData.Nodes.Count} nodes, {genData.Elements.Count} elements");
            Console.WriteLine($"  {outCnt}: base control file");

            // If only generating, stop here
            if (string.IsNullOrEmpty(meshPath) && string.IsNullOrEmpty(cntTemplate))
                return 0;

            meshPath    = outMsh;
            cntTemplate = outCnt;
        }

        // ── Validate required arguments ───────────────────────────────────
        if (string.IsNullOrEmpty(meshPath) || string.IsNullOrEmpty(cntTemplate))
        {
            Console.Error.WriteLine("--mesh and --cnt are required (or use --gen-mesh nx ny)");
            return PrintUsage();
        }

        if (!File.Exists(meshPath))
        {
            Console.Error.WriteLine($"Mesh file not found: {meshPath}");
            return 1;
        }
        if (!File.Exists(cntTemplate))
        {
            Console.Error.WriteLine($"Control template not found: {cntTemplate}");
            return 1;
        }

        // ── Preprocess-only mode ──────────────────────────────────────────
        if (preprocessOnly)
        {
            string outMsh = Path.Combine(
                Path.GetDirectoryName(meshPath) ?? ".",
                Path.GetFileNameWithoutExtension(meshPath) + "_work.msh");
            Console.WriteLine($"Preprocessing {meshPath} → {outMsh}");
            MeshPreprocessor.GenerateMshWithPerElemGroups(meshPath, outMsh);
            Console.WriteLine("Done.");
            return 0;
        }

        // ── Working directory and derived paths ───────────────────────────
        string workDir  = Path.GetDirectoryName(Path.GetFullPath(meshPath)) ?? ".";
        string mshFile  = Path.GetFileName(meshPath);
        string cntFile  = Path.Combine(workDir, Path.GetFileNameWithoutExtension(meshPath) + "_work.cnt");
        string resPrefix = Path.GetFileNameWithoutExtension(meshPath);
        string resFile  = Path.Combine(workDir, resPrefix + ".res.0.0");

        // ── Read mesh ─────────────────────────────────────────────────────
        Console.WriteLine("Reading mesh...");
        var mesh = MeshPreprocessor.ReadGeometry(meshPath);
        int ne = mesh.Elements.Count;
        int nn = mesh.Nodes.Count;

        Console.WriteLine($"  Nodes: {nn}, Elements: {ne}");
        Console.WriteLine($"  Total volume: {mesh.ElemVolumes.Sum():F4}");
        Console.WriteLine($"Parameters: volFrac={volFrac}, penal={penal}, rMin={rMin}, maxIter={maxIter}");

        // Map result element IDs to mesh element indices
        var idToIdx = new Dictionary<int, int>(ne);
        for (int e = 0; e < ne; e++)
            idToIdx[mesh.Elements[e].Id] = e;

        // ── Initialize densities ─────────────────────────────────────────
        var rho = new double[ne];
        Array.Fill(rho, volFrac);

        // Write hecmw_ctrl.dat once (mesh file doesn't change)
        FistrWriter.WriteHecmwCtrl(workDir, mshFile, Path.GetFileName(cntFile), resPrefix);

        Console.WriteLine();
        Console.WriteLine($"{"Iter",4}  {"Compliance",14}  {"Change",10}  {"VolFrac",8}");
        Console.WriteLine(new string('-', 45));

        // ── Optimization loop ─────────────────────────────────────────────
        for (int iter = 1; iter <= maxIter; iter++)
        {
            // 1. Write .cnt with SIMP-scaled materials
            FistrWriter.WriteCnt(cntTemplate, cntFile, mesh.Elements, rho, E0, Emin, nu, penal);

            // 2. Run FrontISTR
            if (!RunFistr(fistrExe, workDir, out string errorOut))
            {
                Console.Error.WriteLine($"FrontISTR failed at iteration {iter}:");
                Console.Error.WriteLine(errorOut);
                return 1;
            }

            // 3. Read element results from .res file
            if (!File.Exists(resFile))
            {
                Console.Error.WriteLine($"Result file not found: {resFile}");
                return 1;
            }
            var result = ResReader.Read(resFile);

            // 4. Reorder result arrays to match mesh element order
            var stress = new double[ne, 6];
            var strain = new double[ne, 6];
            for (int re = 0; re < result.ElemIds.Length; re++)
            {
                if (!idToIdx.TryGetValue(result.ElemIds[re], out int e)) continue;
                for (int c = 0; c < 6; c++)
                {
                    stress[e, c] = result.ElemStress[re, c];
                    strain[e, c] = result.ElemStrain[re, c];
                }
            }

            // 5. Compute sensitivity: s_e = -p * W_e / rho_e
            var sensitivity = Simp.ComputeSensitivity(stress, strain, mesh.ElemVolumes, rho, penal);

            // 6. Apply sensitivity filter
            var filtered = Simp.ApplyFilter(sensitivity, mesh.ElemCentroids, rMin);

            // 7. Update densities via OC method
            var rhoNew = Simp.OcUpdate(rho, filtered, mesh.ElemVolumes, volFrac);

            // 8. Compute compliance (= 2 * total strain energy)
            double compliance = 0;
            for (int e = 0; e < ne; e++)
                for (int c = 0; c < 6; c++)
                    compliance += stress[e, c] * strain[e, c] * mesh.ElemVolumes[e];

            // 9. Compute max density change for convergence
            double change = 0;
            double actualVol = 0;
            for (int e = 0; e < ne; e++)
            {
                change = Math.Max(change, Math.Abs(rhoNew[e] - rho[e]));
                actualVol += rhoNew[e] * mesh.ElemVolumes[e];
            }
            double actualVolFrac = actualVol / mesh.ElemVolumes.Sum();

            Console.WriteLine($"{iter,4}  {compliance,14:E6}  {change,10:F6}  {actualVolFrac,8:F4}");

            Array.Copy(rhoNew, rho, ne);

            // 10. Convergence check
            if (iter > 10 && change < 1e-3)
            {
                Console.WriteLine($"\nConverged at iteration {iter} (max density change = {change:E3})");
                break;
            }

        }

        // ── Write final density field ─────────────────────────────────────
        string densityFile = Path.Combine(workDir, resPrefix + "_density.txt");
        using (var sw = new StreamWriter(densityFile))
        {
            sw.WriteLine("# element_id  density");
            for (int e = 0; e < ne; e++)
                sw.WriteLine(FormattableString.Invariant($"{mesh.Elements[e].Id}  {rho[e]:F6}"));
        }
        Console.WriteLine($"\nFinal density written to: {densityFile}");
        Console.WriteLine("Elements with rho > 0.5 form the structural skeleton.");

        return 0;
    }

    private static bool RunFistr(string fistrExe, string workDir, out string errorOutput)
    {
        var psi = new ProcessStartInfo(fistrExe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        if (proc == null) { errorOutput = "Failed to start process"; return false; }

        proc.WaitForExit();
        errorOutput = proc.StandardError.ReadToEnd();
        return proc.ExitCode == 0;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("""
Usage:
  TopOpt --gen-mesh <nx> <ny> [--mesh out.msh] [--cnt out_base.cnt]
  TopOpt --mesh <mesh.msh> --cnt <base.cnt> [options]
  TopOpt --preprocess --mesh <mesh.msh>

Options:
  --fistr <path>   FrontISTR executable (default: fistr1)
  --volfrac <f>    Target volume fraction (default: 0.5)
  --penal <p>      SIMP exponent (default: 3.0)
  --rmin <r>       Filter radius in mesh units (default: 1.5)
  --maxiter <n>    Max iterations (default: 100)
  --E0 <v>         Solid Young's modulus (default: 1.0)
  --Emin <v>       Void Young's modulus (default: 1e-9)
  --nu <v>         Poisson's ratio (default: 0.3)
""");
        return 1;
    }
}
