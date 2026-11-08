using System.Globalization;
using System.Runtime.InteropServices;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Agent.Approval;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Windows;

internal static class ApartmentThread
{
    public static Task<T> Run<T>(Func<T> action) => Run(ApartmentState.STA, action);

    // Clipboard APIs require STA; a thread-pool thread cannot guarantee that apartment state.
    // UI Automation objects must live in the MTA: each call runs on a new thread, and STA-bound
    // objects reach later threads only as proxies, which UIA rejects with E_INVALIDARG.
    public static Task<T> Run<T>(ApartmentState apartment, Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dpi = SetThreadDpiAwarenessContext(new nint(-4));
            try
            {
                completion.SetResult(action());
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
            finally
            {
                if (dpi != nint.Zero)
                {
                    SetThreadDpiAwarenessContext(dpi);
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(apartment);
        thread.Start();
        return completion.Task;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}

public sealed class UtilityTools : IApprovalRule
{
    public bool AppliesTo(string toolName) => toolName == "clipboard_get";

    public ApprovalDecision Decide(FunctionCallContent call) => new(ApprovalOutcome.Ask,
                                                                    false,
                                                                    "Clipboard text may contain sensitive information and will be sent to the configured model.");

    [Tool("clipboard_get",
          "utilities",
          "Read clipboard text. Always requires approval because the clipboard may contain sensitive information.",
          RiskLevel.Change)]
    public Task<string> ClipboardGet() => ApartmentThread.Run(() => Win32Clipboard.GetText() ?? "No text on the clipboard.");

    [Tool("clipboard_set",
          "utilities",
          "Replace clipboard text with the supplied text.",
          RiskLevel.Change)]
    public Task<string> ClipboardSet(string text)
    {
        return ApartmentThread.Run(() =>
        {
            Win32Clipboard.SetText(text);
            return "Clipboard text updated.";
        });
    }

    [Tool("calc_evaluate",
          "utilities",
          "Evaluate arithmetic with +, -, *, /, %, ^ and parentheses. No code execution. Uses invariant decimal notation.")]
    public double Calculate(string expression) => Arithmetic.Evaluate(expression);
}

public static class Arithmetic
{
    public static double Evaluate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        if (expression.Length > 512)
        {
            throw new ArgumentException("Expression is too long.");
        }

        var parser = new Parser(expression);
        var value = parser.Expression();
        parser.End();
        if (!double.IsFinite(value))
        {
            throw new ArithmeticException("Result is not finite.");
        }

        return value;
    }

    private sealed class Parser(string text)
    {
        private int _position;

        private void White()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private bool Take(char token)
        {
            White();
            if (_position >= text.Length || text[_position] != token)
            {
                return false;
            }

            _position++;
            return true;
        }

        public void End()
        {
            White();
            if (_position != text.Length)
            {
                throw new ArgumentException("Invalid arithmetic expression.");
            }
        }

        public double Expression()
        {
            var value = Product();
            while (true)
            {
                if (Take('+'))
                {
                    value += Product();
                }
                else if (Take('-'))
                {
                    value -= Product();
                }
                else
                {
                    return value;
                }
            }
        }

        private double Product()
        {
            var value = Unary();
            while (true)
            {
                if (Take('*'))
                {
                    value *= Unary();
                }
                else if (Take('/'))
                {
                    value /= Unary();
                }
                else if (Take('%'))
                {
                    value %= Unary();
                }
                else
                {
                    return value;
                }
            }
        }

        private double Unary()
        {
            if (Take('+'))
            {
                return Unary();
            }

            if (Take('-'))
            {
                return -Unary();
            }

            return Power();
        }

        private double Power()
        {
            var value = Number();
            return Take('^') ? Math.Pow(value, Unary()) : value;
        }

        private double Number()
        {
            if (Take('('))
            {
                var value = Expression();
                if (!Take(')'))
                {
                    throw new ArgumentException("Missing closing parenthesis.");
                }

                return value;
            }
            White();
            var start = _position;
            while (_position < text.Length && (char.IsDigit(text[_position]) || text[_position] == '.'))
            {
                _position++;
            }

            if (start == _position || !double.TryParse(text[start.._position],
                                                       NumberStyles.AllowDecimalPoint,
                                                       CultureInfo.InvariantCulture,
                                                       out var number))
            {
                throw new ArgumentException("Expected a number.");
            }

            return number;
        }
    }
}
