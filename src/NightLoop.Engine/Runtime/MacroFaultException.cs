namespace NightLoop.Engine.Runtime;

/// <summary>A problem that stops the run and is shown to the user on the block that caused it.</summary>
public sealed class MacroFaultException(string message) : Exception(message);
