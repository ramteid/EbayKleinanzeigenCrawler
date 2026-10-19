using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Models;

namespace KleinanzeigenCrawler.Tests.Models;

public class ConversationSessionTests
{
    [Fact]
    public void ConversationSession_ShouldInitializeWithCorrectDefaults()
    {
        // Arrange & Act
        var session = new ConversationSession("test_user");
        
        // Assert
        session.UserId.Should().Be("test_user");
        session.State.Should().Be(ConversationState.Idle);
        session.Data.Should().NotBeNull();
        session.Data.Should().BeEmpty();
    }
    
    [Fact]
    public void ConversationSession_ShouldTrackActivityInUtc()
    {
        // Arrange & Act
        var session = new ConversationSession("test_user");
        session.UpdateActivity();

        // Assert
        session.LastActivity.Kind.Should().Be(System.DateTimeKind.Utc);
    }

    [Fact]
    public void UpdateActivity_ShouldUpdateLastActivityTime()
    {
        // Arrange
        var session = new ConversationSession("test_user");
        var initialTime = session.LastActivity;
        System.Threading.Thread.Sleep(10); // Small delay
        
        // Act
        session.UpdateActivity();
        
        // Assert
        session.LastActivity.Should().BeAfter(initialTime);
    }
    
    [Fact]
    public void GetData_ShouldReturnNullForNonExistentKey()
    {
        // Arrange
        var session = new ConversationSession("test_user");
        
        // Act
        var result = session.GetData<string>("non_existent");
        
        // Assert
        result.Should().BeNull();
    }
    
    [Fact]
    public void SetData_AndGetData_ShouldWorkCorrectly()
    {
        // Arrange
        var session = new ConversationSession("test_user");
        var testData = new AddSubscriptionData { Title = "Test" };
        
        // Act
        session.SetData("test_key", testData);
        var retrieved = session.GetData<AddSubscriptionData>("test_key");
        
        // Assert
        retrieved.Should().NotBeNull();
        retrieved.Title.Should().Be("Test");
    }
    
    [Fact]
    public void ClearData_ShouldResetData()
    {
        // Arrange
        var session = new ConversationSession("test_user");
        session.SetData("key1", "value1");
        session.SetData("key2", "value2");
        
        // Act
        session.ClearData();
        
        // Assert
        session.Data.Should().BeEmpty();
    }
}
