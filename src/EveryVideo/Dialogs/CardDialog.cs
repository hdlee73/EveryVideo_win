using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EveryVideo.Dialogs;

public enum MessageKind { Info, Error, Question, Update }

/// <summary>MessageBox 대신 쓰는 둥근 카드 대화상자. 버튼 순서대로 0,1,2... 를 돌려주고, 닫으면 -1.</summary>
public sealed class CardDialog : CardWindow
{
    private int _result = -1;
    private TextBox? _input;

    private CardDialog(string title, string message, MessageKind kind, string[] buttons, string? inputValue, string? inputHint)
    {
        Title = title;
        CardIcon = (Geometry)FindResource(kind switch
        {
            MessageKind.Error => "IcError",
            MessageKind.Update => "IcUpdate",
            MessageKind.Question => "IcInfo",
            _ => "IcInfo",
        });

        var panel = new StackPanel { MinWidth = 340, MaxWidth = 520 };
        if (!string.IsNullOrEmpty(message))
            panel.Children.Add(new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21,
                Foreground = (Brush)FindResource("TextDim"), Margin = new Thickness(0, 0, 0, 6),
            });
        if (inputValue != null)
        {
            if (!string.IsNullOrEmpty(inputHint))
                panel.Children.Add(new TextBlock { Text = inputHint, Style = (Style)FindResource("Label") });
            _input = new TextBox { Text = inputValue, MinWidth = 420 };
            _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Finish(0); };
            panel.Children.Add(_input);
            Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            var index = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)FindResource(i == 0 ? "PrimaryButton" : "TextButton"),
                IsDefault = i == 0 && inputValue == null,
            };
            b.Click += (_, _) => Finish(index);
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        Content = panel;
    }

    private void Finish(int index)
    {
        _result = index;
        Close();
    }

    public static int Show(string title, string message, MessageKind kind = MessageKind.Info, params string[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { "확인" };
        var d = new CardDialog(title, message, kind, buttons, null, null);
        d.ShowDialog();
        return d._result;
    }

    public static bool Confirm(string title, string message, string ok = "확인", string cancel = "취소") =>
        Show(title, message, MessageKind.Question, ok, cancel) == 0;

    /// <summary>글자를 입력받는다. 취소하면 null.</summary>
    public static string? Prompt(string title, string message, string value = "", string? hint = null, string ok = "확인")
    {
        var d = new CardDialog(title, message, MessageKind.Question, new[] { ok, "취소" }, value, hint);
        d.ShowDialog();
        return d._result == 0 ? d._input!.Text : null;
    }
}
