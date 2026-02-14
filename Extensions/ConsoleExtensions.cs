namespace Siphon.Extensions;

/// <summary>
/// Static helper class for interactive console prompts.
/// </summary>
public static class ConsoleExtensions
{
    /// <summary>
    /// Prompts the user for a string value.
    /// </summary>
    /// <param name="prompt">The prompt message to display.</param>
    /// <param name="required">If true and input is empty, keeps prompting until a value is entered.</param>
    /// <returns>The entered string value.</returns>
    public static string PromptString(string prompt, bool required = true)
    {
        while (true)
        {
            Console.Write($"{prompt}: ");
            var input = Console.ReadLine()?.Trim() ?? string.Empty;

            if (!required || !string.IsNullOrEmpty(input))
            {
                return input;
            }

            WriteError("This value is required. Please try again.");
        }
    }

    /// <summary>
    /// Prompts for a password with masked input (shows * for each character).
    /// </summary>
    /// <param name="prompt">The prompt message to display.</param>
    /// <returns>The entered password.</returns>
    public static string PromptPassword(string prompt)
    {
        Console.Write($"{prompt}: ");
        var password = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    /// <summary>
    /// Prompts for a Y/N response.
    /// </summary>
    /// <param name="prompt">The prompt message to display.</param>
    /// <param name="defaultYes">If true, default is Y; otherwise default is N.</param>
    /// <returns>True for Y, false for N.</returns>
    public static bool PromptYesNo(string prompt, bool defaultYes = true)
    {
        var hint = defaultYes ? "[Y/n]" : "[y/N]";

        while (true)
        {
            Console.Write($"{prompt} {hint}: ");
            var input = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(input))
            {
                return defaultYes;
            }

            if (input == "Y" || input == "YES")
            {
                return true;
            }

            if (input == "N" || input == "NO")
            {
                return false;
            }

            WriteError("Please enter Y or N.");
        }
    }

    /// <summary>
    /// Writes an info message in cyan.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public static void WriteInfo(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    /// <summary>
    /// Writes a success message in green.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public static void WriteSuccess(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    /// <summary>
    /// Writes a warning message in yellow.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public static void WriteWarning(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    /// <summary>
    /// Writes an error message in red.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public static void WriteError(string message)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }

    /// <summary>
    /// Shows progress like [50/100] or [50/100] Table: Customers.
    /// </summary>
    /// <param name="current">The current progress value.</param>
    /// <param name="total">The total value.</param>
    /// <param name="message">Optional message to append.</param>
    public static void WriteProgress(int current, int total, string? message = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            Console.WriteLine($"[{current}/{total}]");
        }
        else
        {
            Console.WriteLine($"[{current}/{total}] {message}");
        }
    }
}