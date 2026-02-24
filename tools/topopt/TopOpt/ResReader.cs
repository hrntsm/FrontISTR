// ResReader.cs
// Parses FrontISTR native result files (.res.0.0)
// File format reference: hecmw1/src/common/hecmw_result_io_txt.c
//
// Format after *data section:
//   n_node  n_elem
//   n_node_comp  n_elem_comp
//   dof_1  dof_2  ...  dof_(n_node_comp+n_elem_comp)
//   label_1
//   ...
//   label_(n_node_comp+n_elem_comp)
//   node_id  val_1  val_2  ...   (repeated for each node)
//   elem_id  val_1  val_2  ...   (repeated for each element)

using System.Globalization;

namespace TopOpt;

public class ResResult
{
    public int[] ElemIds { get; init; } = Array.Empty<int>();
    // [elemIndex, component]: XX, YY, ZZ, XY, YZ, ZX
    public double[,] ElemStress { get; init; } = new double[0, 0];
    public double[,] ElemStrain { get; init; } = new double[0, 0];
}

public static class ResReader
{
    public static ResResult Read(string resPath)
    {
        var lines = File.ReadAllLines(resPath);
        int li = 0;

        // Advance to *data section
        while (li < lines.Length && !lines[li].TrimStart().StartsWith("*data", StringComparison.OrdinalIgnoreCase))
            li++;
        if (li >= lines.Length)
            throw new InvalidDataException($"*data section not found in {resPath}");
        li++; // skip *data line

        // Skip empty/comment lines then read structured data using a token queue
        var tokens = new Queue<string>();

        void FillTokens()
        {
            while (tokens.Count == 0 && li < lines.Length)
            {
                var raw = lines[li++].Trim();
                if (raw.Length > 0)
                    foreach (var t in raw.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                        tokens.Enqueue(t);
            }
        }

        string Peek()
        {
            FillTokens();
            return tokens.Count > 0 ? tokens.Peek() : "";
        }

        string Next()
        {
            FillTokens();
            if (tokens.Count == 0) throw new InvalidDataException("Unexpected end of file in .res data section");
            return tokens.Dequeue();
        }

        // n_node, n_elem
        int nNode = int.Parse(Next());
        int nElem = int.Parse(Next());

        // n_node_comp, n_elem_comp
        int nNodeComp = int.Parse(Next());
        int nElemComp = int.Parse(Next());

        // DOFs per component
        int totalComps = nNodeComp + nElemComp;
        var dofs = new int[totalComps];
        for (int i = 0; i < totalComps; i++)
            dofs[i] = int.Parse(Next());

        int nodeValPerNode = 0;
        for (int i = 0; i < nNodeComp; i++) nodeValPerNode += dofs[i];
        int elemValPerElem = 0;
        for (int i = 0; i < nElemComp; i++) elemValPerElem += dofs[nNodeComp + i];

        // Labels: each label is on its own line (may contain spaces within the label name itself,
        // but in practice FrontISTR labels are single words).
        // We read labels line-by-line to avoid confusing them with subsequent numeric data.
        // Drain any partially-consumed line tokens first.
        tokens.Clear();

        var allLabels = new string[totalComps];
        for (int i = 0; i < totalComps; i++)
        {
            // Skip blank lines, read next non-empty line
            string? labelLine = null;
            while (li < lines.Length)
            {
                var raw = lines[li++].Trim();
                if (raw.Length > 0 && !raw.StartsWith("*"))
                {
                    labelLine = raw;
                    break;
                }
            }
            allLabels[i] = labelLine ?? throw new InvalidDataException("Missing label in .res file");
        }

        // Determine offsets of ElementalSTRAIN and ElementalSTRESS in element value array
        int strainOffset = -1, stressOffset = -1, strainDof = 0, stressDof = 0;
        int offset = 0;
        for (int i = 0; i < nElemComp; i++)
        {
            string label = allLabels[nNodeComp + i];
            if (label.StartsWith("ElementalSTRAIN", StringComparison.OrdinalIgnoreCase))
            {
                strainOffset = offset;
                strainDof = dofs[nNodeComp + i];
            }
            else if (label.StartsWith("ElementalSTRESS", StringComparison.OrdinalIgnoreCase))
            {
                stressOffset = offset;
                stressDof = dofs[nNodeComp + i];
            }
            offset += dofs[nNodeComp + i];
        }

        // Re-initialize token queue for numeric data
        tokens.Clear();

        // Skip node data (nNode blocks of: 1 ID + nodeValPerNode values)
        for (int i = 0; i < nNode; i++)
        {
            Next(); // node ID
            for (int j = 0; j < nodeValPerNode; j++) Next();
        }

        // Read element data
        var elemIds = new int[nElem];
        var elemStress = new double[nElem, 6];
        var elemStrain = new double[nElem, 6];

        for (int i = 0; i < nElem; i++)
        {
            elemIds[i] = int.Parse(Next());
            var vals = new double[elemValPerElem];
            for (int j = 0; j < elemValPerElem; j++)
                vals[j] = double.Parse(Next(), CultureInfo.InvariantCulture);

            if (strainOffset >= 0)
                for (int c = 0; c < Math.Min(6, strainDof); c++)
                    elemStrain[i, c] = vals[strainOffset + c];

            if (stressOffset >= 0)
                for (int c = 0; c < Math.Min(6, stressDof); c++)
                    elemStress[i, c] = vals[stressOffset + c];
        }

        return new ResResult
        {
            ElemIds = elemIds,
            ElemStress = elemStress,
            ElemStrain = elemStrain
        };
    }
}
