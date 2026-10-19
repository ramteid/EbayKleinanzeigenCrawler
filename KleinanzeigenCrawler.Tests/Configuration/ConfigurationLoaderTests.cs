using Xunit;
using FluentAssertions;
using KleinanzeigenCrawler.Configuration;
using System;
using System.Collections.Generic;

namespace KleinanzeigenCrawler.Tests.Configuration;

public class ConfigurationLoaderTests
{
    private static AppConfiguration Build(params (string Key, string Value)[] variables)
    {
        var environment = new Dictionary<string, string>();
        foreach (var (key, value) in variables)
        {
            environment[key] = value;
        }

        return ConfigurationLoader.Build(key => environment.GetValueOrDefault(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_ShouldFailWhenNoMessengerIsConfigured(string blankValue)
    {
        // Act
        var act = () => Build(("TELEGRAM_BOT_TOKEN", blankValue), ("SIGNAL_NUMBER", blankValue));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*TELEGRAM_BOT_TOKEN*SIGNAL_NUMBER*");
    }

    [Fact]
    public void Build_ShouldEnableTelegramOnlyWhenOnlyTheTokenIsSet()
    {
        // Act
        var config = Build(("TELEGRAM_BOT_TOKEN", "123:abc"));

        // Assert
        config.Telegram.Enabled.Should().BeTrue();
        config.Telegram.BotToken.Should().Be("123:abc");
        config.Signal.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Build_ShouldEnableSignalOnlyWithDefaultApiUrlWhenOnlyTheNumberIsSet()
    {
        // Act
        var config = Build(("SIGNAL_NUMBER", "+491701234567"));

        // Assert
        config.Telegram.Enabled.Should().BeFalse();
        config.Signal.Enabled.Should().BeTrue();
        config.Signal.Number.Should().Be("+491701234567");
        config.Signal.ApiUrl.Should().Be("http://signal-api:8080");
    }

    [Fact]
    public void Build_ShouldEnableBothMessengersAndUseConfiguredSignalApiUrl()
    {
        // Act
        var config = Build(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("SIGNAL_NUMBER", "+491701234567"),
            ("SIGNAL_API_URL", "http://localhost:8081"));

        // Assert
        config.Telegram.Enabled.Should().BeTrue();
        config.Signal.Enabled.Should().BeTrue();
        config.Signal.ApiUrl.Should().Be("http://localhost:8081");
    }

    [Theory]
    [InlineData("491701234567")]
    [InlineData("+49 170 1234567")]
    [InlineData("+0491701234567")]
    [InlineData("+49170")]
    [InlineData("+4917012345678901")]
    [InlineData("+49170abc4567")]
    public void Build_ShouldRejectMalformedSignalNumber(string number)
    {
        // Act
        var act = () => Build(("SIGNAL_NUMBER", number));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*SIGNAL_NUMBER*international format*");
    }

    [Theory]
    [InlineData("signal-api:8080")]
    [InlineData("not a url")]
    [InlineData("ftp://signal-api")]
    public void Build_ShouldRejectInvalidSignalApiUrl(string url)
    {
        // Act
        var act = () => Build(("SIGNAL_NUMBER", "+491701234567"), ("SIGNAL_API_URL", url));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*SIGNAL_API_URL*");
    }

    [Fact]
    public void Build_ShouldNotValidateSignalSettingsWhenSignalIsDisabled()
    {
        // Act
        var config = Build(("TELEGRAM_BOT_TOKEN", "123:abc"), ("SIGNAL_API_URL", "not a url"));

        // Assert
        config.Signal.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Build_ShouldReadMessagingSettings()
    {
        // Act
        var config = Build(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("SESSION_TIMEOUT_MINUTES", "30"),
            ("ALLOW_NEW_USERS", "false"));

        // Assert
        config.Messaging.SessionTimeoutMinutes.Should().Be(30);
        config.Messaging.AllowNewUsers.Should().BeFalse();
    }

    [Fact]
    public void Build_ShouldReadTheAccessPasswordWithoutSurroundingWhitespace()
    {
        // Act
        var config = Build(("TELEGRAM_BOT_TOKEN", "123:abc"), ("ACCESS_PASSWORD", "  correct horse\n"));

        // Assert
        config.Messaging.AccessPassword.Should().Be("correct horse");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("1234567")]
    [InlineData("   1234567   ")]
    public void Build_ShouldRejectAShortAccessPasswordWithoutRevealingIt(string value)
    {
        // Act
        var act = () => Build(("TELEGRAM_BOT_TOKEN", "123:abc"), ("ACCESS_PASSWORD", value));

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message == "ACCESS_PASSWORD must have at least 8 characters");
    }

    [Theory]
    [InlineData("SESSION_TIMEOUT_MINUTES", "0")]
    [InlineData("SESSION_TIMEOUT_MINUTES", "-5")]
    [InlineData("SESSION_TIMEOUT_MINUTES", "soon")]
    [InlineData("SESSION_TIMEOUT_MINUTES", "10081")]
    [InlineData("SESSION_TIMEOUT_MINUTES", "2147483647")]
    [InlineData("CHECK_INTERVAL_SECONDS", "0")]
    [InlineData("CHECK_INTERVAL_SECONDS", "-60")]
    [InlineData("CHECK_INTERVAL_SECONDS", "often")]
    [InlineData("CHECK_INTERVAL_SECONDS", "1.5")]
    [InlineData("CHECK_INTERVAL_SECONDS", "86401")]
    [InlineData("CHECK_INTERVAL_SECONDS", "2147483647")]
    [InlineData("PROMETHEUS_PORT", "0")]
    [InlineData("PROMETHEUS_PORT", "65536")]
    [InlineData("PROMETHEUS_PORT", "http")]
    public void Build_ShouldRejectInvalidNumbers(string name, string value)
    {
        // Act
        var act = () => Build(("TELEGRAM_BOT_TOKEN", "123:abc"), (name, value));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"{name} must be a whole number*'{value}'");
    }

    [Theory]
    [InlineData("ALLOW_NEW_USERS", "flase")]
    [InlineData("ALLOW_NEW_USERS", "0")]
    [InlineData("ENABLE_PROMETHEUS", "yes")]
    public void Build_ShouldRejectInvalidBooleans(string name, string value)
    {
        // Act
        var act = () => Build(("TELEGRAM_BOT_TOKEN", "123:abc"), (name, value));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"{name} must be true or false*'{value}'");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_ShouldUseDefaultsForUnsetOrEmptyValues(string blankValue)
    {
        // Act
        var config = Build(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("SESSION_TIMEOUT_MINUTES", blankValue),
            ("CHECK_INTERVAL_SECONDS", blankValue),
            ("PROMETHEUS_PORT", blankValue),
            ("ALLOW_NEW_USERS", blankValue),
            ("ACCESS_PASSWORD", blankValue),
            ("ENABLE_PROMETHEUS", blankValue),
            ("DATA_PATH", null));

        // Assert
        config.Messaging.SessionTimeoutMinutes.Should().Be(15);
        config.Messaging.AllowNewUsers.Should().BeTrue();
        config.Messaging.AccessPassword.Should().BeNull();
        config.Crawler.CheckIntervalSeconds.Should().Be(60);
        config.Crawler.DataPath.Should().Be("data");
        config.Monitoring.PrometheusPort.Should().Be(9090);
        config.Monitoring.EnablePrometheus.Should().BeTrue();
    }

    [Fact]
    public void Build_ShouldReadCrawlerAndMonitoringSettings()
    {
        // Act
        var config = Build(
            ("TELEGRAM_BOT_TOKEN", "123:abc"),
            ("CHECK_INTERVAL_SECONDS", "120"),
            ("PROMETHEUS_PORT", "9100"),
            ("ENABLE_PROMETHEUS", "False"),
            ("DATA_PATH", "/var/lib/crawler"));

        // Assert
        config.Crawler.CheckIntervalSeconds.Should().Be(120);
        config.Monitoring.PrometheusPort.Should().Be(9100);
        config.Monitoring.EnablePrometheus.Should().BeFalse();
        config.Crawler.DataPath.Should().Be("/var/lib/crawler");
    }
}
