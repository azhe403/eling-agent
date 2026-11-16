namespace Eling.Core.Skill;

public readonly record struct SkillId
{
    public string Value { get; }

    public SkillId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!System.Ulid.TryParse(value, out _))
        {
            throw new ArgumentException("Invalid ULID format.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public static SkillId NewId() => new(System.Ulid.NewUlid().ToString().ToLowerInvariant());

    public static SkillId Parse(string value) => new(value);

    public static bool TryParse(string? value, out SkillId result)
    {
        if (string.IsNullOrWhiteSpace(value) || !System.Ulid.TryParse(value, out _))
        {
            result = default;
            return false;
        }

        result = new SkillId(value);
        return true;
    }

    public override string ToString() => Value;
}
