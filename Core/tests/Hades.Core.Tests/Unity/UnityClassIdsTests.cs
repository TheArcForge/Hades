using Hades.Core.Unity;

namespace Hades.Core.Tests.Unity;

public class UnityClassIdsTests
{
    [Theory]
    [InlineData(1, "GameObject")]
    [InlineData(4, "Transform")]
    [InlineData(114, "MonoBehaviour")]
    [InlineData(115, "MonoScript")]
    [InlineData(21, "Material")]
    [InlineData(1001, "PrefabInstance")]
    [InlineData(224, "RectTransform")]
    [InlineData(91, "AnimatorController")]
    [InlineData(1101, "AnimatorStateTransition")]
    [InlineData(1102, "AnimatorState")]
    [InlineData(1107, "AnimatorStateMachine")]
    public void ResolvesTheClassIdsThatMatter(int classId, string expected)
    {
        Assert.Equal(expected, UnityClassIds.NameFor(classId));
    }

    [Fact]
    public void UnknownClassIdsGetAStableSyntheticName()
    {
        // New Unity versions add class ids. An unknown one must degrade to something addressable,
        // never throw — the graph is still useful without a friendly name for every builtin.
        Assert.Equal("UnityType_99999", UnityClassIds.NameFor(99999));
    }

    [Fact]
    public void KnowsWhetherAClassIdIsRecognised()
    {
        Assert.True(UnityClassIds.IsKnown(1));
        Assert.False(UnityClassIds.IsKnown(99999));
    }
}
