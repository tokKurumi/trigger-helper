using System.Collections.Frozen;
using Sharp.Shared.Types;
using TriggerHelper.Config;

namespace TriggerHelper.Runtime;

internal sealed class CompiledGlowConfig
{
    private readonly FrozenDictionary<string, CompiledGlowEntry> _includeExact;
    private readonly string[] _includeWildcardPatterns;
    private readonly CompiledGlowEntry[] _includeWildcardEntries;

    private readonly FrozenSet<string> _excludeExact;
    private readonly string[] _excludeWildcard;

    private readonly Dictionary<string, CompiledGlowEntry?> _decision;

    public int EntityCount => _includeExact.Count + _includeWildcardEntries.Length;

    public int ExcludePatternCount => _excludeExact.Count + _excludeWildcard.Length;

    public CompiledGlowConfig(GlowConfig source)
    {
        var color = new Color32
        (
            source.Style.Color.R,
            source.Style.Color.G,
            source.Style.Color.B,
            source.Style.Color.A
        );

        var colorVector = new Vector
        (
            source.Style.Color.R / 255f,
            source.Style.Color.G / 255f,
            source.Style.Color.B / 255f
        );

        var exactBuilder = new Dictionary<string, CompiledGlowEntry>(StringComparer.OrdinalIgnoreCase);
        var wildcardPatterns = new List<string>();
        var wildcardEntries = new List<CompiledGlowEntry>();

        foreach (var raw in source.Entities)
        {
            if (string.IsNullOrWhiteSpace(raw.Name))
            {
                continue;
            }

            var compiled = new CompiledGlowEntry
            (
                raw.GlowType,
                raw.MinGlowRange,
                raw.MaxGlowRange,
                raw.MinSize,
                raw.MaxSize,
                color,
                colorVector
            );

            var name = raw.Name.Trim();

            if (Wildcard.HasWildcards(name))
            {
                wildcardPatterns.Add(name);
                wildcardEntries.Add(compiled);
            }
            else
            {
                // First declaration wins on collision — matches the wildcard "first-match" rule.
                exactBuilder.TryAdd(name, compiled);
            }
        }

        _includeExact = exactBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _includeWildcardPatterns = wildcardPatterns.ToArray();
        _includeWildcardEntries = wildcardEntries.ToArray();

        (_excludeExact, _excludeWildcard) = SplitExcludePatterns(source.Exclude);

        _decision = new Dictionary<string, CompiledGlowEntry?>(StringComparer.OrdinalIgnoreCase);
    }

    public CompiledGlowEntry? Match(string classname)
    {
        if (_decision.TryGetValue(classname, out var cached))
        {
            return cached;
        }

        var result = ComputeDecision(classname);
        _decision[classname] = result;

        return result;
    }

    private CompiledGlowEntry? ComputeDecision(string classname)
    {
        if (_excludeExact.Contains(classname) || MatchesAny(classname, _excludeWildcard))
        {
            return null;
        }

        if (_includeExact.TryGetValue(classname, out var exactEntry))
        {
            return exactEntry;
        }

        var input = classname.AsSpan();

        for (var index = 0; index < _includeWildcardPatterns.Length; index++)
        {
            if (Wildcard.IsMatch(input, _includeWildcardPatterns[index].AsSpan()))
            {
                return _includeWildcardEntries[index];
            }
        }

        return null;
    }

    private static bool MatchesAny(string classname, string[] patterns)
    {
        if (patterns.Length == 0)
        {
            return false;
        }

        var input = classname.AsSpan();

        for (var index = 0; index < patterns.Length; index++)
        {
            if (Wildcard.IsMatch(input, patterns[index].AsSpan()))
            {
                return true;
            }
        }

        return false;
    }

    private static (FrozenSet<string> Exact, string[] Wildcard) SplitExcludePatterns(List<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return (FrozenSet<string>.Empty, []);
        }

        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wildcard = new List<string>();

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();

            if (Wildcard.HasWildcards(trimmed))
            {
                wildcard.Add(trimmed);
            }
            else
            {
                exact.Add(trimmed);
            }
        }

        return (exact.ToFrozenSet(StringComparer.OrdinalIgnoreCase), wildcard.ToArray());
    }
}
