using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

/// <summary>
/// Runs the reader over a real Unity project. Skips cleanly when that project is absent, so it is
/// a local sanity check rather than a CI dependency. Plan 1's two worst defects were both found
/// this way — by a number looking wrong, not by review.
/// </summary>
public class RealCorpusReaderTests
{
    const string Corpus = "/Users/mike/Projects/project_aurora/Assets";

    [Fact]
    public void ParsesTheRealCorpusWithoutFailing()
    {
        if (!Directory.Exists(Corpus)) return;

        string[] patterns = ["*.prefab", "*.unity", "*.asset", "*.mat", "*.controller"];
        var files = patterns
            .SelectMany(p => Directory.EnumerateFiles(Corpus, p, SearchOption.AllDirectories))
            .ToList();

        int parsed = 0, skipped = 0, objects = 0, references = 0, stripped = 0, scriptRefs = 0;

        foreach (var file in files)
        {
            string content;
            try { content = File.ReadAllText(file); } catch { continue; }

            if (!UnityYamlPreprocessor.LooksLikeUnityYaml(content)) { skipped++; continue; }

            var read = UnityYamlReader.Read(content, file);
            parsed++;
            objects += read.Count;
            references += read.Sum(o => o.References.Count);
            stripped += read.Count(o => o.IsStripped);
            scriptRefs += read.Sum(o => o.References.Count(r => r.PropertyPath == "m_Script"));
        }

        Console.WriteLine($"CORPUS files={files.Count} parsed={parsed} skipped(binary)={skipped} "
                        + $"objects={objects} references={references} stripped={stripped} m_Script={scriptRefs}");

        // Measured 2026-08-02: 612 files, 603 parsed, 9 binary, 24,893 objects, 527 stripped.
        //
        // References were 104,014 until plan 3 captured PrefabInstance m_Modifications as
        // structured entries instead of loose references — now 58,621. The 45,393 difference
        // reconciles with 44,576 modification targets plus 792 objectReferences, which moved to
        // UnityObject.Modifications rather than disappearing. Emitting both forms would have
        // double-counted every override.
        //
        // The binary count is pinned exactly: a jump there means binary detection broke rather
        // than the corpus changing.
        Assert.True(files.Count > 500, $"only found {files.Count} assets");
        Assert.True(parsed > 550, $"only parsed {parsed}");
        Assert.True(objects > 20_000, $"only extracted {objects} objects");
        Assert.True(references > 50_000, $"only extracted {references} references");
        Assert.Equal(9, skipped);
        Assert.True(stripped > 400, $"only {stripped} stripped objects — variant handling may have regressed");
        Assert.True(scriptRefs > 1_000, $"only {scriptRefs} m_Script refs — script linkage may have regressed");
    }
}
