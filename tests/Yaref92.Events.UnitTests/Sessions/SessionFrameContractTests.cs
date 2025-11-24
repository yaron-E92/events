using System;
using System.Text.Json;

using FluentAssertions;

using NUnit.Framework;

using Yaref92.Events.Sessions;

namespace Yaref92.Events.UnitTests.Sessions;

[TestFixture]
public class SessionFrameContractTests
{
    [Test]
    public void CreateSessionToken_WhenAuthenticationRequired_UsesProvidedSecret()
    {
        var sessionKey = new SessionKey(Guid.NewGuid(), "host", 5000);
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            AuthenticationToken = "configured-secret",
        };

        var token = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: "provided-secret");

        token.Should().Be($"{sessionKey}{SessionFrameContract.TokenSecretDelimiter}provided-secret");
    }

    [Test]
    public void CreateSessionToken_WhenAuthenticationNotRequired_AppendsRandomSuffix()
    {
        var sessionKey = new SessionKey(Guid.NewGuid(), "host", 5000);
        var options = new ResilientSessionOptions { RequireAuthentication = false };

        var token = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);

        token.Should().StartWith(sessionKey + SessionFrameContract.TokenSecretDelimiter);
        token[(sessionKey.ToString()!.Length + SessionFrameContract.TokenSecretDelimiter.Length)..]
            .Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void CreateAuthFrame_Includes_Secret_And_Callback_Info()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            AuthenticationToken = "configured-secret",
            CallbackHost = "callback-host",
            CallbackPort = 12345,
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "host", 4040);
        var sessionToken = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: "provided-secret");

        var frame = SessionFrameContract.CreateAuthFrame(sessionToken, options, authenticationSecret: null);

        frame.Kind.Should().Be(SessionFrameKind.Auth);
        frame.Token.Should().Be(sessionToken);
        frame.Payload.Should().NotBeNull();
        using var payload = JsonDocument.Parse(frame.Payload!);
        payload.RootElement.GetProperty("secret").GetString().Should().Be("configured-secret");
        payload.RootElement.GetProperty("callbackHost").GetString().Should().Be("callback-host");
        payload.RootElement.GetProperty("callbackPort").GetInt32().Should().Be(12345);
    }

    [Test]
    public void TryValidateAuthentication_Succeeds_ForAuthenticatedPeer()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            AuthenticationToken = "configured-secret",
            CallbackHost = "callback-host",
            CallbackPort = 1001,
        };
        var sessionKey = new SessionKey(Guid.NewGuid(), "host", 4040);
        var token = SessionFrameContract.CreateSessionToken(sessionKey, options, authenticationSecret: null);
        var frame = SessionFrameContract.CreateAuthFrame(token, options, authenticationSecret: null);

        var result = SessionFrameContract.TryValidateAuthentication(frame, options, out var validatedSessionKey, out var payload);

        result.Should().BeTrue();
        validatedSessionKey.Should().BeEquivalentTo(sessionKey);
        payload.Should().NotBeNull();
        payload!.Secret.Should().Be("configured-secret");
    }

    [Test]
    public void TryValidateAuthentication_Succeeds_ForAnonymousPeer_WithSecretRequirement()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            AuthenticationToken = "anonymous-secret",
            DoAnonymousSessionsRequireAuthentication = true,
        };
        var anonymousKey = new SessionKey(Guid.Empty, "host", 4040) { IsAnonymousKey = true };
        var token = SessionFrameContract.CreateSessionToken(anonymousKey, options, authenticationSecret: "anonymous-secret");
        var frame = SessionFrameContract.CreateAuthFrame(token, options, authenticationSecret: "anonymous-secret");

        var result = SessionFrameContract.TryValidateAuthentication(frame, options, out var validatedSessionKey, out var payload);

        result.Should().BeTrue();
        validatedSessionKey.Should().NotBeNull();
        validatedSessionKey!.IsAnonymousKey.Should().BeTrue();
        payload.Should().NotBeNull();
        payload!.Secret.Should().Be("anonymous-secret");
    }

    [Test]
    public void TryValidateAuthentication_Succeeds_ForAnonymousPeer_WithoutSecret_WhenNotRequired()
    {
        var options = new ResilientSessionOptions
        {
            RequireAuthentication = true,
            AuthenticationToken = "anonymous-secret",
            DoAnonymousSessionsRequireAuthentication = false,
        };
        const string token = "anonymous@host:4040";
        var frame = SessionFrame.CreateAuth(token, secret: null);

        var result = SessionFrameContract.TryValidateAuthentication(frame, options, out var validatedSessionKey, out var payload);

        result.Should().BeTrue();
        validatedSessionKey.Should().NotBeNull();
        validatedSessionKey!.IsAnonymousKey.Should().BeTrue();
        payload.Should().BeNull();
    }

    [Test]
    public void TryValidateAuthentication_ReturnsFalse_ForInvalidTokens()
    {
        var options = new ResilientSessionOptions { RequireAuthentication = true };
        var frame = SessionFrame.CreateAuth("invalid-token", secret: null);

        var result = SessionFrameContract.TryValidateAuthentication(frame, options, out var sessionKey, out var payload);

        result.Should().BeFalse();
        sessionKey.Should().BeNull();
        payload.Should().BeNull();
    }
}
