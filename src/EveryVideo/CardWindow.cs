using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EveryVideo;

/// <summary>둥근 카드 모양 대화상자의 기본 창. 제목줄을 끌어 옮기고 X/Esc 로 닫는다.</summary>
public class CardWindow : Window
{
    public static readonly DependencyProperty CardIconProperty =
        DependencyProperty.Register(nameof(CardIcon), typeof(Geometry), typeof(CardWindow));

    public Geometry? CardIcon
    {
        get => (Geometry?)GetValue(CardIconProperty);
        set => SetValue(CardIconProperty, value);
    }

    static CardWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CardWindow), new FrameworkPropertyMetadata(typeof(CardWindow)));
    }

    public CardWindow()
    {
        SetResourceReference(StyleProperty, typeof(CardWindow));
        if (Application.Current?.MainWindow is { IsLoaded: true } main && !ReferenceEquals(main, this))
            Owner = main;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Title") is FrameworkElement title)
            title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        if (GetTemplateChild("PART_Close") is Button close)
            close.Click += (_, _) => Close();
    }
}
