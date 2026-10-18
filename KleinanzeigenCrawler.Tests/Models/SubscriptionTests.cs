using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Models;
using System;
using System.Collections.Generic;

namespace KleinanzeigenCrawler.Tests.Models;

public class SubscriptionTests
{
    [Fact]
    public void Subscription_ShouldGenerateUniqueId()
    {
        // Arrange & Act
        var subscription1 = new Subscription();
        var subscription2 = new Subscription();
        
        // Assert
        subscription1.Id.Should().NotBe(Guid.Empty);
        subscription2.Id.Should().NotBe(Guid.Empty);
        subscription1.Id.Should().NotBe(subscription2.Id);
    }
    
    [Fact]
    public void Subscription_ShouldInitializeWithDefaults()
    {
        // Arrange & Act
        var subscription = new Subscription();
        
        // Assert
        subscription.Enabled.Should().BeFalse();
        subscription.FirstRunCompleted.Should().BeFalse();
        subscription.IncludeKeywords.Should().NotBeNull().And.BeEmpty();
        subscription.ExcludeKeywords.Should().NotBeNull().And.BeEmpty();
    }
    
    [Fact]
    public void Subscription_ShouldAllowSettingProperties()
    {
        // Arrange
        var subscription = new Subscription
        {
            Title = "Test Subscription",
            QueryUrl = new Uri("https://example.com"),
            IncludeKeywords = new List<string> { "keyword1", "keyword2" },
            ExcludeKeywords = new List<string> { "excluded" },
            InitialPull = true,
            Enabled = true
        };
        
        // Assert
        subscription.Title.Should().Be("Test Subscription");
        subscription.QueryUrl.ToString().Should().Be("https://example.com/");
        subscription.IncludeKeywords.Should().HaveCount(2);
        subscription.ExcludeKeywords.Should().HaveCount(1);
        subscription.InitialPull.Should().BeTrue();
        subscription.Enabled.Should().BeTrue();
    }
}
