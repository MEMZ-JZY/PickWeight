using System.Text.Json.Serialization;

namespace PickWeight.Core.Models;

public sealed class Member
{
 public string Name { get; set; } = string.Empty;
 public MemberGender? Gender { get; set; }
 public double ExplicitWeight { get; set; } = 100d;
 [JsonIgnore]
 public double ImplicitWeight { get; set; } = 1d;
 public int CalledCount { get; set; }
 public bool IsWeightLinked { get; set; } = true;
 public bool IsActive { get; set; } = true;

 /// <summary>学号，用于区分重名成员。</summary>
 public string? StudentId { get; set; }

 /// <summary>备注。</summary>
 public string? Note { get; set; }

 /// <summary>
 /// 今日请假日期。仅当值等于当天时生效，次日自动失效，无需定时清理。
 /// </summary>
 public DateOnly? AbsentOn { get; set; }

 /// <summary>
 /// 权重是否被课堂表现评价自动调整过，用于在界面上标记，避免用户困惑。
 /// </summary>
 public bool IsWeightAdjustedByRating { get; set; }

 /// <summary>当天是否请假。</summary>
 public bool IsAbsentToday() => AbsentOn == DateOnly.FromDateTime(DateTime.Today);

 public Member Clone()
 {
 return new Member
 {
 Name = Name,
 Gender = Gender,
 ExplicitWeight = ExplicitWeight,
 ImplicitWeight = ImplicitWeight,
 CalledCount = CalledCount,
 IsWeightLinked = IsWeightLinked,
 IsActive = IsActive,
 StudentId = StudentId,
 Note = Note,
 AbsentOn = AbsentOn,
 IsWeightAdjustedByRating = IsWeightAdjustedByRating
 };
 }

 /// <summary>
 /// 把另一名成员的全部字段复制到当前实例（不替换实例本身）。
 /// 用于恢复快照时保持对象引用稳定。
 /// </summary>
 public void CopyFrom(Member other)
 {
 ArgumentNullException.ThrowIfNull(other);
 Name = other.Name;
 Gender = other.Gender;
 ExplicitWeight = other.ExplicitWeight;
 ImplicitWeight = other.ImplicitWeight;
 CalledCount = other.CalledCount;
 IsWeightLinked = other.IsWeightLinked;
 IsActive = other.IsActive;
 StudentId = other.StudentId;
 Note = other.Note;
 AbsentOn = other.AbsentOn;
 IsWeightAdjustedByRating = other.IsWeightAdjustedByRating;
 }
}
