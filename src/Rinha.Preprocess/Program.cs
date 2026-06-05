using Rinha.Preprocess;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Rinha.Preprocess <references.json.gz> <output.idx>");
    Environment.Exit(1);
}

string inputPath = args[0];
string outputPath = args[1];

int leafSize = int.TryParse(Environment.GetEnvironmentVariable("RINHA_LEAF_SIZE"), out var ls)
    ? ls
    : Constants.DefaultLeafSize;

var references = ReferenceLoader.LoadReferences(inputPath);
var builder = new IndexBuilder();
string schemeName = Environment.GetEnvironmentVariable("RINHA_PARTITION_SCHEME") ?? "tree256";
var indexBytes = builder.BuildIndex(references, leafSize, schemeName);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
File.WriteAllBytes(outputPath, indexBytes);

Console.WriteLine($"wrote {indexBytes.Length} bytes, {references.Count} references, leaf_size={leafSize}");
