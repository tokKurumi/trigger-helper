namespace TriggerHelper.Runtime;

internal static class Wildcard
{
    public static bool HasWildcards(string pattern)
        => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

    public static bool IsMatch(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
    {
        var inputIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;

        while (inputIndex < input.Length)
        {
            if (
                (patternIndex < pattern.Length) &&
                (pattern[patternIndex] == '?' || EqualsIgnoreCase(pattern[patternIndex], input[inputIndex]))
            )
            {
                inputIndex++;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex;
                matchIndex = inputIndex;
                patternIndex++;
            }
            else if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                matchIndex++;
                inputIndex = matchIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool EqualsIgnoreCase(char left, char right)
        => (left == right) || (char.ToLowerInvariant(left) == char.ToLowerInvariant(right));
}
