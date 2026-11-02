using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Registry;

/// <summary>
/// String identities that must not be interchangeable. A plan id passed where a
/// digest belongs is a compile error, not a wrong row that looks right.
/// </summary>

[JsonConverter(typeof(ImplementationIdConverter))]
public readonly record struct ImplementationId
{
    public string Value { get; }

    public ImplementationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("implementation id must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

[JsonConverter(typeof(BindingIdConverter))]
public readonly record struct BindingId
{
    public string Value { get; }

    public BindingId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("binding id must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

[JsonConverter(typeof(SnapshotIdConverter))]
public readonly record struct SnapshotId
{
    public string Value { get; }

    public SnapshotId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("snapshot id must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

[JsonConverter(typeof(StageIdConverter))]
public readonly record struct StageId
{
    public string Value { get; }

    public StageId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("stage id must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

[JsonConverter(typeof(StagePlanInstanceIdConverter))]
public readonly record struct StagePlanInstanceId
{
    public string Value { get; }

    public StagePlanInstanceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("stage plan instance id must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

[JsonConverter(typeof(ContentDigestConverter))]
public readonly record struct ContentDigest
{
    public string Value { get; }

    public ContentDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("content digest must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

[JsonConverter(typeof(TargetHashConverter))]
public readonly record struct TargetHash
{
    public string Value { get; }

    public TargetHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryFault("target hash must be a non-empty name");
        }

        Value = value;
    }

    public override string ToString() => Value;
}

file static class IdentityJson
{
    internal static string Read(ref Utf8JsonReader reader, string name) =>
        reader.GetString() ?? throw new JsonException($"{name} was null");
}


file sealed class ImplementationIdConverter : JsonConverter<ImplementationId>
{
    public override ImplementationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "implementation id"));

    public override void Write(Utf8JsonWriter writer, ImplementationId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

file sealed class BindingIdConverter : JsonConverter<BindingId>
{
    public override BindingId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "binding id"));

    public override void Write(Utf8JsonWriter writer, BindingId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

file sealed class SnapshotIdConverter : JsonConverter<SnapshotId>
{
    public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "snapshot id"));

    public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

file sealed class StageIdConverter : JsonConverter<StageId>
{
    public override StageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "stage id"));

    public override void Write(Utf8JsonWriter writer, StageId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

file sealed class StagePlanInstanceIdConverter : JsonConverter<StagePlanInstanceId>
{
    public override StagePlanInstanceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "stage plan instance id"));

    public override void Write(Utf8JsonWriter writer, StagePlanInstanceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

file sealed class ContentDigestConverter : JsonConverter<ContentDigest>
{
    public override ContentDigest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "content digest"));

    public override void Write(Utf8JsonWriter writer, ContentDigest value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

file sealed class TargetHashConverter : JsonConverter<TargetHash>
{
    public override TargetHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(IdentityJson.Read(ref reader, "target hash"));

    public override void Write(Utf8JsonWriter writer, TargetHash value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// Who performed a Registry operation: the party a transition receipt names.
///
/// <c>Kind</c> is the class of actor (an operator, a scheduler, a worker),
/// <c>ActorId</c> its stable name, and the host and pid are where it ran. It
/// carries no authority of its own; it is the answer to "who did this" that
/// every journal entry records.
/// </summary>
public sealed record RegistryActor(string Kind, string ActorId, string OwnerHost, long OwnerPid);
