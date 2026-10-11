using System.Net.Sockets;
using Eling.Backend.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public sealed class BindFailureTests
{
    [Fact]
    public void IsAddressInUse_True_ForDirectSocketError()
    {
        // Arrange
        var ex = new SocketException((int)SocketError.AddressAlreadyInUse);

        // Act
        var actual = BindFailure.IsAddressInUse(ex);

        // Assert
        Assert.True(actual);
    }

    [Fact]
    public void IsAddressInUse_True_ForWrappedInnerException()
    {
        // Arrange
        var inner = new SocketException((int)SocketError.AddressAlreadyInUse);
        var outer = new IOException("bind failed", inner);

        // Act
        var actual = BindFailure.IsAddressInUse(outer);

        // Assert
        Assert.True(actual);
    }

    [Fact]
    public void IsAddressInUse_False_ForOtherSocketError()
    {
        // Arrange
        var ex = new SocketException((int)SocketError.ConnectionRefused);

        // Act
        var actual = BindFailure.IsAddressInUse(ex);

        // Assert
        Assert.False(actual);
    }

    [Fact]
    public void IsAddressInUse_False_ForPlainException()
    {
        // Arrange
        var ex = new InvalidOperationException("boom");

        // Act
        var actual = BindFailure.IsAddressInUse(ex);

        // Assert
        Assert.False(actual);
    }

    [Fact]
    public void Log_DoesNotThrow()
    {
        // Act — smoke: structured log call formats without throwing
        var ex = Record.Exception(() => BindFailure.Log(NullLogger.Instance, 4417));

        // Assert
        Assert.Null(ex);
    }
}
