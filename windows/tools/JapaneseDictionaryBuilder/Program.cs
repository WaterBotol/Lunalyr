// Builds Lunalyr's Japanese dictionary from the MeCab IPAdic source, and prints analyses for checking it.
//
//   build <ipadic source folder> <output .lunadic.br>   reads *.csv, matrix.def, char.def, unk.def, left-id.def (EUC-JP)
//   probe <.lunadic.br> <text file>                   prints each line's morphemes, readings and parts of speech
//
// The source is data only: nothing in it is run.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Lyrics.Readings.Japanese;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;

switch (args)
{
    case ["build", var source, var output]:
        Build(source, output);
        return 0;
    case ["probe", var dictionary, var texts]:
        Probe(dictionary, texts);
        return 0;
    default:
        Console.Error.WriteLine("usage: build <ipadic folder> <output.lunadic.br> | probe <dictionary.lunadic.br> <texts.txt>");
        return 2;
}

static void Build(string folder, string output)
{
    var eucJp = Encoding.GetEncoding("euc-jp");
    var words = new List<(string Surface, short Left, short Right, short Cost, string? Reading)>();
    foreach (var file in Directory.GetFiles(folder, "*.csv").OrderBy(path => path, StringComparer.Ordinal))
    {
        foreach (var line in File.ReadLines(file, eucJp))
        {
            if (line.Length == 0) continue;
            var fields = SplitCsv(line);
            if (fields.Count < 4) throw new InvalidDataException($"{Path.GetFileName(file)}: {line}");
            var reading = fields.Count > 11 && fields[11] is { Length: > 0 } yomi && yomi != "*" ? yomi : null;
            words.Add((fields[0], short.Parse(fields[1], CultureInfo.InvariantCulture), short.Parse(fields[2], CultureInfo.InvariantCulture),
                short.Parse(fields[3], CultureInfo.InvariantCulture), reading));
        }
    }

    var matrixLines = File.ReadLines(Path.Combine(folder, "matrix.def"), Encoding.ASCII).GetEnumerator();
    matrixLines.MoveNext();
    var size = matrixLines.Current.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    var (leftSize, rightSize) = (size[0], size[1]);
    var matrix = new short[leftSize * rightSize];
    while (matrixLines.MoveNext())
    {
        var parts = matrixLines.Current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) continue;
        // "right id of the word before, left id of the word after, cost".
        matrix[int.Parse(parts[0], CultureInfo.InvariantCulture) + leftSize * int.Parse(parts[1], CultureInfo.InvariantCulture)] =
            short.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    // char.def: category definitions (NAME INVOKE GROUP LENGTH), then code ranges with their categories; a later
    // range overrides an earlier one, and the first category named is the character's own.
    var definitions = new List<(string Name, bool Invoke, bool Group, int Length)>();
    var ranges = new List<(int Low, int High, string[] Names)>();
    foreach (var raw in File.ReadLines(Path.Combine(folder, "char.def"), eucJp))
    {
        var line = raw.Split('#')[0].Trim();
        if (line.Length == 0) continue;
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts[0].StartsWith("0x", StringComparison.Ordinal))
        {
            var bounds = parts[0].Split("..");
            var low = int.Parse(bounds[0][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var high = bounds.Length > 1 ? int.Parse(bounds[1][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : low;
            ranges.Add((low, high, parts[1..]));
        }
        else
        {
            definitions.Add((parts[0], parts[1] == "1", parts[2] == "1", int.Parse(parts[3], CultureInfo.InvariantCulture)));
        }
    }
    int CategoryIndex(string name)
    {
        var index = definitions.FindIndex(definition => definition.Name == name);
        return index >= 0 ? index : throw new InvalidDataException($"char.def: unknown category {name}");
    }
    var defaultIndex = CategoryIndex("DEFAULT");
    var table = new uint[0x10000];
    Array.Fill(table, JapaneseDictionaryFormat.CharInfo(defaultIndex, 1u << defaultIndex));
    foreach (var (low, high, names) in ranges)
    {
        var mask = names.Aggregate(0u, (sum, name) => sum | (1u << CategoryIndex(name)));
        var info = JapaneseDictionaryFormat.CharInfo(CategoryIndex(names[0]), mask);
        for (var code = low; code <= high && code <= 0xFFFF; code++) table[code] = info;
    }

    var unknown = definitions.ToDictionary(definition => definition.Name, _ => new List<(short, short, short)>());
    foreach (var line in File.ReadLines(Path.Combine(folder, "unk.def"), eucJp))
    {
        if (line.Length == 0) continue;
        var fields = SplitCsv(line);
        unknown[fields[0]].Add((short.Parse(fields[1], CultureInfo.InvariantCulture), short.Parse(fields[2], CultureInfo.InvariantCulture),
            short.Parse(fields[3], CultureInfo.InvariantCulture)));
    }
    var categories = definitions
        .Select(definition => new JapaneseDictionaryFormat.CategorySource(definition.Name, definition.Invoke, definition.Group,
            definition.Length, unknown[definition.Name]))
        .ToList();

    var partsOfSpeech = new string[leftSize];
    foreach (var line in File.ReadLines(Path.Combine(folder, "left-id.def"), eucJp))
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0) continue;
        partsOfSpeech[int.Parse(line[..space], CultureInfo.InvariantCulture)] = line[(space + 1)..];
    }

    var bytes = JapaneseDictionaryFormat.Write(new JapaneseDictionaryFormat.Source(words, leftSize, rightSize, matrix, categories,
        table, CategoryIndex("SPACE"), partsOfSpeech.Select(name => name ?? "").ToList()));
    using (var file = File.Create(output))
    using (var brotli = new BrotliStream(file, CompressionLevel.SmallestSize))
    {
        brotli.Write(bytes);
    }
    var dictionary = JapaneseDictionary.Open(bytes);
    Console.WriteLine($"{words.Count} words, {dictionary.SurfaceCount} surfaces, matrix {leftSize}x{rightSize}");
    Console.WriteLine($"expanded {bytes.Length} bytes, SHA-256 {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
    Console.WriteLine($"compressed {new FileInfo(output).Length} bytes, SHA-256 {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(output)))}");
}

static void Probe(string path, string texts)
{
    using var input = new BrotliStream(File.OpenRead(path), CompressionMode.Decompress);
    using var expanded = new MemoryStream();
    input.CopyTo(expanded);
    var analyzer = new MorphologicalAnalyzer(JapaneseDictionary.Open(expanded.ToArray()));
    foreach (var line in File.ReadLines(texts, Encoding.UTF8))
    {
        if (line.Length == 0) continue;
        // surface/reading, as MeCab's own output gives them, so the two can be compared line by line.
        var morphemes = analyzer.Analyze(line);
        Console.WriteLine($"{line}\t{string.Join(" | ", morphemes.Select(m => $"{line.Substring(m.Start, m.Length)}/{m.Reading ?? "?"}"))}");
    }
}

static List<string> SplitCsv(string line)
{
    var fields = new List<string>();
    var field = new StringBuilder();
    var quoted = false;
    for (var index = 0; index < line.Length; index++)
    {
        var character = line[index];
        if (quoted)
        {
            if (character == '"' && index + 1 < line.Length && line[index + 1] == '"') { field.Append('"'); index++; }
            else if (character == '"') quoted = false;
            else field.Append(character);
        }
        else if (character == '"') quoted = true;
        else if (character == ',') { fields.Add(field.ToString()); field.Clear(); }
        else field.Append(character);
    }
    fields.Add(field.ToString());
    return fields;
}
