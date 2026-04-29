// McpWarmupState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace SaddleRAG.Mcp;

public sealed class McpWarmupState
{
    public string Status { get; private set; } = StatusNotStarted;

    public string CurrentPhase { get; private set; } = PhaseIdle;

    public string? LastError { get; private set; }
    private readonly object mLock = new object();

    public void MarkStarted(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock(mLock)
        {
            Status = StatusRunning;
            CurrentPhase = phase;
            LastError = null;
        }
    }

    public void MarkPhase(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock(mLock)
        {
            CurrentPhase = phase;
        }
    }

    public void MarkCompleted(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock(mLock)
        {
            Status = StatusCompleted;
            CurrentPhase = phase;
            LastError = null;
        }
    }

    public void MarkFailed(string phase, string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        ArgumentException.ThrowIfNullOrEmpty(error);
        lock(mLock)
        {
            Status = StatusFailed;
            CurrentPhase = phase;
            LastError = error;
        }
    }

    private const string StatusNotStarted = "NotStarted";
    private const string StatusRunning = "Running";
    private const string StatusCompleted = "Completed";
    private const string StatusFailed = "Failed";
    private const string PhaseIdle = "Idle";
}
