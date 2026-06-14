// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
///     Abstraction over the user-facing UI (alerts, prompts, notifications).
///     On Windows the original code called <c>MessageBox.Show</c> directly from the network
///     engine. To keep the (fully portable) engine free of any UI-framework dependency, every
///     such call now goes through this interface, implemented by the Avalonia layer
///     (<see cref="AvaloniaUserInterface" />). All implementations marshal to the UI thread
///     internally, so the engine may call these from any background task.
/// </summary>
internal interface IUserInterface
{
    /// <summary>Show an informational alert with a single OK button.</summary>
    Task ShowInfoAsync(string message);

    /// <summary>Show an error alert with a single OK button.</summary>
    Task ShowErrorAsync(string message);

    /// <summary>Show a Yes/No question. Returns <c>true</c> when the user chose Yes.</summary>
    Task<bool> ShowYesNoAsync(string message);

    /// <summary>Show a Retry/Cancel error. Returns <c>true</c> when the user chose Retry.</summary>
    Task<bool> ShowRetryCancelAsync(string message);

    /// <summary>Show an OK/Cancel prompt. Returns <c>true</c> when the user chose OK.</summary>
    Task<bool> ShowOkCancelAsync(string message);

    /// <summary>
    ///     Show the "which game do you want to launch?" prompt. Returns the chosen game, or
    ///     <see cref="LaunchGame.Prompt" /> if the user cancelled without choosing.
    /// </summary>
    Task<LaunchGame> ShowGamePromptAsync(LaunchGame initialGame, bool rememberDefault);

    /// <summary>Open the given URL in the user's default browser.</summary>
    void OpenUrl(string url);

    /// <summary>Post a transient notification (replaces the Windows tray balloon tip).</summary>
    void Notify(string message);
}
