namespace NightLoop.Engine.Runtime;

/// <summary>Runner states from schema section 7.</summary>
public enum RunState
{
    Idle,
    Running,
    Paused,
    AtBreakpoint,
    WaitingForWindow,
    Stopped,
    Faulted,
}
