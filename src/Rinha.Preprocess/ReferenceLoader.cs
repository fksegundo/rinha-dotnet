using System.IO.Compression;
using System.Text.Json;

namespace Rinha.Preprocess;

public static class ReferenceLoader
{
    public static List<Reference> LoadReferences(string path)
    {
        using var fileStream = File.OpenRead(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            return ParseReferences(gzip);
        }

        return ParseReferences(fileStream);
    }

    private static List<Reference> ParseReferences(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("expected top-level array");

        var references = new List<Reference>(root.GetArrayLength());

        foreach (var item in root.EnumerateArray())
        {
            var vec = item.GetProperty("vector");
            if (vec.GetArrayLength() != Constants.Dim)
                throw new InvalidOperationException($"expected {Constants.Dim} dims, got {vec.GetArrayLength()}");

            var vector = new short[Constants.PackedDim];
            int i = 0;
            foreach (var val in vec.EnumerateArray())
                vector[i++] = Quantize(val.GetDouble());

            string labelStr = item.GetProperty("label").GetString()!;
            byte label = labelStr == "fraud" ? (byte)1 : (byte)0;
            references.Add(new Reference(vector, label));
        }

        return references;
    }

    private static short Quantize(double value)
    {
        if (value <= -1.0)
            return (short)-Constants.Scale;
        if (value <= 0.0)
            return 0;
        if (value >= 1.0)
            return (short)Constants.Scale;
        return (short)Math.Round(value * Constants.Scale);
    }
}
