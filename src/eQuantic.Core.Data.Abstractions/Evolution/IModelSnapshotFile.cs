namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     The generated snapshot file, committed beside the changes it explains. It is C# rather than data so that
///     it compiles with the model it describes — a member renamed in code and forgotten here stops being a silent
///     mismatch and becomes a build error — and so that a reviewer reads the change in a diff, in the same
///     language as everything else in the pull request.
/// </summary>
public interface IModelSnapshotFile
{
    /// <summary>The model as it stood when the last change was generated.</summary>
    ModelSnapshot Model { get; }
}
