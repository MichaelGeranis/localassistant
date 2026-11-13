using LocalAssistant.Digest;

namespace LocalAssistant.Tests;

public class DigestNumberParserTests
{
    [Theory]
    [InlineData("1 3 5", new[] { 1, 3, 5 })]
    [InlineData("mark 2, 4 and 2 again", new[] { 2, 4 })]
    [InlineData("the first and third", new[] { 1, 3 })]
    [InlineData("1st 2nd 3rd", new[] { 1, 2, 3 })]
    [InlineData("3-5", new[] { 3, 4, 5 })]
    [InlineData("2 to 4", new[] { 2, 3, 4 })]
    [InlineData("1 through 3 and 7", new[] { 1, 2, 3, 7 })]
    [InlineData("Seventh", new[] { 7 })]
    public void TryParseNumbers_WhenNumbersAreNamed_ShouldReturnThemDistinctAndAscending(string text, int[] expected)
    {
        Assert.True(DigestNumberParser.TryParseNumbers(text, out var numbers));
        Assert.Equal(expected, numbers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("archive everything")]
    [InlineData("0")]
    public void TryParseNumbers_WhenNoDigestNumberIsNamed_ShouldReturnFalse(string text)
    {
        Assert.False(DigestNumberParser.TryParseNumbers(text, out var numbers));
        Assert.Empty(numbers);
    }
}
