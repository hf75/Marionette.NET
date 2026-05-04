// Sample.WinUI.FormLab - FormState
//
// Plain data class representing a snapshot of the form's submitted state.
// Used as the FormSubmittedEventArgs payload so subscribers to the
// FormSubmitted event receive the full snapshot in one push.

namespace Sample.WinUI.FormLab.Models;

/// <summary>
/// Read-only snapshot of the form's submitted state.
/// </summary>
public sealed class FormState
{
    public FormState(string name, int age, bool notificationsEnabled, string theme)
    {
        Name = name;
        Age = age;
        NotificationsEnabled = notificationsEnabled;
        Theme = theme;
    }

    /// <summary>The user's display name at the time of submission.</summary>
    public string Name { get; }
    /// <summary>The user's age at the time of submission.</summary>
    public int Age { get; }
    /// <summary>Whether notifications are enabled at the time of submission.</summary>
    public bool NotificationsEnabled { get; }
    /// <summary>The theme selected at the time of submission ("Light", "Dark", or "Default").</summary>
    public string Theme { get; }
}
