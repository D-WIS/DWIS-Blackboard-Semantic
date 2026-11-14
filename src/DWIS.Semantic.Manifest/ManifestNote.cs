namespace DWIS.Semantic.Manifest;

/// <summary>How serious a note is. Mirrors the severities of the report (Appendix C.9).</summary>
public enum NoteSeverity
{
    /// <summary>Worth knowing, nothing to fix.</summary>
    Info,

    /// <summary>Accepted, but something was dropped or guessed.</summary>
    Warning,

    /// <summary>The item could not be converted.</summary>
    Error,
}

/// <summary>One thing the preparation or the conversion wants the provider to know.</summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">A stable code, kebab-case, for tests and tooling.</param>
/// <param name="Message">A sentence for people.</param>
/// <param name="Subject">The manifest item concerned (a variable id, a node name, a reference), when there is one.</param>
public sealed record ManifestNote(NoteSeverity Severity, string Code, string Message, string? Subject = null);
