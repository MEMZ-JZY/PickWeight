namespace PickWeight.Core.Models;

/// <summary>
/// 单次抽取中某一成员的明细记录，含课堂表现评价。
/// </summary>
public sealed class DrawRecord
{
    public string Name { get; set; } = string.Empty;

    /// <summary>课堂表现评价。</summary>
    public DrawRating Rating { get; set; } = DrawRating.None;

    /// <summary>评价备注。</summary>
    public string? Note { get; set; }

    public DrawRecord Clone() => new()
    {
        Name = Name,
        Rating = Rating,
        Note = Note
    };
}

/// <summary>
/// 课堂表现评价等级。
/// </summary>
public enum DrawRating
{
    /// <summary>未评价。</summary>
    None = 0,

    /// <summary>👍 回答优秀。</summary>
    Excellent = 1,

    /// <summary>👌 回答一般。</summary>
    Fair = 2,

    /// <summary>👎 未答上来。</summary>
    NoAnswer = 3,

    /// <summary>🙋 主动举手回答。</summary>
    Volunteered = 4
}
