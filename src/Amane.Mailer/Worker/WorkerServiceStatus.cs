namespace Amane.Mailer.Worker;

public sealed class WorkerServiceStatus
{
    private int _workerRunning;
    private int _sweepRunning;

    public bool IsWorkerRunning => Volatile.Read(ref _workerRunning) == 1;

    public bool IsSweepRunning => Volatile.Read(ref _sweepRunning) == 1;

    public void SetWorkerRunning(bool running) => Volatile.Write(ref _workerRunning, running ? 1 : 0);

    public void SetSweepRunning(bool running) => Volatile.Write(ref _sweepRunning, running ? 1 : 0);
}
