using System.Text.RegularExpressions;

namespace AIWorld.Core.Chat;

/// <summary>Untrusted generated speech must never become Stardew dialogue commands.</summary>
public static class DialoguePresentationPolicy
{
    public static bool CanRewrite(bool eventActive, bool question, bool callbacks, bool sideEffects,
        int lineCount, string? translationKey, string text) => !eventActive && !question && !callbacks && !sideEffects
        && lineCount == 1 && !string.IsNullOrWhiteSpace(text) && text.Length <= 800
        && translationKey?.Replace('\\', '/').StartsWith("Characters/Dialogue/", StringComparison.OrdinalIgnoreCase) == true
        && !Regex.IsMatch(Regex.Replace(text, @"\$(?:neutral|h|s|u|l|a)(?=\s|$)", ""), @"[$%#{}^¦*]");

    public static bool IsPlainSpeech(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 480
        && !text.Any(c => char.IsControl(c) || "$%#{}^¦*@".Contains(c));

    // For displaying an existing chat response in the native renderer, remove its command alphabet.
    // This is presentation only; the unchanged transcript remains available in the chat UI.
    public static string NativeText(string text) => ContextBudget.Clip(new string(text.Select(c =>
        char.IsControl(c) || "$%#{}^¦*@".Contains(c) ? ' ' : c).ToArray()), 480);
}
