using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;

namespace Eling.Core;

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> as an ISO-8601 scalar (base behavior),
/// but tolerates the legacy object-dump form that YamlDotNet emitted before the
/// converter was registered:
/// <code>
/// created_at: &amp;o0
///   utc_date_time: 2026-08-13T15:27:02.7938172Z
///   offset: 00:00:00
///   ...
/// </code>
/// Files written that way (or with an alias like <c>updated_at: *o0</c>) still parse
/// correctly; re-saving normalizes them to the scalar form.
/// </summary>
public sealed class LegacyTolerantDateTimeOffsetConverter : DateTimeOffsetConverter
{
    public override object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        // Standard scalar form: created_at: 2026-08-13T15:27:02.7938172Z
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return DateTimeOffset.Parse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        // Legacy object-dump form: a mapping of DateTimeOffset public properties.
        if (parser.TryConsume<MappingStart>(out _))
        {
            string? utcDateTime = null;
            string? ticks = null;
            string? offset = null;

            while (true)
            {
                if (parser.TryConsume<MappingEnd>(out _))
                {
                    break;
                }

                if (!parser.TryConsume<Scalar>(out var key))
                {
                    throw new InvalidDataException("Unsupported DateTimeOffset YAML representation: expected mapping key.");
                }

                if (parser.TryConsume<Scalar>(out var value))
                {
                    switch (key.Value)
                    {
                        case "utc_date_time":
                            utcDateTime = value.Value;
                            break;
                        case "ticks":
                            ticks = value.Value;
                            break;
                        case "offset":
                            offset = value.Value;
                            break;
                    }
                }
                else if (parser.TryConsume<MappingStart>(out _))
                {
                    SkipToEnd(parser, isSequence: false);
                }
                else if (parser.TryConsume<SequenceStart>(out _))
                {
                    SkipToEnd(parser, isSequence: true);
                }
                else
                {
                    throw new InvalidDataException("Unsupported DateTimeOffset YAML representation: expected mapping value.");
                }
            }

            if (utcDateTime is not null)
            {
                return DateTimeOffset.Parse(utcDateTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (ticks is not null)
            {
                var timeSpan = TimeSpan.Parse(offset ?? "00:00:00", CultureInfo.InvariantCulture);
                return new DateTimeOffset(long.Parse(ticks, CultureInfo.InvariantCulture), timeSpan);
            }
        }

        throw new InvalidDataException("Unsupported DateTimeOffset YAML representation.");
    }

    private static void SkipToEnd(IParser parser, bool isSequence)
    {
        while (true)
        {
            if (isSequence ? parser.TryConsume<SequenceEnd>(out _) : parser.TryConsume<MappingEnd>(out _))
            {
                return;
            }

            if (parser.TryConsume<Scalar>(out _))
            {
                continue;
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                SkipToEnd(parser, isSequence: false);
                continue;
            }

            if (parser.TryConsume<SequenceStart>(out _))
            {
                SkipToEnd(parser, isSequence: true);
            }
        }
    }
}

