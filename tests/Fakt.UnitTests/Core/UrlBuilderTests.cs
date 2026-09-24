using System;
using System.Collections.Generic;
using Fakt.Core.Llm;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class UrlBuilderTests
{
    [Theory]
    [InlineData("http://host:1234/v1", "/v1/models", "http://host:1234/v1/models")]
    [InlineData("http://host:1234/v1/", "models", "http://host:1234/v1/models")]
    [InlineData("http://host:1234/v1", "v1", "http://host:1234/v1")]
    [InlineData("https://api.example.test/v1", "chat/completions", "https://api.example.test/v1/chat/completions")]
    [InlineData("https://res.example.test", "openai/v1/chat/completions", "https://res.example.test/openai/v1/chat/completions")]
    [InlineData("https://res.example.test/openai/v1", "openai/v1/models", "https://res.example.test/openai/v1/models")]
    [InlineData("https://gw.example.test/openai", "openai/v1/models", "https://gw.example.test/openai/v1/models")]
    [InlineData("http://host/V1", "v1/models", "http://host/V1/models")]
    [InlineData("http://host:11434/api", "api/chat", "http://host:11434/api/chat")]
    [InlineData("http://host", "", "http://host")]
    public void Combine_DoesNotDuplicateOverlappingSegments(string baseUrl, string relative, string expected)
    {
        Assert.Equal(expected, UrlBuilder.Combine(baseUrl, relative));
    }

    [Fact]
    public void Combine_OnlyRemovesOverlapBetweenBaseTailAndRelativeHead()
    {
        // «v1» в конце относительного пути не совпадает с хвостом базового — это другой путь.
        Assert.Equal("http://host/v1/models/v1", UrlBuilder.Combine("http://host/v1", "models/v1"));
        Assert.Equal("http://host/api/v1/models", UrlBuilder.Combine("http://host/api/v1", "v1/models"));
    }

    [Fact]
    public void Combine_MergesBaseQueryRelativeQueryAndEscapedParameters()
    {
        var url = UrlBuilder.Combine(
            "https://res.example.test/openai?api-version=2024-10-21",
            "models?limit=5",
            new[]
            {
                new KeyValuePair<string, string>("after", "model a/b"),
                new KeyValuePair<string, string>("skipped", null),
            });

        Assert.Equal("https://res.example.test/openai/models?api-version=2024-10-21&limit=5&after=model%20a%2Fb", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Combine_RequiresBaseUrl(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => UrlBuilder.Combine(baseUrl, "models"));
    }

    [Theory]
    [InlineData("https://API.Example.test/v1", "api.example.test")]
    [InlineData("https://api.example.test:443/v1", "api.example.test")]
    [InlineData("http://api.example.test:80", "api.example.test")]
    [InlineData("https://api.example.test:8443/v1", "api.example.test:8443")]
    [InlineData("http://api.example.test:443", "api.example.test:443")]
    [InlineData("http://localhost:11434", "localhost:11434")]
    [InlineData("  http://127.0.0.1:1234/v1  ", "127.0.0.1:1234")]
    public void HostOf_ReturnsLowercaseHostWithNonDefaultPort(string url, string expected)
    {
        Assert.Equal(expected, UrlBuilder.HostOf(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("api.example.test/v1")]
    public void HostOf_InvalidUrl_ReturnsNull(string url)
    {
        Assert.Null(UrlBuilder.HostOf(url));
    }

    [Theory]
    [InlineData("https://api.example.test/v1")]
    [InlineData("http://127.0.0.1:1234")]
    [InlineData("  https://api.example.test  ")]
    public void IsValidHttpUrl_AcceptsHttpAndHttps(string url)
    {
        Assert.True(UrlBuilder.IsValidHttpUrl(url, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, "не задан")]
    [InlineData("  ", "не задан")]
    [InlineData("ftp://files.example.test", "http://")]
    [InlineData("file:///C:/models", "http://")]
    [InlineData("api.example.test/v1", "http://")]
    [InlineData("https://user:secret@api.example.test/v1", "Учётные данные")]
    public void IsValidHttpUrl_RejectsWithExplanation(string url, string expectedFragment)
    {
        Assert.False(UrlBuilder.IsValidHttpUrl(url, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://127.10.20.30/")]
    [InlineData("http://10.0.0.5:8000/v1")]
    [InlineData("http://192.168.1.20:11434")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://169.254.10.10/")]
    [InlineData("http://gpu-server:8000/v1")]
    [InlineData("http://llm.local:1234")]
    [InlineData("http://ollama.lan/")]
    [InlineData("https://models.internal/v1")]
    [InlineData("https://ai.corp/v1")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd12:3456::1]/")]
    public void IsLocalOrPrivate_TrueForLoopbackPrivateAndIntranetHosts(string url)
    {
        Assert.True(UrlBuilder.IsLocalOrPrivate(url));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.example.test")]
    [InlineData("http://172.15.0.1/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://192.169.0.1/")]
    [InlineData("http://11.0.0.1/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("https://[2001:db8::1]/")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void IsLocalOrPrivate_FalseForPublicOrInvalidAddresses(string url)
    {
        Assert.False(UrlBuilder.IsLocalOrPrivate(url));
    }
}
