// MeshPreprocessor.cs
// Reads FrontISTR .msh files and provides:
//  - ReadGeometry(): node coordinates + element connectivity for geometry computation
//  - GenerateMshWithPerElemGroups(): rewrites .msh so each element has its own EGRP
//  - GenerateCantilever(): creates a structured hex8 cantilever mesh
//
// Supported element types for volume computation:
//   341 = Tet4, 361 = Hex8

using System.Globalization;
using System.Text;

namespace TopOpt;

public class ElementInfo
{
    public int Id { get; set; }
    public string Type { get; set; } = "";   // e.g. "361"
    public string Group { get; set; } = "";  // e.g. "e_1"
    public int[] Nodes { get; set; } = Array.Empty<int>();
}

public class MeshData
{
    public Dictionary<int, double[]> Nodes { get; } = new(); // ID → [x,y,z]
    public List<ElementInfo> Elements { get; } = new();
    public Dictionary<string, List<int>> NodeGroups { get; } = new(); // NGRP → node ID list
    public double[] ElemVolumes { get; set; } = Array.Empty<double>();
    public double[,] ElemCentroids { get; set; } = new double[0, 0]; // [ne, 3]
}

public static class MeshPreprocessor
{
    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads node coordinates and element connectivity from a .msh file.
    /// Also computes element volumes and centroids.
    /// </summary>
    public static MeshData ReadGeometry(string mshPath)
    {
        var data = ParseMsh(mshPath);
        ComputeGeometry(data);
        return data;
    }

    /// <summary>
    /// Rewrites a .msh file so that each element in the design domain has its own
    /// element group named "e_{id}" and a corresponding SECTION entry.
    /// Material definitions are NOT written here; they go into the .cnt file each iteration.
    /// </summary>
    public static void GenerateMshWithPerElemGroups(string inputMsh, string outputMsh)
    {
        var data = ParseMsh(inputMsh);
        WriteMshWithPerElemGroups(data, inputMsh, outputMsh);
    }

    /// <summary>
    /// Generates a structured hex8 cantilever mesh with per-element groups.
    /// Domain: [0,lx] x [0,ly] x [0,1], fixed left face, load at right midpoint.
    /// </summary>
    public static MeshData GenerateCantilever(
        int nx, int ny,
        double lx, double ly,
        string outputMsh, string outputCntBase)
    {
        var data = BuildHex8Grid(nx, ny, 1, lx, ly, 1.0);
        WriteGeneratedMsh(data, nx, ny, outputMsh);
        WriteCantileverCntBase(nx, ny, outputCntBase);
        ComputeGeometry(data);
        return data;
    }

    // -----------------------------------------------------------------------
    // .msh parser
    // -----------------------------------------------------------------------

    private static MeshData ParseMsh(string path)
    {
        var data = new MeshData();
        var lines = File.ReadAllLines(path);

        string section = "";
        string elemType = "";
        string elemGroup = "";
        string ngrpName = "";
        bool ngrpGenerate = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("!!") || line.StartsWith("#")) continue;

            if (line.StartsWith("!"))
            {
                // Parse keyword and options
                var parts = line.Split(',');
                var keyword = parts[0].Trim().ToUpperInvariant();
                var opts = ParseOptions(parts.Skip(1));

                section = keyword;
                switch (keyword)
                {
                    case "!NODE":
                        elemType = elemGroup = ngrpName = "";
                        break;
                    case "!ELEMENT":
                        elemType = opts.GetValueOrDefault("TYPE", "");
                        elemGroup = opts.GetValueOrDefault("EGRP", "ALL");
                        break;
                    case "!NGROUP":
                        ngrpName = opts.GetValueOrDefault("NGRP", "");
                        ngrpGenerate = opts.ContainsKey("GENERATE");
                        if (!string.IsNullOrEmpty(ngrpName))
                            data.NodeGroups.TryAdd(ngrpName, new List<int>());
                        break;
                    default:
                        break;
                }
                continue;
            }

