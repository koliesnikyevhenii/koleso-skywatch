namespace Skywatch.Core.Filtering;

/// <summary>A range of UTF-16 code units in a string: the unit Telegram message entities use.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}
