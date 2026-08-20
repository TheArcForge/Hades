using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

public class UnityYamlPreprocessorTests
{
    [Fact]
    public void RewritesTheUnityTagToAPlainAnchor()
    {
        var result = UnityYamlPreprocessor.MakeStandardYaml("--- !u!1 &12345\nGameObject:\n");
        Assert.Contains("--- &12345", result);
        Assert.DoesNotContain("!u!", result);
    }

    [Fact]
    public void HandlesNegativeFileIds()
    {
        // Unity fileIDs are signed 64-bit. Missing the minus silently drops ~7% of prefabs.
        var result = UnityYamlPreprocessor.MakeStandardYaml("--- !u!114 &-1766573354249734030\nMonoBehaviour:\n");
        Assert.Contains("--- &-1766573354249734030", result);
        Assert.DoesNotContain("!u!", result);
    }

    [Fact]
    public void HandlesTheStrippedMarker()
    {
        var result = UnityYamlPreprocessor.MakeStandardYaml("--- !u!1 &6346727972004658377 stripped\nGameObject:\n");
        Assert.Contains("--- &6346727972004658377", result);
        Assert.DoesNotContain("!u!", result);
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        // 112 files in the real corpus use CRLF. Without \r? before the anchor the " stripped"
        // variant never matches, and 16 files fail to parse — silently, because the tag stays.
        var result = UnityYamlPreprocessor.MakeStandardYaml("--- !u!1 &123 stripped\r\nGameObject:\r\n");
        Assert.DoesNotContain("!u!", result);
    }

    [Theory]
    [InlineData("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &1\n", true)]
    [InlineData("not yaml at all", false)]
    [InlineData("%YAML 1.1\n\0\0binary\0", false)]
    public void RecognisesParseableUnityYaml(string content, bool expected)
    {
        Assert.Equal(expected, UnityYamlPreprocessor.LooksLikeUnityYaml(content));
    }

    [Fact]
    public void LeavesOrdinaryContentAlone()
    {
        const string body = "GameObject:\n  m_Name: Player\n";
        Assert.Equal(body, UnityYamlPreprocessor.MakeStandardYaml(body));
    }
}
