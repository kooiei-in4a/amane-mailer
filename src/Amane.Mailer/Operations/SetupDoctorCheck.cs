namespace Amane.Mailer.Operations;

public enum SetupDoctorResultCode
{
    Pass,
    Fail,
    Warn,
    Action,
}

public sealed record SetupDoctorCheck(
    SetupDoctorResultCode Code,
    string CheckId,
    string Message);

public sealed class SetupDoctorReport
{
    private readonly List<SetupDoctorCheck> _checks = [];

    public IReadOnlyList<SetupDoctorCheck> Checks => _checks;

    public void Add(SetupDoctorResultCode code, string checkId, string message) =>
        _checks.Add(new SetupDoctorCheck(code, checkId, message));

    public void AddPass(string checkId, string message) =>
        Add(SetupDoctorResultCode.Pass, checkId, message);

    public void AddFail(string checkId, string message) =>
        Add(SetupDoctorResultCode.Fail, checkId, message);

    public void AddWarn(string checkId, string message) =>
        Add(SetupDoctorResultCode.Warn, checkId, message);

    public void AddAction(string checkId, string message) =>
        Add(SetupDoctorResultCode.Action, checkId, message);

    public bool HasFailure => _checks.Any(check => check.Code == SetupDoctorResultCode.Fail);

    public int PassCount => _checks.Count(check => check.Code == SetupDoctorResultCode.Pass);

    public int FailCount => _checks.Count(check => check.Code == SetupDoctorResultCode.Fail);

    public int WarnCount => _checks.Count(check => check.Code == SetupDoctorResultCode.Warn);

    public int ActionCount => _checks.Count(check => check.Code == SetupDoctorResultCode.Action);
}