            // Data line
            var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            switch (section)
            {
                case "!NODE":
                    if (tokens.Length >= 4)
                    {
                        int id = int.Parse(tokens[0]);
                        double x = double.Parse(tokens[1], CultureInfo.InvariantCulture);
                        double y = double.Parse(tokens[2], CultureInfo.InvariantCulture);
                        double z = double.Parse(tokens[3], CultureInfo.InvariantCulture);
                        data.Nodes[id] = new[] { x, y, z };
                    }
                    break;

                case "!ELEMENT":
                    if (tokens.Length >= 2)
                    {
                        int id = int.Parse(tokens[0]);
                        var nodes = tokens.Skip(1).Select(int.Parse).ToArray();
                        data.Elements.Add(new ElementInfo
                        {
                            Id = id,
                            Type = elemType,
                            Group = elemGroup,
                            Nodes = nodes
                        });
                    }
                    break;

                case "!NGROUP":
                    if (!string.IsNullOrEmpty(ngrpName))
                    {
                        if (ngrpGenerate && tokens.Length >= 2)
                        {
                            int start = int.Parse(tokens[0]);
                            int end = int.Parse(tokens[1]);
                            int step = tokens.Length >= 3 ? int.Parse(tokens[2]) : 1;
                            for (int n = start; n <= end; n += step)
                                data.NodeGroups[ngrpName].Add(n);
                        }
                        else
                        {
                            foreach (var t in tokens)
                                if (int.TryParse(t, out int nid))
                                    data.NodeGroups[ngrpName].Add(nid);
                        }
                    }
                    break;
            }
        }

        return data;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> parts)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts)
        {
            var kv = p.Split('=');
            if (kv.Length == 2)
                opts[kv[0].Trim()] = kv[1].Trim();
        }
        return opts;
    }

    // -----------------------------------------------------------------------
    // Mesh writing: per-element groups
    // -----------------------------------------------------------------------

    private static void WriteMshWithPerElemGroups(MeshData data, string inputMsh, string outputMsh)
    {
        var originalLines = File.ReadAllLines(inputMsh);
        var sb = new StringBuilder();

        // Copy everything up to the first !ELEMENT or !SECTION line (keep header + nodes)
        bool inElementOrSection = false;
        foreach (var raw in originalLines)
        {
            var line = raw.Trim().ToUpperInvariant();
            if (line.StartsWith("!ELEMENT") || line.StartsWith("!SECTION"))
            {
                inElementOrSection = true;
                continue;
            }
            if (line.StartsWith("!MATERIAL") || line.StartsWith("!END"))
            {
                inElementOrSection = false;
            }
            if (!inElementOrSection)
            {
                // Skip original ELEMENT/SECTION/MATERIAL data lines
                if (inElementOrSection) continue;
                // Check if this is a data line after a skipped section
                sb.AppendLine(raw);
            }
        }

        // Actually, let's do a cleaner line-by-line approach
        sb.Clear();
        string currentSection = "";
        bool skipLines = false;

        foreach (var raw in originalLines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("!"))
            {
                var kw = trimmed.Split(',')[0].Trim().ToUpperInvariant();
                currentSection = kw;
                skipLines = kw is "!ELEMENT" or "!SECTION" or "!MATERIAL";
                if (!skipLines) sb.AppendLine(raw);
            }
            else if (!skipLines)
            {
                sb.AppendLine(raw);
            }
        }

        // Now append per-element ELEMENT entries
        // Group elements by type for efficiency
        var byType = data.Elements.GroupBy(e => e.Type);
        foreach (var grp in byType)
        {
            foreach (var elem in grp)
            {
                sb.AppendLine($"!ELEMENT, TYPE={grp.Key}, EGRP=e_{elem.Id}");
                sb.Append($" {elem.Id}");
                foreach (var n in elem.Nodes) sb.Append($", {n}");
                sb.AppendLine();
            }
        }

        // Append per-element SECTION entries (material names match group names)
        foreach (var elem in data.Elements)
            sb.AppendLine($"!SECTION, TYPE=SOLID, EGRP=e_{elem.Id}, MATERIAL=e_{elem.Id}\n 1.0");

        sb.AppendLine("!END");
        File.WriteAllText(outputMsh, sb.ToString());
    }

    // -----------------------------------------------------------------------
    // Cantilever mesh generator
    // -----------------------------------------------------------------------

    private static MeshData BuildHex8Grid(int nx, int ny, int nz, double lx, double ly, double lz)
    {
        var data = new MeshData();

        // Node numbering: id = 1 + i + j*(nx+1) + k*(nx+1)*(ny+1)
        int stride_j = nx + 1;
        int stride_k = (nx + 1) * (ny + 1);

        for (int k = 0; k <= nz; k++)
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                {
                    int id = 1 + i + j * stride_j + k * stride_k;
                    data.Nodes[id] = new[]
                    {
                        i * lx / nx,
                        j * ly / ny,
                        k * lz / nz
                    };
                }

        // Element numbering: id = 1 + i + j*nx + k*nx*ny
        int eStride_j = nx;
        int eStride_k = nx * ny;
        int eId = 1;

        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    // Hex8 connectivity (FrontISTR type 361):
                    // Bottom face (z=k):  n1,n2,n3,n4 = (i,j,k),(i+1,j,k),(i+1,j+1,k),(i,j+1,k)
                    // Top face (z=k+1):   n5,n6,n7,n8 = same with k+1
                    int n(int di, int dj, int dk) => 1 + (i + di) + (j + dj) * stride_j + (k + dk) * stride_k;

                    data.Elements.Add(new ElementInfo
                    {
                        Id = eId++,
                        Type = "361",
                        Group = $"e_{eId - 1}",
                        Nodes = new[]
                        {
                            n(0,0,0), n(1,0,0), n(1,1,0), n(0,1,0),
                            n(0,0,1), n(1,0,1), n(1,1,1), n(0,1,1)
                        }
                    });
                }

        return data;
    }

    private static void WriteGeneratedMsh(MeshData data, int nx, int ny, string outputMsh)
    {
        var sb = new StringBuilder();
        sb.AppendLine("!HEADER");
        sb.AppendLine($" Cantilever {nx}x{ny}x1 Hex8 mesh for topology optimization");
        sb.AppendLine("!NODE");
        foreach (var kv in data.Nodes.OrderBy(x => x.Key))
            sb.AppendLine($" {kv.Key}, {kv.Value[0]:F6}, {kv.Value[1]:F6}, {kv.Value[2]:F6}");

        foreach (var elem in data.Elements)
        {
            sb.AppendLine($"!ELEMENT, TYPE={elem.Type}, EGRP=e_{elem.Id}");
            sb.Append($" {elem.Id}");
            foreach (var n in elem.Nodes) sb.Append($", {n}");
            sb.AppendLine();
        }

        foreach (var elem in data.Elements)
        {
            sb.AppendLine($"!SECTION, TYPE=SOLID, EGRP=e_{elem.Id}, MATERIAL=e_{elem.Id}");
            sb.AppendLine(" 1.0");
        }

        // Fixed face: x=0 → all nodes with i=0
        int stride_j = nx + 1;
        int stride_k = (nx + 1) * (ny + 1);
        var fixNodes = new List<int>();
        for (int k = 0; k <= 1; k++)
            for (int j = 0; j <= ny; j++)
                fixNodes.Add(1 + 0 + j * stride_j + k * stride_k);

        sb.AppendLine("!NGROUP, NGRP=FIX");
        sb.AppendLine(" " + string.Join(", ", fixNodes));

        // Load nodes: right face (x=nx), y=ny/2, all z
        var loadNodes = new List<int>();
        int jMid = ny / 2;
        for (int k = 0; k <= 1; k++)
            loadNodes.Add(1 + nx + jMid * stride_j + k * stride_k);

        sb.AppendLine("!NGROUP, NGRP=LOAD");
        sb.AppendLine(" " + string.Join(", ", loadNodes));

        sb.AppendLine("!END");
        File.WriteAllText(outputMsh, sb.ToString());
    }

    private static void WriteCantileverCntBase(int nx, int ny, string outputCnt)
    {
        // Number of load nodes determines force per node
        int nLoadNodes = 2; // 2 z-layers
        double forcePerNode = -1.0 / nLoadNodes;

        var sb = new StringBuilder();
        sb.AppendLine("!! Cantilever topology optimization - base control file");
        sb.AppendLine("!! Material definitions are prepended each iteration by FistrWriter.");
        sb.AppendLine("!VERSION");
        sb.AppendLine(" 3");
        sb.AppendLine("!SOLUTION, TYPE=STATIC");
        sb.AppendLine("!WRITE,RESULT");
        sb.AppendLine("!OUTPUT_RES");
        sb.AppendLine(" ESTRESS, ON");
        sb.AppendLine(" ESTRAIN, ON");
        sb.AppendLine("!BOUNDARY");
        sb.AppendLine(" FIX, 1, 3, 0.0");
        sb.AppendLine("!CLOAD");
        sb.AppendLine($" LOAD, 2, {forcePerNode:F6}");
        sb.AppendLine("!SOLVER, METHOD=CG, PRECOND=1, ITERLOG=NO, TIMELOG=NO");
        sb.AppendLine(" 10000, 1");
        sb.AppendLine(" 1.0e-8, 1.0, 0.0");
        sb.AppendLine("!END");
        File.WriteAllText(outputCnt, sb.ToString());
    }

    // -----------------------------------------------------------------------
    // Geometry computation (volumes and centroids)
    // -----------------------------------------------------------------------

    private static void ComputeGeometry(MeshData data)
    {
        int ne = data.Elements.Count;
        data.ElemVolumes = new double[ne];
        data.ElemCentroids = new double[ne, 3];

        for (int e = 0; e < ne; e++)
        {
            var elem = data.Elements[e];
            var nodeCoords = elem.Nodes.Select(nid => data.Nodes[nid]).ToArray();

            // Centroid = average of node coordinates
            double cx = 0, cy = 0, cz = 0;
            foreach (var nc in nodeCoords) { cx += nc[0]; cy += nc[1]; cz += nc[2]; }
            cx /= nodeCoords.Length; cy /= nodeCoords.Length; cz /= nodeCoords.Length;
            data.ElemCentroids[e, 0] = cx;
            data.ElemCentroids[e, 1] = cy;
            data.ElemCentroids[e, 2] = cz;

            data.ElemVolumes[e] = elem.Type switch
            {
                "361" => ComputeHex8Volume(nodeCoords),
                "341" => ComputeTet4Volume(nodeCoords),
                _ => ComputeFallbackVolume(nodeCoords)
            };
        }
    }

    // Hex8 volume via Jacobian at element center (xi=eta=zeta=0)
    // Natural coordinates of hex8 nodes: xi,eta,zeta each in {-1,+1}
    private static double ComputeHex8Volume(double[][] coords)
    {
        // xi signs: -1,+1,+1,-1,-1,+1,+1,-1
        // eta signs: -1,-1,+1,+1,-1,-1,+1,+1
        // zeta signs: -1,-1,-1,-1,+1,+1,+1,+1
        double[] xi   = { -1, 1, 1, -1, -1, 1, 1, -1 };
        double[] eta  = { -1,-1, 1,  1, -1,-1, 1,  1 };
        double[] zeta = { -1,-1,-1, -1,  1, 1, 1,  1 };

        // J = (1/8) * sum_i [ xi_i*(x_i,y_i,z_i); eta_i*(...); zeta_i*(...) ]
        var J = new double[3, 3];
        for (int i = 0; i < 8; i++)
        {
            J[0, 0] += xi[i]   * coords[i][0];
            J[0, 1] += xi[i]   * coords[i][1];
            J[0, 2] += xi[i]   * coords[i][2];
            J[1, 0] += eta[i]  * coords[i][0];
            J[1, 1] += eta[i]  * coords[i][1];
            J[1, 2] += eta[i]  * coords[i][2];
            J[2, 0] += zeta[i] * coords[i][0];
            J[2, 1] += zeta[i] * coords[i][1];
            J[2, 2] += zeta[i] * coords[i][2];
        }
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                J[r, c] /= 8.0;

        double detJ = J[0,0]*(J[1,1]*J[2,2]-J[1,2]*J[2,1])
                    - J[0,1]*(J[1,0]*J[2,2]-J[1,2]*J[2,0])
                    + J[0,2]*(J[1,0]*J[2,1]-J[1,1]*J[2,0]);

        // Volume of reference hex = 8 (from -1 to +1 in each direction)
        return Math.Abs(detJ) * 8.0;
    }

    // Tet4 volume = |det([v2-v1, v3-v1, v4-v1])| / 6
    private static double ComputeTet4Volume(double[][] coords)
    {
        double[] v1 = coords[0], v2 = coords[1], v3 = coords[2], v4 = coords[3];
        double a0 = v2[0]-v1[0], a1 = v2[1]-v1[1], a2 = v2[2]-v1[2];
        double b0 = v3[0]-v1[0], b1 = v3[1]-v1[1], b2 = v3[2]-v1[2];
        double c0 = v4[0]-v1[0], c1 = v4[1]-v1[1], c2 = v4[2]-v1[2];
        double det = a0*(b1*c2-b2*c1) - a1*(b0*c2-b2*c0) + a2*(b0*c1-b1*c0);
        return Math.Abs(det) / 6.0;
    }

    // Fallback: use bounding box volume / number of nodes (rough approximation)
    private static double ComputeFallbackVolume(double[][] coords)
    {
        double xmin = coords.Min(c => c[0]), xmax = coords.Max(c => c[0]);
        double ymin = coords.Min(c => c[1]), ymax = coords.Max(c => c[1]);
        double zmin = coords.Min(c => c[2]), zmax = coords.Max(c => c[2]);
        return (xmax - xmin) * (ymax - ymin) * (zmax - zmin);
    }
}
