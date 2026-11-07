using NightLoop.Engine.Input;
using NightLoop.Engine.Screen;
using NightLoop.Engine.Window;

namespace NightLoop.Engine.Runtime;

/// <summary>Everything a run needs from the outside world. Only <see cref="Input"/> is required.</summary>
public sealed class RunEnvironment
{
    public required IInputSender Input { get; init; }
    public IDelay Delay { get; init; } = PreciseDelay.Instance;
    public IWindowService? Windows { get; init; }
    public IConditionEvaluator Conditions { get; init; } = new BasicConditionEvaluator();
    public Random Random { get; init; } = Random.Shared;

    /// <summary>
    /// Held for each input block so two macros running together can't interleave mid-combo.
    /// <see cref="RunManager"/> gives every run the same one.
    /// </summary>
    public SemaphoreSlim InputLock { get; init; } = new(1, 1);

    /// <summary>Reads pixels for pixel checks, picture and text finding, and screenshots. Null means those blocks fault.</summary>
    public IScreenCapture? Screen { get; init; }
    /// <summary>Reads text for Wait for text, Find &amp; click text and "Text visible". Null means those fault clearly.</summary>
    public ITextReader? Text { get; init; }
    /// <summary>Whether a key is held right now (Wait for key, "Key held"). Takes a virtual-key code.</summary>
    public Func<int, bool>? IsKeyDown { get; init; }

    /// <summary>utility.notify. Null means the block does nothing.</summary>
    public Action<string>? Notify { get; init; }
    /// <summary>utility.playSound, and Notify's sound.</summary>
    public Action<string>? PlaySound { get; init; }
    /// <summary>utility.screenshot: saves the picture under the given file name (the app picks the folder).</summary>
    public Action<ScreenImage, string>? SaveScreenshot { get; init; }
    /// <summary>Pause and Resume macro: finds another running macro by name.</summary>
    public Func<string, MacroRun?>? FindRun { get; init; }
    /// <summary>Pause and Resume macro with "All other macros".</summary>
    public Func<IReadOnlyList<MacroRun>>? ActiveRuns { get; init; }
    /// <summary>utility.runMacro: runs another macro by name and completes when it ends.</summary>
    public Func<string, CancellationToken, Task>? RunMacro { get; init; }
    public Action<string>? Log { get; init; }
    /// <summary>Settings › Macros' wait after clicks and after every block. Null means no extra waits.</summary>
    public BlockWaits? Waits { get; init; }
}

/// <summary>Per-run settings chosen by whoever starts the run.</summary>
public sealed class RunSettings
{
    /// <summary>The target game's exe name. Null means no target lock ("N/A").</summary>
    public string? TargetExe { get; init; }
    /// <summary>Run from here: the block index to start at.</summary>
    public int StartIndex { get; init; }
    /// <summary>When true the runner pauses before blocks marked breakpoint.</summary>
    public bool Debug { get; init; }
    /// <summary>Step: hold before the first block, as if at a breakpoint. StepOnce runs one block at a time.</summary>
    public bool StartPaused { get; init; }
    /// <summary>Faults when this many steps run with no wait or input in between.</summary>
    public int RunawayStepLimit { get; init; } = 10_000;
    /// <summary>How often to re-check for the game window while waiting for it.</summary>
    public int WindowPollMs { get; init; } = 100;
}
