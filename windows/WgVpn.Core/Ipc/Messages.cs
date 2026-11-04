// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 National Institute of Technology, Asahikawa College New-Tech-Club

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgVpn.Core.Ipc;

/// <summary>Thrown by <see cref="IpcSerializer.Deserialize"/> for malformed JSON or an unknown message type.</summary>
public sealed class IpcFormatException : Exception
{
    public IpcFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

// The discriminator must be the first JSON property: System.Text.Json rejects a message whose
// "type" comes later. Serialize writes it first; hand-written JSON has to do the same.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectRequest), "connect")]
[JsonDerivedType(typeof(DisconnectRequest), "disconnect")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(ImportBundleRequest), "importBundle")]
[JsonDerivedType(typeof(ImportBundleReply), "importBundleReply")]
[JsonDerivedType(typeof(StatusReply), "status")]
public abstract record IpcMessage;

public sealed record ConnectRequest : IpcMessage;
public sealed record DisconnectRequest : IpcMessage;
public sealed record GetStatusRequest : IpcMessage;
public sealed record ImportBundleRequest(string BundleJson) : IpcMessage
{
    /// <summary>BundleJson holds the WireGuard private key, so it never reaches logs or exception text.</summary>
    public override string ToString() => "ImportBundleRequest { BundleJson = [redacted] }";
}

public sealed record ImportBundleReply(bool Ok, string? Error) : IpcMessage;
public sealed record StatusReply(TunnelState State, string? ServerHost, string? LastError) : IpcMessage;

public static class IpcSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Serialize(IpcMessage message) =>
        JsonSerializer.Serialize(message, Options);

    /// <exception cref="IpcFormatException">Malformed input or unknown discriminator.</exception>
    public static IpcMessage Deserialize(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<IpcMessage>(json, Options)
                ?? throw new IpcFormatException("IPC message is null");
            // System.Text.Json does not enforce non-nullable constructor parameters.
            if (message is ImportBundleRequest { BundleJson: null })
                throw new IpcFormatException("ImportBundleRequest has no BundleJson");
            return message;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentNullException)
        {
            throw new IpcFormatException("Invalid IPC message", e);
        }
    }
}
