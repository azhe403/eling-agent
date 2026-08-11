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
        Value = value;
    }

    public static MemoryId NewId() => new(System.Ulid.NewUlid().ToString());

    public static MemoryId Parse(string value) => new(value);

    public override string ToString() => Value;
}
