using AIWorld.Core.Chat;
using Xunit;

namespace AIWorld.Core.Tests;

public class DeterministicChatControlTests
{
    [Theory]
    [InlineData("取消约会。", true)]
    [InlineData("取消约会吗？", false)]
    [InlineData("不要取消约会", false)]
    [InlineData("你说过‘取消约会’", false)]
    public void CancellationRequiresWholeExplicitControl(string text, bool expected)
        => Assert.Equal(expected, PlayerChatRules.IsCancelAppointment(text));

    [Theory]
    [InlineData("不用跟着我了！", false, true)]
    [InlineData("Stop following me.", false, true)]
    [InlineData("不用跟着我了吗？", true, false)]
    [InlineData("算了", false, false)]
    [InlineData("算了", true, true)]
    [InlineData("他说‘不用跟了’", true, false)]
    public void StopControlUsesContextAndDoesNotGuess(string text, bool following, bool expected)
        => Assert.Equal(expected, PlayerChatRules.IsStopFollowing(text, following));
}
