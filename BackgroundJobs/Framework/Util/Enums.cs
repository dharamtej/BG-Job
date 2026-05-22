namespace CareerPanda.Framework.Util;

public enum Status
{
    Success,
    Executing,
    Failed,
    InProcess
}

public enum CrudOperation
{
    Create = 1,
    Update = 2,
    Delete = 3,
    Fetch = 4
}

public enum JobStatus
{
    Pending = 0,
    InProcess = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public enum LoginProvider
{
    Internal = 0,
    Google = 1,
    External = 2
}
