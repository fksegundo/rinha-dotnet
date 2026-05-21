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
var indexBytes = builder.BuildIndex(references, leafSize, 128);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
File.WriteAllBytes(outputPath, indexBytes);

Console.WriteLine($"wrote {indexBytes.Length} bytes, {references.Count} references, leaf_size={leafSize}");
