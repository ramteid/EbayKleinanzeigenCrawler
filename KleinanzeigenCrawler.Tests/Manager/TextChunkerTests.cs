using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Manager;
using System.Linq;

namespace KleinanzeigenCrawler.Tests.Manager;

public class TextChunkerTests
{
    [Fact]
    public void Split_ShouldReturnShortTextAsASingleChunk()
    {
        // Act
        var chunks = TextChunker.Split("Hello\nWorld", 100).ToList();

        // Assert
        chunks.Should().Equal("Hello\nWorld");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Split_ShouldReturnNothingForBlankText(string text)
    {
        // Act
        var chunks = TextChunker.Split(text, 100).ToList();

        // Assert
        chunks.Should().BeEmpty();
    }

    [Fact]
    public void Split_ShouldPreferLineBreaks()
    {
        // Arrange
        var text = "aaaa\nbbbb\ncccc\ndddd";

        // Act
        var chunks = TextChunker.Split(text, 10).ToList();

        // Assert
        chunks.Should().Equal("aaaa\nbbbb", "cccc\ndddd");
    }

    [Fact]
    public void Split_ShouldCutLinesThatAreLongerThanTheLimit()
    {
        // Arrange
        var text = new string('x', 25);

        // Act
        var chunks = TextChunker.Split(text, 10).ToList();

        // Assert
        chunks.Should().Equal(new string('x', 10), new string('x', 10), new string('x', 5));
    }

    [Fact]
    public void Split_ShouldKeepEveryChunkWithinTheLimitAndLoseNoContent()
    {
        // Arrange
        var lines = Enumerable.Range(1, 200).Select(i => $"Line number {i} with some text").ToList();
        var text = string.Join("\n", lines);

        // Act
        var chunks = TextChunker.Split(text, 120).ToList();

        // Assert
        chunks.Should().OnlyContain(chunk => chunk.Length <= 120);
        string.Join("\n", chunks).Should().Be(text);
    }

    [Fact]
    public void Split_ShouldNeverReturnBlankChunks()
    {
        // Arrange
        var text = "aaaaaaaa\n" + new string(' ', 12) + "\nbbbb";

        // Act
        var chunks = TextChunker.Split(text, 10).ToList();

        // Assert
        chunks.Should().OnlyContain(chunk => chunk.Trim().Length > 0);
        string.Concat(chunks).Replace("\n", string.Empty).Replace(" ", string.Empty).Should().Be("aaaaaaaabbbb");
    }

    [Fact]
    public void Split_ShouldTerminateForALimitSmallerThanASurrogatePair()
    {
        // Arrange
        var text = char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0x1F600);

        // Act
        var chunks = TextChunker.Split(text, 1).ToList();

        // Assert
        string.Concat(chunks).Should().Be(text);
    }

    [Fact]
    public void Split_ShouldNotSeparateSurrogatePairs()
    {
        // Arrange
        var text = "abcd" + char.ConvertFromUtf32(0x1F600) + "efgh";

        // Act
        var chunks = TextChunker.Split(text, 5).ToList();

        // Assert
        chunks.Should().Equal("abcd", char.ConvertFromUtf32(0x1F600) + "efg", "h");
    }
}
