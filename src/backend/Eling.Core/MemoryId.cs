using System;

namespace Eling.Core;

public readonly record struct MemoryId
{
    public string Value { get; }

    public MemoryId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!System.Ulid.TryParse(value, out _))
        {
            throw new ArgumentException("Invalid ULID format.", nameof(value));
        }
        Value = value.ToLowerInvariant();
    }

    public static MemoryId NewId() => new(System.Ulid.NewUlid().ToString().ToLowerInvariant());

    public static MemoryId Parse(string value) => new(value);

    public static bool TryParse(string? value, out MemoryId result)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.Ulid.TryParse(value, out _))
        {
            result = default;
            return false;
        }

        result = new MemoryId(value);
        return true;
    }

    public override string ToString() => Value;
}
