using System.Text.Json;

namespace Fern.Registry;

/// <summary>
/// The physical accelerator a Worker Site may spend. A Python environment is
/// not a GPU. Two profiles that observe the same NVIDIA UUID are two
/// interpreters on one device; they share that device's capacity.
/// </summary>
public static class GpuDeviceIdentity
{
    public static string? FromFingerprintJson(string fingerprintJson)
    {
        if (string.IsNullOrWhiteSpace(fingerprintJson) || fingerprintJson == "{}")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(fingerprintJson);
            if (!document.RootElement.TryGetProperty("cuda_devices", out var devices)
                || devices.ValueKind != JsonValueKind.Array
                || devices.GetArrayLength() == 0)
            {
                return null;
            }

            var selected = SelectedDevice(document.RootElement, devices);
            if (selected is null || !selected.Value.TryGetProperty("uuid", out var uuid))
            {
                return null;
            }

            var value = uuid.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static JsonElement? SelectedDevice(JsonElement root, JsonElement devices)
    {
        if (root.TryGetProperty("cuda_current_device", out var current)
            && current.ValueKind == JsonValueKind.Number
            && current.TryGetUInt32(out var currentIndex))
        {
            foreach (var device in devices.EnumerateArray())
            {
                if (device.ValueKind == JsonValueKind.Object
                    && device.TryGetProperty("index", out var index)
                    && index.ValueKind == JsonValueKind.Number
                    && index.TryGetUInt32(out var deviceIndex)
                    && deviceIndex == currentIndex)
                {
                    return device.Clone();
                }
            }
        }

        return devices[0].Clone();
    }

    public static string? RequireForGpuCapacity(string fingerprintJson, long maxGpuStages)
    {
        var uuid = FromFingerprintJson(fingerprintJson);
        if (maxGpuStages <= 0)
        {
            return uuid;
        }

        if (HasCudaDevices(fingerprintJson) && uuid is null)
        {
            throw new ArgumentException(
                "a CUDA Site with GPU capacity requires an observed GPU UUID; "
                + "a second Python environment on the same card is not a second GPU");
        }

        return uuid;
    }

    static bool HasCudaDevices(string fingerprintJson)
    {
        if (string.IsNullOrWhiteSpace(fingerprintJson) || fingerprintJson == "{}")
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(fingerprintJson);
            return document.RootElement.TryGetProperty("cuda_devices", out var devices)
                && devices.ValueKind == JsonValueKind.Array
                && devices.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
