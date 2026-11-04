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
public sealed record ImportBundleRequest(string BundleJson) : IpcMessage;
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
            return JsonSerializer.Deserialize<IpcMessage>(json, Options)
                ?? throw new IpcFormatException("IPC message is null");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentNullException)
        {
            throw new IpcFormatException("Invalid IPC message", e);
        }
    }
}
