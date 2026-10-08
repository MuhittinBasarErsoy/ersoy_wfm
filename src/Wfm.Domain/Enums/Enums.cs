namespace Wfm.Domain.Enums;

public enum WorkTaskStatus
{
    Draft = 0,
    Assigned = 1,
    Accepted = 2,
    EnRoute = 3,
    OnSite = 4,
    Completed = 5,
    Rejected = 6,
    Cancelled = 7,
    Failed = 8
}

public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

public enum AttachmentKind
{
    Photo = 0,
    Signature = 1,
    Document = 2
}

public enum FieldType
{
    Text = 0,
    Number = 1,
    Select = 2,
    Date = 3,
    Checkbox = 4,
    TextArea = 5,
    Phone = 6
}

public enum JobStatus
{
    Active = 0,
    /// <summary>Bir adım başarısız oldu veya reddedildi; yönetici karar verene kadar akış durur.</summary>
    OnHold = 1,
    Completed = 2,
    Cancelled = 3
}
