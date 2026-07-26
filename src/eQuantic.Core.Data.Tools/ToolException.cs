namespace eQuantic.Core.Data.Tools;

/// <summary>
///     A failure the person running the tool can act on. It is reported as its message and nothing else — a stack
///     trace here would describe the tool's problem, not theirs.
/// </summary>
/// <param name="message">What went wrong, and what to do about it.</param>
internal sealed class ToolException(string message) : Exception(message);
