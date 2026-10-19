using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Manager;
using Moq;
using Serilog;

namespace KleinanzeigenCrawler.Tests.Manager;

public class SessionManagerTests
{
    private readonly Mock<ILogger> _mockLogger;
    
    public SessionManagerTests()
    {
        _mockLogger = new Mock<ILogger>();
    }
    
    [Fact]
    public void GetOrCreate_ShouldCreateNewSessionForNewUser()
    {
        // Arrange
        var sessionManager = new SessionManager(_mockLogger.Object);
        
        // Act
        var session = sessionManager.GetOrCreate("user1");
        
        // Assert
        session.Should().NotBeNull();
        session.UserId.Should().Be("user1");
        sessionManager.GetActiveSessionCount().Should().Be(1);
    }
    
    [Fact]
    public void GetOrCreate_ShouldReturnExistingSessionForSameUser()
    {
        // Arrange
        var sessionManager = new SessionManager(_mockLogger.Object);
        var session1 = sessionManager.GetOrCreate("user1");
        session1.SetData("test", "value");
        
        // Act
        var session2 = sessionManager.GetOrCreate("user1");
        
        // Assert
        session2.GetData<string>("test").Should().Be("value");
        sessionManager.GetActiveSessionCount().Should().Be(1);
    }
    
    [Fact]
    public void EndSession_ShouldRemoveSession()
    {
        // Arrange
        var sessionManager = new SessionManager(_mockLogger.Object);
        sessionManager.GetOrCreate("user1");
        
        // Act
        sessionManager.EndSession("user1");
        
        // Assert
        sessionManager.GetActiveSessionCount().Should().Be(0);
    }
    
    [Fact]
    public void GetActiveSessionCount_ShouldReturnCorrectCount()
    {
        // Arrange
        var sessionManager = new SessionManager(_mockLogger.Object);
        
        // Act
        sessionManager.GetOrCreate("user1");
        sessionManager.GetOrCreate("user2");
        sessionManager.GetOrCreate("user3");
        
        // Assert
        sessionManager.GetActiveSessionCount().Should().Be(3);
    }
    
    [Fact]
    public void CleanupExpiredSessions_ShouldRemoveOnlySessionsThatWereInactiveLongerThanTheTimeout()
    {
        // Arrange
        var sessionManager = new SessionManager(_mockLogger.Object, System.TimeSpan.FromMilliseconds(200));
        sessionManager.GetOrCreate("inactive");
        sessionManager.GetOrCreate("active").SetData("marker", "kept");
        System.Threading.Thread.Sleep(300);
        sessionManager.GetOrCreate("active");

        // Act
        sessionManager.CleanupExpiredSessions();

        // Assert
        sessionManager.GetActiveSessionCount().Should().Be(1);
        sessionManager.GetOrCreate("active").GetData<string>("marker").Should().Be("kept");
    }
    
    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        var sessionManager = new SessionManager(_mockLogger.Object);
        sessionManager.GetOrCreate("user1");
        
        // Act & Assert
        var act = () => sessionManager.Dispose();
        act.Should().NotThrow();
    }
}
